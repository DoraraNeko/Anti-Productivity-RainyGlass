using System.Numerics;
using System.Runtime.InteropServices;

namespace RainyGlass;

[StructLayout(LayoutKind.Sequential)]
public struct Inst
{
    public Vector4 A;
    public Vector4 B;
    public Inst(Vector4 a, Vector4 b) { A = a; B = b; }
}

sealed class Drop
{
    public float X, Y, R, Vx, Vy, Seed, Stretch;
    public float PrevX, PrevY;
    public float TrailAcc, StallTimer;
    public bool Moving, Dead, Fresh;
}

/// <summary>
/// 窓ガラス上の水滴の CPU シミュレーション。
/// 大きい水滴は個別に動かし、小さな水滴と流れ跡は GPU 側の永続テクスチャにスタンプする。
/// </summary>
sealed class RainSim
{
    public readonly int W, H;
    public readonly float S;          // 1080p 基準のスケール
    public float Intensity = 1f;       // 雨量
    public bool Raining = true;

    public readonly List<Inst> DropInst = new();
    public readonly List<Inst> EraseInst = new();
    public readonly List<Inst> StampInst = new();

    readonly List<Drop> _drops = new();
    readonly Random _rng = new();
    readonly Dictionary<long, List<Drop>> _grid = new();
    readonly float _cell;
    float _accBig, _accSmall;

    const float BigHeight = 0.5f;
    const float SmallHeight = 0.5f;

    float RMove => 15f * S;   // これ以上の大きさで流れ始める
    float RMaxSpawn => 15.5f * S;

    public RainSim(int w, int h)
    {
        W = w; H = h;
        S = h / 1080f;
        _cell = 64f * S;
    }

    float Rand() => (float)_rng.NextDouble();
    float Rand(float a, float b) => a + (b - a) * Rand();
    float MegaPixels => W * H / 1_000_000f;

    public void Reset()
    {
        _drops.Clear();
        _accBig = _accSmall = 0;
    }

    /// <summary>起動直後にガラスが既に濡れている状態を作る。</summary>
    public void Prefill()
    {
        int nBig = (int)(MegaPixels * 330 * Intensity);
        for (int i = 0; i < nBig; i++) SpawnDrop(Rand(0, W), Rand(0, H), SpawnRadius(), false);
        int nSmall = (int)(MegaPixels * 7000 * Intensity);
        for (int i = 0; i < nSmall; i++) StampSmall(Rand(0, W), Rand(0, H), SmallRadius());
    }

    float SpawnRadius()
    {
        float u = Rand();
        return 2.5f * S + (RMaxSpawn - 2.5f * S) * MathF.Pow(u, 1.8f);
    }

    float SmallRadius() => S * (1.0f + 3.6f * MathF.Pow(Rand(), 2.0f));

    void SpawnDrop(float x, float y, float r, bool fresh)
    {
        _drops.Add(new Drop
        {
            X = x, Y = y, PrevX = x, PrevY = y, R = r,
            Seed = Rand(), Fresh = fresh,
            StallTimer = Rand(0.1f, 0.5f),
        });
        if (fresh) // 雨粒が当たった場所の細かい水滴は取り込まれる
            EraseInst.Add(new Inst(new Vector4(x, y, x, y), new Vector4(r * 1.05f, 0, 0, 0)));
    }

    void StampSmall(float x, float y, float r)
    {
        StampInst.Add(new Inst(new Vector4(x, y, x, y), new Vector4(r, SmallHeight, Rand(), 0)));
    }

    public void Update(float dt)
    {
        DropInst.Clear();
        EraseInst.Clear();
        StampInst.Clear();
        dt = Math.Clamp(dt, 0f, 1f / 20f);

        if (Raining)
        {
            _accBig += dt * MegaPixels * 14f * Intensity;
            _accSmall += dt * MegaPixels * 260f * Intensity;
            while (_accBig >= 1) { _accBig--; SpawnDrop(Rand(0, W), Rand(-20, H), SpawnRadius(), true); }
            while (_accSmall >= 1) { _accSmall--; StampSmall(Rand(0, W), Rand(0, H), SmallRadius()); }
        }

        foreach (var d in _drops) Step(d, dt);
        Collide();
        _drops.RemoveAll(d => d.Dead || d.Y - d.R * 4 > H || d.R < 0.8f * S);

        foreach (var d in _drops)
        {
            DropInst.Add(new Inst(new Vector4(d.X, d.Y, d.R, d.Stretch), new Vector4(BigHeight, d.Seed, 0, 0)));
            d.Fresh = false;
        }
    }

    void Step(Drop d, float dt)
    {
        d.PrevX = d.X; d.PrevY = d.Y;

        if (!d.Moving)
        {
            if (d.R > RMove)
            {
                float chance = ((d.R - RMove) / RMove * 5f + 0.4f) * dt;
                if (Rand() < chance) { d.Moving = true; d.Vy = 20f * S; }
            }
            d.Stretch += (0 - d.Stretch) * Math.Min(1, dt * 6);
            return;
        }

        float mass = d.R / RMove;
        float accel = 900f * S * (mass - 0.6f);
        d.Vy += accel * dt;
        d.Vy -= d.Vy * 2.2f * dt;

        // ガラス上の水滴は引っかかっては急に滑り出す (stick-slip)
        d.StallTimer -= dt;
        if (d.StallTimer <= 0)
        {
            d.StallTimer = Rand(0.08f, 0.6f);
            float k = Rand();
            if (k < 0.25f) d.Vy *= 0.15f;
            else if (k < 0.45f) d.Vy += 120f * S * mass;
            d.Vx += Rand(-1f, 1f) * 30f * S;
        }
        d.Vy = Math.Clamp(d.Vy, 0, 1100f * S);
        d.Vx *= MathF.Exp(-5f * dt);
        d.X += d.Vx * dt;
        d.Y += d.Vy * dt;

        float dist = MathF.Sqrt((d.X - d.PrevX) * (d.X - d.PrevX) + (d.Y - d.PrevY) * (d.Y - d.PrevY));
        // 流れ跡に水を残して少しずつ痩せる
        d.R -= dist * 0.0035f;

        float targetStretch = Math.Clamp(d.Vy / (d.R * 14f + 1e-3f), 0f, 0.6f);
        d.Stretch += (targetStretch - d.Stretch) * Math.Min(1, dt * 10);

        if (dist > 0.01f)
        {
            EraseInst.Add(new Inst(new Vector4(d.PrevX, d.PrevY, d.X, d.Y), new Vector4(d.R * 0.95f, 0, 0, 0)));
            float tw = d.R * 0.32f;
            StampInst.Add(new Inst(new Vector4(d.PrevX, d.PrevY - d.R * 0.4f, d.X, d.Y - d.R * 0.4f),
                                   new Vector4(tw, 0.45f, d.Seed, 1)));
        }

        d.TrailAcc += dist;
        float spacing = d.R * Rand(0.5f, 1.6f);
        if (d.TrailAcc > spacing)
        {
            d.TrailAcc = 0;
            if (Rand() < 0.7f)
                StampSmall(d.X + Rand(-0.3f, 0.3f) * d.R, d.Y - d.R * Rand(1.2f, 2.2f), d.R * Rand(0.12f, 0.32f));
        }

        if (d.R < RMove * 0.65f && d.Vy < 60f * S) d.Moving = false;
    }

    long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    void Collide()
    {
        foreach (var l in _grid.Values) l.Clear();
        foreach (var d in _drops)
        {
            var k = Key((int)MathF.Floor(d.X / _cell), (int)MathF.Floor(d.Y / _cell));
            if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<Drop>();
            list.Add(d);
        }

        foreach (var a in _drops)
        {
            if (a.Dead || !(a.Moving || a.Fresh)) continue;
            int cx = (int)MathF.Floor(a.X / _cell), cy = (int)MathF.Floor(a.Y / _cell);
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                if (!_grid.TryGetValue(Key(cx + ox, cy + oy), out var list)) continue;
                foreach (var b in list)
                {
                    if (b == a || b.Dead) continue;
                    float dx = b.X - a.X, dy = b.Y - a.Y;
                    float rr = (a.R + b.R) * 0.82f;
                    if (dx * dx + dy * dy > rr * rr) continue;
                    Merge(a, b);
                    if (a.Dead) goto next;
                }
            }
            next:;
        }
    }

    void Merge(Drop a, Drop b)
    {
        // 大きい方 (または動いている方) が小さい方を吸収する
        Drop keep = a, eat = b;
        if (!a.Moving && (b.Moving || b.R > a.R)) { keep = b; eat = a; }
        float ka = keep.R * keep.R, kb = eat.R * eat.R;
        float nr = MathF.Sqrt(ka + kb);
        if (!keep.Moving)
        {
            keep.X = (keep.X * ka + eat.X * kb) / (ka + kb);
            keep.Y = (keep.Y * ka + eat.Y * kb) / (ka + kb);
        }
        else
        {
            keep.X += (eat.X - keep.X) * kb / (ka + kb) * 0.5f;
            keep.Vy += 40f * S;
        }
        keep.R = Math.Min(nr, 40f * S);
        eat.Dead = true;
    }

    public void Clear()
    {
        _drops.Clear();
    }
}
