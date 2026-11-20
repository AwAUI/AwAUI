using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>滤镜执行计划中一步的种类（软件与 GPU 后端按同一语义执行，见 <see cref="FilterPlan"/>）。</summary>
internal enum FilterPassKind : byte
{
    /// <summary>颜色矩阵：非预乘颜色 × 4×5 矩阵，钳制到 [0, 1] 后再预乘。</summary>
    ColorMatrix,
    /// <summary>k×k 方块平均降采样（输出在 1/k 分辨率的坐标系中，块按绝对坐标对齐到 k 的倍数）。</summary>
    Downsample,
    /// <summary>水平方向的高斯卷积（半径 <see cref="FilterPass.Radius"/>，权重 exp(−t²/2σ²) 归一化）。</summary>
    BlurX,
    /// <summary>垂直方向的高斯卷积。</summary>
    BlurY,
    /// <summary>从 1/k 分辨率双线性放大回设备像素：纹素中心在 (i + 0.5)·k。</summary>
    Upsample,
    /// <summary>投影合成：out = I + 颜色 × S.a(p − d) × (1 − I.a)，I 为 <see cref="FilterPass.Input"/>，S 为 <see cref="FilterPass.Input2"/>。</summary>
    DropShadow,
}

/// <summary>计划中的一张中间图像：设备像素（或 1/<see cref="Scale"/> 分辨率）坐标中的矩形。第 0 张是滤镜的输入。</summary>
internal readonly struct FilterImage
{
    public FilterImage(PixelRect rect, int scale) { Rect = rect; Scale = scale; }
    public PixelRect Rect { get; }
    public int Scale { get; }
}

/// <summary>计划中的一步：读取 <see cref="Input"/>（与 <see cref="Input2"/>），写出图像 <see cref="Output"/>。</summary>
internal readonly struct FilterPass
{
    public FilterPass(FilterPassKind kind, int input, int input2, int output, int scale, float sigma, int radius,
        float[]? matrix, Vector4 color, int dx, int dy)
    {
        Kind = kind; Input = input; Input2 = input2; Output = output; Scale = scale; Sigma = sigma; Radius = radius;
        Matrix = matrix; Color = color; Dx = dx; Dy = dy;
    }

    public FilterPassKind Kind { get; }
    public int Input { get; }
    public int Input2 { get; }
    public int Output { get; }

    /// <summary>降采样 / 放大的因子 k。</summary>
    public int Scale { get; }

    /// <summary>模糊的标准差（所在分辨率的像素）。</summary>
    public float Sigma { get; }

    /// <summary>模糊核半径 ⌈3σ⌉。</summary>
    public int Radius { get; }

    /// <summary>颜色矩阵（20 个系数，行主序；通道顺序 R、G、B、A）。</summary>
    public float[]? Matrix { get; }

    /// <summary>投影的预乘颜色。</summary>
    public Vector4 Color { get; }

    /// <summary>投影的偏移（设备像素，已取整）。</summary>
    public int Dx { get; }
    public int Dy { get; }
}

/// <summary>
/// 把 <see cref="Filter"/> 展开为两个后端共用的执行计划：每一步读一两张中间图像、写一张新图像，图像是设备像素中的矩形（范围外为透明黑），
/// 每一步的结果都按 8 位预乘颜色保存。两个后端执行同一份计划，因此只在浮点舍入上有差别。
/// <para>
/// <b>模糊</b>：σ（设备像素）不超过 <see cref="MaxDirectSigma"/> 时在全分辨率上做分离的高斯卷积；更大时先按绝对坐标对齐的 k×k 方块平均降采样
/// （k = ⌈σ / <see cref="MaxDirectSigma"/>⌉），在 1/k 分辨率上以 σ/k 卷积，再双线性放大——工作量与 σ 无关（近似：降采样与双线性放大各自
/// 增加少量模糊，没有补偿）。所有范围都按绝对坐标对齐、只与输入矩形和 <c>limit</c> 有关，与裁剪无关，局部重绘与整帧重绘的结果相同。
/// </para>
/// <para>
/// <b>上限</b>（恶意输入）：每条链最多 <see cref="MaxOperations"/> 个操作（其余忽略）；σ 钳制到 <see cref="MaxSigma"/> 设备像素；
/// 每张图像都与 <c>limit</c>（渲染目标按有限的距离外扩）求交，所以中间图像的面积有界。超出时通过 <c>degraded</c> 上报。
/// </para>
/// </summary>
internal static class FilterPlan
{
    /// <summary>直接卷积的最大 σ（设备像素）；更大的模糊先降采样。</summary>
    public const float MaxDirectSigma = 4f;

    /// <summary>σ 的上限（设备像素）。</summary>
    public const float MaxSigma = 1024f;

    /// <summary>一条滤镜链最多执行的操作数。</summary>
    public const int MaxOperations = 32;

    /// <summary>滤镜输入可以超出渲染目标的最大距离（设备像素）：目标之外的内容最多这么远仍参与模糊。</summary>
    public const int MaxOutsideReach = 256;

    /// <summary>
    /// 滤镜的设备外扩量（左、上、右、下，设备像素，向上取整）：与计划使用的半径一致（σ 按 <see cref="MaxSigma"/> 钳制，降采样的半径按 k 放大）。
    /// </summary>
    public static void DeviceOutset(Filter filter, in Matrix3x2 ctm, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        var ops = filter.Operations;
        int n = Math.Min(ops.Count, MaxOperations);
        for (int i = 0; i < n; i++)
        {
            var op = ops[i];
            if (op.Kind == FilterKind.ColorMatrix) continue;
            DeviceSigma(op, ctm, out float sx, out float sy, out _);
            int rx = Reach(sx), ry = Reach(sy);
            if (op.Kind == FilterKind.Blur)
            {
                left = Add(left, rx); right = Add(right, rx); top = Add(top, ry); bottom = Add(bottom, ry);
            }
            else
            {
                ShadowOffset(op, ctm, out int dx, out int dy);
                left = Add(left, Math.Max(0, rx - dx)); right = Add(right, Math.Max(0, rx + dx));
                top = Add(top, Math.Max(0, ry - dy)); bottom = Add(bottom, Math.Max(0, ry + dy));
            }
        }
    }

    static int Add(int a, int b) => (int)Math.Min((long)a + b, 1 << 24);

    /// <summary>模糊在设备像素中的影响半径（含降采样时放大的半径）。</summary>
    static int Reach(float sigma)
    {
        if (!(sigma > 0)) return 0;
        int k = DownsampleFactor(sigma);
        int r = (int)Math.Ceiling(3 * sigma / k);
        // 降采样的块对齐与双线性放大再各多一个低分辨率纹素
        return k == 1 ? r : (r + 2) * k;
    }

    static int DownsampleFactor(float sigma) => sigma <= MaxDirectSigma ? 1 : (int)Math.Min(256, Math.Ceiling(sigma / MaxDirectSigma));

    /// <summary>操作的设备标准差：局部标准差乘以变换在两个轴上的缩放（旋转下是近似）。</summary>
    static void DeviceSigma(FilterOperation op, in Matrix3x2 ctm, out float sx, out float sy, out bool clamped)
    {
        float kx = MathF.Sqrt(ctm.M11 * ctm.M11 + ctm.M12 * ctm.M12);
        float ky = MathF.Sqrt(ctm.M21 * ctm.M21 + ctm.M22 * ctm.M22);
        sx = op.StdDeviationX * kx;
        sy = op.StdDeviationY * ky;
        clamped = false;
        if (!(sx <= MaxSigma)) { sx = float.IsNaN(sx) ? 0 : MaxSigma; clamped = true; }
        if (!(sy <= MaxSigma)) { sy = float.IsNaN(sy) ? 0 : MaxSigma; clamped = true; }
    }

    /// <summary>投影偏移（局部向量经变换的线性部分，四舍五入到整设备像素）。</summary>
    static void ShadowOffset(FilterOperation op, in Matrix3x2 ctm, out int dx, out int dy)
    {
        float fx = op.OffsetX * ctm.M11 + op.OffsetY * ctm.M21;
        float fy = op.OffsetX * ctm.M12 + op.OffsetY * ctm.M22;
        dx = ClampInt(fx);
        dy = ClampInt(fy);
    }

    static int ClampInt(float v) => float.IsNaN(v) ? 0 : (int)Math.Max(-(1 << 24), Math.Min(1 << 24, Math.Round(v)));

    /// <summary>
    /// 生成计划。<paramref name="source"/> 是输入图像的设备矩形，<paramref name="limit"/> 限制所有图像的范围。
    /// 返回最终图像的下标（没有任何步骤时为 0）。
    /// </summary>
    public static int Build(Filter filter, in Matrix3x2 ctm, PixelRect source, PixelRect limit,
        List<FilterImage> images, List<FilterPass> passes, Action<string>? degraded)
    {
        images.Clear();
        passes.Clear();
        images.Add(new FilterImage(source, 1));
        int current = 0;
        var ops = filter.Operations;
        if (ops.Count > MaxOperations) degraded?.Invoke($"Filter chains longer than {MaxOperations} operations are truncated.");
        int n = Math.Min(ops.Count, MaxOperations);
        for (int i = 0; i < n; i++)
        {
            var op = ops[i];
            if (images[current].Rect.IsEmpty) break;
            switch (op.Kind)
            {
                case FilterKind.ColorMatrix:
                {
                    var m = new float[20];
                    for (int j = 0; j < 20 && j < op.Matrix.Count; j++) m[j] = op.Matrix[j];
                    current = AddPass(images, passes, FilterPassKind.ColorMatrix, current, -1, images[current].Rect, 1, 0, 0, m, default, 0, 0);
                    break;
                }
                case FilterKind.Blur:
                {
                    DeviceSigma(op, ctm, out float sx, out float sy, out bool clamped);
                    if (clamped) degraded?.Invoke($"Filter blur larger than {MaxSigma} device pixels was clamped.");
                    current = Blur(images, passes, current, sx, sy, limit);
                    break;
                }
                case FilterKind.DropShadow:
                {
                    DeviceSigma(op, ctm, out float sx, out float sy, out bool clamped);
                    if (clamped) degraded?.Invoke($"Filter blur larger than {MaxSigma} device pixels was clamped.");
                    ShadowOffset(op, ctm, out int dx, out int dy);
                    int blurred = Blur(images, passes, current, sx, sy, limit);
                    var shifted = images[blurred].Rect;
                    shifted = new PixelRect(shifted.X + dx, shifted.Y + dy, shifted.Width, shifted.Height);
                    var outRect = images[current].Rect.Union(shifted).Intersect(limit);
                    var c = op.Color;
                    float a = Math.Max(0, Math.Min(1, c.A));
                    var color = new Vector4(Clamp01(c.R) * a, Clamp01(c.G) * a, Clamp01(c.B) * a, a);
                    current = AddPass(images, passes, FilterPassKind.DropShadow, current, blurred, outRect, 1, 0, 0, null, color, dx, dy);
                    break;
                }
            }
        }
        return current;
    }

    static float Clamp01(float v) => float.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;

    static int AddPass(List<FilterImage> images, List<FilterPass> passes, FilterPassKind kind, int input, int input2, PixelRect rect, int scale,
        float sigma, int radius, float[]? matrix, Vector4 color, int dx, int dy)
    {
        images.Add(new FilterImage(rect, scale));
        int output = images.Count - 1;
        passes.Add(new FilterPass(kind, input, input2, output, scale, sigma, radius, matrix, color, dx, dy));
        return output;
    }

    /// <summary>分离的高斯模糊（必要时降采样）；σ 都不大于 0 时原样返回输入。</summary>
    static int Blur(List<FilterImage> images, List<FilterPass> passes, int input, float sx, float sy, PixelRect limit)
    {
        if (!(sx > 0) && !(sy > 0)) return input;
        int k = DownsampleFactor(Math.Max(sx, sy));
        var src = images[input].Rect;
        int cur = input;
        var lowLimit = k == 1 ? limit : PixelRect.FromLTRB(FloorDiv(limit.X, k), FloorDiv(limit.Y, k), CeilDiv(limit.Right, k), CeilDiv(limit.Bottom, k));
        if (k > 1)
        {
            var low = PixelRect.FromLTRB(FloorDiv(src.X, k), FloorDiv(src.Y, k), CeilDiv(src.Right, k), CeilDiv(src.Bottom, k));
            cur = AddPass(images, passes, FilterPassKind.Downsample, cur, -1, low, k, 0, 0, null, default, 0, 0);
        }
        float lsx = sx / k, lsy = sy / k;
        if (lsx > 0)
        {
            int r = (int)Math.Ceiling(3 * lsx);
            var rect = images[cur].Rect;
            rect = PixelRect.FromLTRB(rect.X - r, rect.Y, rect.Right + r, rect.Bottom).Intersect(lowLimit);
            cur = AddPass(images, passes, FilterPassKind.BlurX, cur, -1, rect, k, lsx, r, null, default, 0, 0);
        }
        if (lsy > 0)
        {
            int r = (int)Math.Ceiling(3 * lsy);
            var rect = images[cur].Rect;
            rect = PixelRect.FromLTRB(rect.X, rect.Y - r, rect.Right, rect.Bottom + r).Intersect(lowLimit);
            cur = AddPass(images, passes, FilterPassKind.BlurY, cur, -1, rect, k, lsy, r, null, default, 0, 0);
        }
        if (k > 1)
        {
            var lr = images[cur].Rect;
            // 双线性放大的支撑比低分辨率范围多半个纹素：外扩 k 个设备像素
            var up = PixelRect.FromLTRB(lr.X * k - k, lr.Y * k - k, lr.Right * k + k, lr.Bottom * k + k).Intersect(limit);
            cur = AddPass(images, passes, FilterPassKind.Upsample, cur, -1, up, k, 0, 0, null, default, 0, 0);
        }
        return cur;
    }

    internal static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    internal static int CeilDiv(int a, int b) => a >= 0 ? (a + b - 1) / b : -((-a) / b);

    /// <summary>高斯核权重（未归一化的 exp(−t²/2σ²)，t = 0..r）与它们的总和（两侧对称），两个后端按同一顺序求和。</summary>
    public static float[] Weights(float sigma, int radius, out float sum)
    {
        var w = new float[radius + 1];
        float inv = 1 / (2 * sigma * sigma);
        sum = 0;
        for (int t = 0; t <= radius; t++)
        {
            w[t] = MathF.Exp(-t * t * inv);
        }
        // 先加外侧再加中心：与着色器的求和顺序一致
        for (int t = radius; t >= 1; t--) sum += 2 * w[t];
        sum += w[0];
        return w;
    }
}
