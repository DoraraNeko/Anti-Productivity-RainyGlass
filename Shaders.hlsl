// RainyGlass shaders
// 座標系はすべてピクセル単位 (y は下向き)。

cbuffer Globals : register(b0)
{
    float2 gScreen;     // 画面サイズ (px)
    float  gTime;
    float  gScale;      // 1080p を 1 とした解像度スケール
    float4 gMul;        // 減衰/乗算パス用の値
    float  gRefract;    // 屈折の強さ
    float  gBlurLod;    // 背景サンプリングのミップレベル
    float  gDarken;     // 縁の暗さ
    float  gPad;
};

struct Inst { float4 a; float4 b; };
StructuredBuffer<Inst> gInst : register(t0);

struct VSOut
{
    float4 pos   : SV_Position;
    float2 p     : TEXCOORD0;   // ローカル座標 (半径単位)
    float2 world : TEXCOORD1;   // ピクセル座標
    nointerpolation float4 a : TEXCOORD2;
    nointerpolation float4 b : TEXCOORD3;
};

float2 ToClip(float2 px) { return float2(px.x / gScreen.x * 2 - 1, 1 - px.y / gScreen.y * 2); }

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float VNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    float a = Hash21(i), b = Hash21(i + float2(1, 0));
    float c = Hash21(i + float2(0, 1)), d = Hash21(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// 水滴の輪郭のゆらぎ (静止した水滴ほど不規則な形になる)
float Wobble(float2 p, float seed, float amount)
{
    float ang = atan2(p.y, p.x);
    float w = 0.08 * sin(2 * ang + seed * 31.0)
            + 0.05 * sin(3 * ang + seed * 6.28)
            + 0.02 * sin(5 * ang + seed * 17.0);
    return 1 + w * amount;
}

// ---------------------------------------------------------------------------
// 大きい水滴 (CPU でシミュレーション)
// a = (x, y, r, stretch)  b = (heightScale, seed, -, -)
// ---------------------------------------------------------------------------
VSOut VS_Drop(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst d = gInst[iid];
    float2 c = float2(vid & 1, vid >> 1);
    float st = d.a.w;
    float m = 1.3;
    float2 lo = float2(-m, -m * (1 + st * 1.6));
    float2 hi = float2(m, m);
    float2 p = lerp(lo, hi, c);
    float2 px = d.a.xy + p * d.a.z;
    VSOut o;
    o.pos = float4(ToClip(px), 0, 1);
    o.p = p;
    o.world = px;
    o.a = d.a;
    o.b = d.b;
    return o;
}

float2 PS_Drop(VSOut i) : SV_Target
{
    float r = i.a.z;
    float st = i.a.w;
    float2 p = i.p;

    // 流れている水滴は上に尾を引く涙型、下側はやや重く膨らむ
    if (p.y < 0)
    {
        float t = saturate(-p.y / (1 + st * 1.6));
        p.y /= (1 + st * 1.6);
        p.x *= 1 + st * 1.4 * t * t;
    }
    else
    {
        p.y *= 1 - 0.06 * st;
        p.x *= 1 - 0.05 * st;
    }

    float wob = Wobble(p, i.b.y, saturate(1 - st * 1.5));
    float d = length(p) / wob;
    float edgePx = (1 - d) * r;          // 輪郭までの距離 (px)
    if (edgePx <= -0.5) discard;
    float h = sqrt(saturate(1 - d * d)) * r * i.b.x;
    h *= saturate(edgePx + 0.5);         // アンチエイリアス用に輪郭で 0 へ
    return float2(h, r);
}

// ---------------------------------------------------------------------------
// 線分インスタンス (小さな水滴のスタンプ / 流れ跡 / 消去)
// a = (x0, y0, x1, y1)  b = (radius, heightScale, seed, kind)
// ---------------------------------------------------------------------------
VSOut VS_Seg(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst d = gInst[iid];
    float2 A = d.a.xy, B = d.a.zw;
    float R = d.b.x * 1.25 + 1.5;
    float2 dir = B - A;
    float L = length(dir);
    dir = L > 1e-4 ? dir / L : float2(0, 1);
    float2 nrm = float2(-dir.y, dir.x);
    float2 c = float2(vid & 1, vid >> 1);
    float2 px = A + dir * lerp(-R, L + R, c.y) + nrm * lerp(-R, R, c.x);
    VSOut o;
    o.pos = float4(ToClip(px), 0, 1);
    o.p = 0;
    o.world = px;
    o.a = d.a;
    o.b = d.b;
    return o;
}

float SegDist(float2 p, float2 a, float2 b, out float t)
{
    float2 ab = b - a;
    float l2 = dot(ab, ab);
    t = l2 > 1e-6 ? saturate(dot(p - a, ab) / l2) : 0;
    return length(p - (a + ab * t));
}

// 小さな水滴 (kind 0 → R チャンネル) と流れ跡 (kind 1 → G チャンネル)
float2 PS_Stamp(VSOut i) : SV_Target
{
    float R = i.b.x;
    float t;
    float dist = SegDist(i.world, i.a.xy, i.a.zw, t);
    float h;
    if (i.b.w < 0.5)
    {
        float2 rel = (i.world - i.a.xy) / max(R, 0.5);
        float d = length(rel) / Wobble(rel, i.b.z, saturate(R / (6 * gScale)));
        float edgePx = (1 - d) * R;
        if (edgePx <= -0.5) discard;
        h = sqrt(saturate(1 - d * d)) * R * i.b.y * saturate(edgePx + 0.5);
        return float2(h, 0);
    }
    else
    {
        // 流れ跡: 幅をノイズで揺らして、所々で途切れた水の筋にする
        float n = VNoise(i.world * (0.045 / gScale) + i.b.z * 37.0);
        float w = R * lerp(0.35, 1.0, n);
        float e = (w - dist);
        if (e <= -0.5) discard;
        float q = dist / max(w, 0.5);
        h = sqrt(saturate(1 - q * q)) * R * i.b.y * saturate(e + 0.5);
        return float2(0, h);
    }
}

// 消去 (乗算ブレンド): 流れる水滴が通った場所の小さな水滴を拭い取る
float4 PS_Erase(VSOut i) : SV_Target
{
    float R = i.b.x;
    float t;
    float dist = SegDist(i.world, i.a.xy, i.a.zw, t);
    float m = smoothstep(R, R * 0.75, dist);
    return float4(1 - m, 1, 1, 1);   // 流れ跡 (G) は残す
}

// ---------------------------------------------------------------------------
// フルスクリーンパス
// ---------------------------------------------------------------------------
float4 VS_Full(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PS_Mul(float4 pos : SV_Position) : SV_Target
{
    return gMul;
}

Texture2D<float2> tBig   : register(t0);
Texture2D<float2> tSmall : register(t1);
Texture2D<float4> tBg    : register(t2);
SamplerState sLinear     : register(s0);

float HeightAt(int2 px)
{
    float b = tBig.Load(int3(px, 0)).x;
    float2 s = tSmall.Load(int3(px, 0));
    return max(b, max(s.x, s.y));
}

float4 PS_Composite(float4 pos : SV_Position) : SV_Target
{
    int2 px = int2(pos.xy);
    float2 big = tBig.Load(int3(px, 0));
    float2 sm = tSmall.Load(int3(px, 0));
    float hs = max(sm.x, sm.y);
    float h = max(big.x, hs);
    float alpha = saturate(h / 0.6);
    if (alpha <= 0.002) return 0;

    float hl = HeightAt(px + int2(-1, 0));
    float hr = HeightAt(px + int2(1, 0));
    float hu = HeightAt(px + int2(0, -1));
    float hd = HeightAt(px + int2(0, 1));
    float2 g = float2(hr - hl, hd - hu) * 0.5;
    float3 n = normalize(float3(-g, 1));

    // 水滴はレンズとして働き、背後の像を反転・縮小して映す
    float lensR = big.x >= hs ? big.y : 3.0 * gScale;
    float2 offs = -n.xy * gRefract * (lensR * 1.4 + 2.0 * gScale);
    float2 uv = (pos.xy + offs) / gScreen;
    float lod = gBlurLod + saturate(lensR / (20 * gScale)) * 0.8;
    float3 col = tBg.SampleLevel(sLinear, uv, lod).rgb;

    float slope = 1 - n.z;                 // 中心 0 → 輪郭 1
    float rim = smoothstep(0.04, 0.55, slope);
    float bottom = saturate(n.y * 1.1 + 0.25);

    // 水が光を集めて内側は明るく、輪郭は全反射で暗くなる (下側ほど濃く太い)
    // レンズで反転した空の光が下側に集まる
    col = col * (1.04 + 0.10 * saturate(n.y * 2 + 0.3)) + 0.04 * (1 - rim);
    float dark = rim * gDarken * lerp(0.25, 1.0, bottom * bottom);
    col *= 1 - dark;

    // 上側の縁に空の映り込み
    float top = saturate(-n.y);
    col += rim * top * 0.10;

    // スペキュラ
    float3 L = normalize(float3(-0.35, -0.75, 0.55));
    float3 H = normalize(L + float3(0, 0, 1));
    float spec = pow(saturate(dot(n, H)), 90) * 0.55;
    col += spec;

    col = saturate(col);
    return float4(col * alpha, alpha);
}
