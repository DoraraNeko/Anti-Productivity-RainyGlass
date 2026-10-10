"""APRG のアイコン (Assets/APRG.ico と Assets/icon-256.png) を生成する。

雨の窓ガラスに大きな水滴が1つ。アプリのシェーダーと同じく、高さマップから法線を求めて
屈折・縁の影・ハイライトを付ける。小さいサイズ用には細部を省いた版を描く。

使い方: python tools/make_icon.py
"""
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "Assets"

TOP = np.array([0.09, 0.14, 0.30])
BOTTOM = np.array([0.24, 0.42, 0.62])


def background(x, y):
    col = TOP + (BOTTOM - TOP) * np.clip(y, 0, 1)[..., None]
    # 窓の外の街明かりのボケ
    for bx, by, br, c, a in [
        (0.25, 0.78, 0.12, (1.0, 0.75, 0.40), 0.35),
        (0.78, 0.70, 0.10, (0.45, 0.80, 0.95), 0.30),
        (0.62, 0.88, 0.08, (1.0, 0.55, 0.40), 0.30),
        (0.15, 0.40, 0.07, (0.90, 0.90, 0.80), 0.18),
    ]:
        d = np.hypot(x - bx, y - by) / br
        w = np.clip(1.2 - d, 0, 1) ** 1.5 * a
        col = col * (1 - w[..., None]) + np.array(c) * w[..., None]
    return col


def drop_height(x, y, cx, cy, r, stretch):
    px, py = (x - cx) / r, (y - cy) / r
    up = py < 0
    t = np.clip(-py / (1 + stretch * 1.6), 0, 1)
    py2 = np.where(up, py / (1 + stretch * 1.6), py * (1 - 0.06 * stretch))
    px2 = np.where(up, px * (1 + stretch * 1.4 * t * t), px)
    d = np.hypot(px2, py2)
    return np.sqrt(np.clip(1 - d * d, 0, 1)) * r * 0.5


def render(n, simple):
    y, x = (np.mgrid[0:n, 0:n] + 0.5) / n

    # 角丸の正方形
    margin, rad = 0.03, 0.21
    qx = np.abs(x - 0.5) - (0.5 - margin - rad)
    qy = np.abs(y - 0.5) - (0.5 - margin - rad)
    sd = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - rad
    alpha = np.clip(0.5 - sd * n, 0, 1)

    bg = background(x, y)

    cx, cy, r = 0.50, 0.60, (0.29 if simple else 0.25)
    h = drop_height(x, y, cx, cy, r, 0.38)
    lens_r = np.where(h > 0, r, 0.0)
    if not simple:
        for sx, sy, sr in [(0.22, 0.24, 0.055), (0.80, 0.30, 0.045), (0.78, 0.86, 0.035), (0.20, 0.62, 0.03)]:
            hs = drop_height(x, y, sx, sy, sr, 0.0)
            lens_r = np.where(hs > h, sr, lens_r)
            h = np.maximum(h, hs)

    hp = h * n
    gy, gx = np.gradient(hp)
    nrm = np.stack([-gx, -gy, np.ones_like(hp)], -1)
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
    nx, ny, nz = nrm[..., 0], nrm[..., 1], nrm[..., 2]

    # 屈折: 背後の景色を反転して映す
    k = lens_r * 1.4
    col = background(x - nx * k, y - ny * k)
    # 水滴の中は光を集めて明るい (背景との差を付けて小さいサイズでも見分けやすく)
    col = col * 0.55 + np.array([0.55, 0.76, 0.93]) * 0.45
    slope = 1 - nz
    rim = np.clip((slope - 0.04) / 0.5, 0, 1)
    rim = rim * rim * (3 - 2 * rim)
    bottom = np.clip(ny * 1.1 + 0.25, 0, 1)
    col = col * (1.06 + 0.12 * np.clip(ny * 2 + 0.3, 0, 1))[..., None] + 0.05 * (1 - rim)[..., None]
    col = col * (1 - rim * 0.85 * (0.25 + 0.75 * bottom * bottom))[..., None]
    col = col + (rim * np.clip(-ny, 0, 1) * 0.12)[..., None]
    light = np.array([-0.35, -0.75, 0.55])
    half = light + np.array([0, 0, 1.0])
    half /= np.linalg.norm(half)
    spec = np.clip(nrm @ half, 0, 1) ** 60 * 0.9
    col = col + spec[..., None]

    # 小さいサイズでも水滴だと分かるよう、左上に白い光点を足す
    glint = np.hypot((x - (cx - r * 0.38)) / 0.045, (y - (cy - r * 0.30)) / 0.065)
    col = col + (np.clip(1 - glint, 0, 1) ** 0.7 * 0.85)[..., None] * (h > 0)[..., None]

    cover = np.clip(hp / 0.8, 0, 1)[..., None]
    rgb = np.clip(bg * (1 - cover) + np.clip(col, 0, 1) * cover, 0, 1)
    img = np.dstack([rgb, alpha[..., None]])
    return Image.fromarray((img * 255 + 0.5).astype(np.uint8), "RGBA")


def main():
    OUT.mkdir(exist_ok=True)
    detailed = render(1024, simple=False)
    simple = render(512, simple=True)
    frames = []
    for s in [16, 20, 24, 32, 40, 48, 64, 128, 256]:
        src = simple if s <= 32 else detailed
        frames.append(src.resize((s, s), Image.LANCZOS))
    frames[-1].save(OUT / "APRG.ico", format="ICO", sizes=[f.size for f in frames], append_images=frames[:-1])
    frames[-1].save(OUT / "icon-256.png")
    print("wrote", OUT / "APRG.ico")


if __name__ == "__main__":
    main()
