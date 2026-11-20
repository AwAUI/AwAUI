using AwAUI.Render.Rhi;

namespace AwAUI.Rhi.Metal;

/// <summary>
/// 固定管线的 MSL 源码（design/03 D10：手写、运行时由系统编译）。实例与常量的布局见 <see cref="GpuQuadInstance"/>、<see cref="GpuQuadUniforms"/>。
/// 覆盖率与混合公式与软件后端（AwAUI.Render）一致：矩形是精确面积覆盖率；圆角按到椭圆距离的半平面覆盖率；
/// 混合模式照搬 W3C Compositing 1 的可分离公式，覆盖率在目标与结果之间线性插值。
/// </summary>
internal static class MetalShaders
{
    public const string VertexFunction = "quad_vertex";
    public const string FragmentFunction = "quad_fragment";

    public const string Source = """
#include <metal_stdlib>
using namespace metal;

struct QuadInstance {
    float4 bounds;
    float4 shape;
    float4 radiiX;
    float4 radiiY;
    float4 color;
    float4 paint0;
    float4 paint1;
    float4 tex;
    float4 clip;
    float4 extra;
    uint4 kinds;
};

struct ClipShape {
    float4 rect;
    float4 radiiX;
    float4 radiiY;
    float4 flags;
};

struct QuadUniforms {
    float4 target;
    float4 dst;
};

struct VOut {
    float4 position [[position]];
    uint instance [[flat]];
};

vertex VOut quad_vertex(uint vid [[vertex_id]], uint iid [[instance_id]],
                        const device QuadInstance* instances [[buffer(0)]],
                        constant QuadUniforms& u [[buffer(1)]])
{
    float4 b = instances[iid].bounds;
    float x = (vid & 1) ? b.z : b.x;
    float y = (vid & 2) ? b.w : b.y;
    VOut o;
    o.position = float4((x - u.target.x) * u.target.z - 1.0, 1.0 - (y - u.target.y) * u.target.w, 0.0, 1.0);
    o.instance = iid;
    return o;
}

// ---------------------------------------------------------------- 覆盖率

// 像素 [p-0.5, p+0.5]² 与矩形 r 的重叠面积（与软件后端 FillRect 相同）
static float rectCoverage(float4 r, float2 p)
{
    float2 lo = max(p - 0.5, r.xy);
    float2 hi = min(p + 0.5, r.zw);
    float2 o = max(hi - lo, 0.0);
    return o.x * o.y;
}

// 单位像素被半平面 { x : n·(x − c) ≤ s } 覆盖的精确面积；n 为单位向量，s 为像素中心到边界的有符号距离（内部为正）
static float halfPlaneCoverage(float2 n, float s)
{
    float a = max(abs(n.x), abs(n.y));
    float b = min(abs(n.x), abs(n.y));
    float h = 0.5 * (a + b);
    if (s >= h) return 1.0;
    if (s <= -h) return 0.0;
    float k = 0.5 * (a - b);
    if (abs(s) <= k || b < 1e-6) return clamp(0.5 + s / max(a, 1e-6), 0.0, 1.0);
    float t = h - abs(s);
    float corner = t * t / (2.0 * a * b);
    return s > 0.0 ? 1.0 - corner : corner;
}

static float2 cornerRadius(float4 r, float4 rx, float4 ry, float2 p, thread bool& left, thread bool& top)
{
    left = p.x < (r.x + r.z) * 0.5;
    top = p.y < (r.y + r.w) * 0.5;
    if (top) return left ? float2(rx.x, ry.x) : float2(rx.y, ry.y);
    return left ? float2(rx.w, ry.w) : float2(rx.z, ry.z);
}

static float roundRectCoverage(float4 r, float4 rx, float4 ry, float2 p)
{
    float base = rectCoverage(r, p);
    if (base <= 0.0) return 0.0;
    bool left, top;
    float2 rad = cornerRadius(r, rx, ry, p, left, top);
    if (rad.x <= 0.0 || rad.y <= 0.0) return base;
    float2 c = float2(left ? r.x + rad.x : r.z - rad.x, top ? r.y + rad.y : r.w - rad.y);
    float2 o = p - c;
    o = float2(left ? -o.x : o.x, top ? -o.y : o.y);   // 指向角外为正
    if (o.x <= 0.0 || o.y <= 0.0) return base;
    // 到椭圆的近似有符号距离（圆时精确）与法线
    float2 g = o / (rad * rad);
    float k1 = length(o / rad);
    float k2 = length(g);
    if (k2 <= 0.0) return base;
    float dist = k1 * (k1 - 1.0) / k2;
    float2 n = g / k2;
    return min(base, halfPlaneCoverage(n, -dist));
}

static float applyAliasing(float cov, uint paintBits)
{
    return (paintBits & 256u) != 0u ? (cov >= 0.5 ? 1.0 : 0.0) : cov;
}

// ---------------------------------------------------------------- 阴影（解析高斯模糊）

// Abramowitz–Stegun 7.1.26，最大误差 1.5e-7
static float erfApprox(float x)
{
    float a = abs(x);
    float t = 1.0 / (1.0 + 0.3275911 * a);
    float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-a * a);
    return x < 0.0 ? -y : y;
}

// 标准差 sigma 的高斯分布在 (-inf, x] 上的积分
static float gaussCdf(float x, float sigma)
{
    return 0.5 + 0.5 * erfApprox(x * (0.70710678 / sigma));
}

// 圆角矩形在高度 y 处的水平范围
static float2 roundRectSpan(float4 r, float4 rx, float4 ry, float y)
{
    float l = r.x, rr = r.z;
    if (rx.x > 0.0 && ry.x > 0.0 && y < r.y + ry.x) {
        float d = (r.y + ry.x - y) / ry.x;
        l = r.x + rx.x * (1.0 - sqrt(max(0.0, 1.0 - d * d)));
    } else if (rx.w > 0.0 && ry.w > 0.0 && y > r.w - ry.w) {
        float d = (y - (r.w - ry.w)) / ry.w;
        l = r.x + rx.w * (1.0 - sqrt(max(0.0, 1.0 - d * d)));
    }
    if (rx.y > 0.0 && ry.y > 0.0 && y < r.y + ry.y) {
        float d = (r.y + ry.y - y) / ry.y;
        rr = r.z - rx.y * (1.0 - sqrt(max(0.0, 1.0 - d * d)));
    } else if (rx.z > 0.0 && ry.z > 0.0 && y > r.w - ry.z) {
        float d = (y - (r.w - ry.z)) / ry.z;
        rr = r.z - rx.z * (1.0 - sqrt(max(0.0, 1.0 - d * d)));
    }
    return float2(l, rr);
}

// [y0, y1] 上按 n 段分段常数积分：∑ 横向覆盖(y_k) · 纵向高斯权重
static float blurBand(float4 r, float4 rx, float4 ry, float sigma, float2 p, float y0, float y1, int n)
{
    if (y1 <= y0) return 0.0;
    float step = (y1 - y0) / float(n);
    float sum = 0.0;
    float prev = gaussCdf(p.y - y0, sigma);
    for (int i = 0; i < n; i++) {
        float ya = y0 + step * float(i);
        float yb = ya + step;
        float next = gaussCdf(p.y - yb, sigma);
        float2 span = roundRectSpan(r, rx, ry, 0.5 * (ya + yb));
        float h = gaussCdf(p.x - span.x, sigma) - gaussCdf(p.x - span.y, sigma);
        sum += h * (prev - next);
        prev = next;
    }
    return sum;
}

// 圆角矩形覆盖率与各向同性高斯核的卷积：直边部分在 y 方向精确积分，圆角带按分段常数积分
static float blurredRoundRect(float4 r, float4 rx, float4 ry, float sigma, float2 p)
{
    float reach = 4.0 * sigma;
    float y0 = max(r.y, p.y - reach), y1 = min(r.w, p.y + reach);
    if (y1 <= y0) return 0.0;
    // 离左右两侧的圆角列都超过 4σ：横向因子恒为 1，只剩纵向的闭式积分
    if (p.x - reach > r.x + max(rx.x, rx.w) && p.x + reach < r.z - max(rx.y, rx.z))
        return clamp(gaussCdf(p.y - r.y, sigma) - gaussCdf(p.y - r.w, sigma), 0.0, 1.0);
    float m0 = min(r.y + max(ry.x, ry.y), r.w);
    float m1 = max(r.w - max(ry.z, ry.w), m0);
    float sum = 0.0;
    sum += blurBand(r, rx, ry, sigma, p, y0, min(y1, m0), 12);
    float a = max(y0, m0), b = min(y1, m1);
    if (b > a) {
        float h = gaussCdf(p.x - r.x, sigma) - gaussCdf(p.x - r.z, sigma);
        sum += h * (gaussCdf(p.y - a, sigma) - gaussCdf(p.y - b, sigma));
    }
    sum += blurBand(r, rx, ry, sigma, p, max(y0, m1), y1, 12);
    return clamp(sum, 0.0, 1.0);
}

static float shadowShape(QuadInstance q, float2 p)
{
    float sigma = q.extra.x;
    return sigma < 0.3 ? roundRectCoverage(q.shape, q.radiiX, q.radiiY, p)
                       : blurredRoundRect(q.shape, q.radiiX, q.radiiY, sigma, p);
}

// ---------------------------------------------------------------- 画刷

static float applySpread(float t, uint spread)
{
    if (spread == 1u) return t - floor(t);
    if (spread == 2u) {
        float m = fmod(t, 2.0);
        if (m < 0.0) m += 2.0;
        return m > 1.0 ? 2.0 - m : m;
    }
    return clamp(t, 0.0, 1.0);
}

// 双点锥形渐变（QuadPaint.TwoPointConical）：取 r0 + t·dr ≥ 0 的最大根；无解返回 false（像素透明）。
// 不用 NaN 表示无解：着色器按快速数学编译，NaN 的比较与 isnan 不可靠。
static bool twoPointConical(float2 q, float4 e, float a, thread float& t)
{
    float b = dot(q, e.xy) + e.z * e.w;
    float c = dot(q, q) - e.z * e.z;
    if (abs(a) < 1e-5) {
        if (b == 0.0) return false;
        t = c / (2.0 * b);
        return e.z + t * e.w >= 0.0;
    }
    float disc = b * b - a * c;
    if (disc < 0.0) return false;
    float sq = sqrt(disc);
    float t1 = (b + sq) / a, t2 = (b - sq) / a;
    float hi = max(t1, t2), lo = min(t1, t2);
    if (e.z + hi * e.w >= 0.0) { t = hi; return true; }
    if (e.z + lo * e.w >= 0.0) { t = lo; return true; }
    return false;
}

static float4 gradientColor(QuadInstance q, float2 p, uint kind, uint spread, texture2d<float> lut)
{
    float3 hp = float3(p, 1.0);
    float qx = dot(q.paint0.xyz, hp);
    float t;
    if (kind == 1u) {
        t = applySpread(qx, spread);
    } else if (kind == 2u) {
        float qy = dot(q.paint1.xyz, hp);
        t = applySpread(sqrt(qx * qx + qy * qy), spread);
    } else if (kind == 4u) {
        float qy = dot(q.paint1.xyz, hp);
        float tc = 0.0;
        if (!twoPointConical(float2(qx, qy), q.extra, q.paint1.w, tc)) return float4(0.0);
        t = applySpread(tc, spread);
    } else {
        float qy = dot(q.paint1.xyz, hp);
        const float twoPi = 6.28318530718;
        float a = fmod(atan2(qy, qx) - q.paint1.w, twoPi);
        if (a < 0.0) a += twoPi;
        t = clamp(a / twoPi, 0.0, 1.0);
    }
    uint row = uint(q.paint0.w);
    float f = t * 255.0;
    int k = int(f);
    if (k >= 255) return lut.read(uint2(255, row));
    if (k < 0) return lut.read(uint2(0, row));
    float4 c0 = lut.read(uint2(k, row));
    float4 c1 = lut.read(uint2(k + 1, row));
    return c0 + (c1 - c0) * (f - float(k));
}

// 位图采样（QuadPaint.Image，与软件后端 ImageShader 相同）：(u, v) = (paint0·(p,1), paint1·(p,1)) 为纹素坐标；
// extra = 纹素下标的钳制范围 (x0, y0, x1, y1)（含两端）；mode 0 = 最近，1 = 双线性；paint1.w = 不透明度。
// 下标先在浮点域钳制再转整数（巨大或越界的坐标也只会读到钳制范围内的纹素）。
static float4 imageColor(QuadInstance q, float2 p, uint mode, texture2d<float> img)
{
    float3 hp = float3(p, 1.0);
    float2 uv = float2(dot(q.paint0.xyz, hp), dot(q.paint1.xyz, hp));
    float2 lo = q.extra.xy, hi = q.extra.zw;
    float4 c;
    if (mode == 0u) {
        c = img.read(uint2(clamp(floor(uv), lo, hi)));
    } else {
        float2 f = uv - 0.5;
        float2 i0 = floor(f);
        float2 t = clamp(f - i0, 0.0, 1.0);
        uint2 a = uint2(clamp(i0, lo, hi));
        uint2 b = uint2(clamp(i0 + 1.0, lo, hi));
        float4 c00 = img.read(uint2(a.x, a.y));
        float4 c10 = img.read(uint2(b.x, a.y));
        float4 c01 = img.read(uint2(a.x, b.y));
        float4 c11 = img.read(uint2(b.x, b.y));
        float4 top = c00 + (c10 - c00) * t.x;
        float4 bottom = c01 + (c11 - c01) * t.x;
        c = top + (bottom - top) * t.y;
    }
    return c * q.paint1.w;
}

// ---------------------------------------------------------------- 混合（W3C Compositing 1，可分离模式）

static float hardLight(float cs, float cb) { return cs <= 0.5 ? cb * 2.0 * cs : cb + (2.0 * cs - 1.0) - cb * (2.0 * cs - 1.0); }

static float softLight(float cs, float cb)
{
    if (cs <= 0.5) return cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb);
    float d = cb <= 0.25 ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);
    return cb + (2.0 * cs - 1.0) * (d - cb);
}

static float blendChannel(float cs, float cb, float sa, float da, uint mode)
{
    float Cs = sa > 0.0 ? cs / sa : 0.0;
    float Cb = da > 0.0 ? cb / da : 0.0;
    float b;
    switch (mode) {
        case 6u: b = Cs * Cb; break;                                     // Multiply
        case 7u: b = Cs + Cb - Cs * Cb; break;                           // Screen
        case 8u: b = hardLight(Cb, Cs); break;                           // Overlay
        case 9u: b = min(Cs, Cb); break;                                 // Darken
        case 10u: b = max(Cs, Cb); break;                                // Lighten
        case 11u: b = Cb <= 0.0 ? 0.0 : (Cs >= 1.0 ? 1.0 : min(1.0, Cb / (1.0 - Cs))); break;          // ColorDodge
        case 12u: b = Cb >= 1.0 ? 1.0 : (Cs <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - Cb) / Cs)); break;    // ColorBurn
        case 13u: b = hardLight(Cs, Cb); break;                          // HardLight
        case 14u: b = softLight(Cs, Cb); break;                          // SoftLight
        case 15u: b = abs(Cs - Cb); break;                               // Difference
        case 16u: b = Cs + Cb - 2.0 * Cs * Cb; break;                    // Exclusion
        default: b = Cs; break;
    }
    return cs * (1.0 - da) + cb * (1.0 - sa) + sa * da * b;
}

static float4 blendModes(float4 s, float4 d, uint mode)
{
    switch (mode) {
        case 0u: return s + d * (1.0 - s.w);           // SrcOver
        case 1u: return s;                              // Src
        case 2u: return float4(0.0);                    // Clear
        case 3u: return d * s.w;                        // DstIn
        case 4u: return d * (1.0 - s.w);                // DstOut
        case 5u: return min(float4(1.0), s + d);        // Plus
        default: break;
    }
    float sa = s.w, da = d.w;
    return float4(blendChannel(s.x, d.x, sa, da, mode),
                  blendChannel(s.y, d.y, sa, da, mode),
                  blendChannel(s.z, d.z, sa, da, mode),
                  sa + da - sa * da);
}

// ---------------------------------------------------------------- 滤镜（QuadShape.Filter，与软件后端的 FilterExecutor 逐步相同）

static float4 filterRead(texture2d<float> t, int2 c, float4 valid)
{
    if (c.x < int(valid.x) || c.y < int(valid.y) || c.x >= int(valid.z) || c.y >= int(valid.w)) return float4(0.0);
    return t.read(uint2(c));
}

static float4 filterPass(QuadInstance q, float2 p, int2 ip, texture2d<float> src, texture2d<float> src2)
{
    uint op = q.kinds.z;
    int2 off = int2(q.tex.xy);
    if (op == 0u) {
        float4 c = filterRead(src, ip + off, q.radiiY);
        float a = c.w;
        float4 u = a > 0.0 ? float4(c.xyz / a, a) : float4(0.0, 0.0, 0.0, a);
        float nr = clamp(dot(q.paint0, u) + q.extra.x, 0.0, 1.0);
        float ng = clamp(dot(q.paint1, u) + q.extra.y, 0.0, 1.0);
        float nb = clamp(dot(q.shape, u) + q.extra.z, 0.0, 1.0);
        float na = clamp(dot(q.radiiX, u) + q.extra.w, 0.0, 1.0);
        return float4(nr * na, ng * na, nb * na, na);
    }
    if (op == 1u) {
        int k = int(q.extra.x);
        float4 acc = float4(0.0);
        for (int b = 0; b < k; b++)
            for (int a = 0; a < k; a++)
                acc += filterRead(src, ip * k + int2(a, b) + off, q.radiiY);
        return acc / float(k * k);
    }
    if (op == 2u || op == 3u) {
        float inv = q.extra.x;
        int r = int(q.extra.y);
        int2 axis = op == 2u ? int2(1, 0) : int2(0, 1);
        int2 c = ip + off;
        float4 acc = float4(0.0);
        for (int t = r; t >= 1; t--) {
            float w = exp(-float(t * t) * inv);
            acc += w * (filterRead(src, c - axis * t, q.radiiY) + filterRead(src, c + axis * t, q.radiiY));
        }
        acc += filterRead(src, c, q.radiiY);
        return acc * q.extra.z;
    }
    if (op == 4u) {
        float k = q.extra.x;
        float2 uv = p / k - 0.5;
        float2 f = floor(uv);
        float2 t = uv - f;
        int2 i0 = int2(f) + off;
        float4 c00 = filterRead(src, i0, q.radiiY);
        float4 c10 = filterRead(src, i0 + int2(1, 0), q.radiiY);
        float4 c01 = filterRead(src, i0 + int2(0, 1), q.radiiY);
        float4 c11 = filterRead(src, i0 + int2(1, 1), q.radiiY);
        float4 top = c00 + (c10 - c00) * t.x;
        float4 bottom = c01 + (c11 - c01) * t.x;
        return top + (bottom - top) * t.y;
    }
    float4 c = filterRead(src, ip + off, q.radiiY);
    float s = filterRead(src2, ip + int2(q.paint0.xy), q.radiiX).w;
    return c + q.color * (s * (1.0 - c.w));
}

// ---------------------------------------------------------------- 片元

fragment float4 quad_fragment(VOut in [[stage_in]],
                              const device QuadInstance* instances [[buffer(0)]],
                              constant QuadUniforms& u [[buffer(1)]],
                              const device ClipShape* clips [[buffer(2)]],
                              texture2d<float> coverageTex [[texture(0)]],
                              texture2d<float> lutTex [[texture(1)]],
                              texture2d<float> clipTex [[texture(2)]],
                              texture2d<float> dstTex [[texture(3)]])
{
    QuadInstance q = instances[in.instance];
    float2 p = in.position.xy + u.target.xy;
    int2 ip = int2(floor(p));
    uint shape = q.kinds.x;
    uint paintBits = q.kinds.y;
    float cov = 1.0;
    float4 src;

    if (shape == 7u) {
        return filterPass(q, p, ip, coverageTex, lutTex);
    }
    if (shape == 4u) {
        src = coverageTex.read(uint2(ip + int2(q.tex.xy)));
        cov = q.color.x;
    } else {
        switch (shape) {
            case 0u: cov = rectCoverage(q.shape, p); break;
            case 1u: cov = applyAliasing(roundRectCoverage(q.shape, q.radiiX, q.radiiY, p), paintBits); break;
            case 2u: {
                float lw = q.tex.x - q.shape.x, lh = q.tex.y - q.shape.y;
                float4 irx = max(q.radiiX - lw, 0.0), iry = max(q.radiiY - lh, 0.0);
                float outer = roundRectCoverage(q.shape, q.radiiX, q.radiiY, p);
                float inner = (q.tex.z > q.tex.x && q.tex.w > q.tex.y) ? roundRectCoverage(q.tex, irx, iry, p) : 0.0;
                cov = applyAliasing(clamp(outer - inner, 0.0, 1.0), paintBits);
                break;
            }
            case 3u: cov = coverageTex.read(uint2(ip + int2(q.tex.xy))).r; break;
            case 5u: {
                // 外阴影：被盒子完全盖住的像素不必求模糊
                float box = roundRectCoverage(q.paint0, q.paint1, q.tex, p);
                cov = box >= 1.0 ? 0.0 : shadowShape(q, p) * (1.0 - box);
                break;
            }
            default: {
                float box = roundRectCoverage(q.paint0, q.paint1, q.tex, p);
                cov = box <= 0.0 ? 0.0 : (1.0 - shadowShape(q, p)) * box;
                break;
            }
        }
        uint paint = paintBits & 15u;
        if (paint == 0u) src = q.color;
        else if (paint == 5u) src = imageColor(q, p, (paintBits >> 4) & 15u, lutTex);
        else src = gradientColor(q, p, paint, (paintBits >> 4) & 15u, lutTex);
    }
    if (q.clip.z != 0.0) cov *= clipTex.read(uint2(ip + int2(q.clip.xy))).r;
    if (q.clip.w != 0.0) {
        ClipShape c = clips[uint(q.clip.w) - 1u];
        float cc = roundRectCoverage(c.rect, c.radiiX, c.radiiY, p);
        cov *= c.flags.x != 0.0 ? (cc >= 0.5 ? 1.0 : 0.0) : cc;
    }

    uint blend = q.kinds.w;
    if (blend == 0u) return src * cov;
    float4 d = dstTex.read(uint2(ip - int2(u.dst.xy)));
    float4 r = blendModes(src, d, blend - 1u);
    return d + (r - d) * cov;
}
""";
}
