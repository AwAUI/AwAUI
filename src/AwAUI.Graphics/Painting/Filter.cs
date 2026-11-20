using System;
using System.Collections.Generic;

namespace AwAUI.Graphics;

/// <summary>滤镜操作的种类（<see cref="FilterOperation.Kind"/>）。数值是 DisplayList 资源语义的一部分。</summary>
public enum FilterKind : byte
{
    /// <summary>高斯模糊：标准差 <see cref="FilterOperation.StdDeviationX"/> / <see cref="FilterOperation.StdDeviationY"/>（局部坐标）。</summary>
    Blur = 0,

    /// <summary>
    /// 投影：内容的 alpha 经模糊（标准差 <see cref="FilterOperation.StdDeviationX"/> / <see cref="FilterOperation.StdDeviationY"/>）、
    /// 平移（<see cref="FilterOperation.OffsetX"/>、<see cref="FilterOperation.OffsetY"/>）、着色（<see cref="FilterOperation.Color"/>）后垫在内容之下。
    /// </summary>
    DropShadow = 1,

    /// <summary>
    /// 4×5 颜色矩阵（<see cref="FilterOperation.Matrix"/>，行主序，行 R、G、B、A，列 R、G、B、A、偏移），
    /// 作用于非预乘的 sRGB 编码颜色（0–1），结果钳制到 [0, 1]。CSS 的 brightness、contrast、grayscale、hue-rotate、invert、
    /// opacity、saturate、sepia 都是它的特例（Filter Effects 1 §「Shorthands」给出的矩阵）。
    /// </summary>
    ColorMatrix = 2,
}

/// <summary>
/// 一个滤镜操作（不可变）。用工厂方法构造：<see cref="Blur"/>、<see cref="DropShadow"/>、<see cref="Brightness"/> 等。
/// 长度（标准差、偏移）是录制时的局部坐标，后端按当前变换换算到设备像素。
/// </summary>
public sealed class FilterOperation : IEquatable<FilterOperation>
{
    readonly float[]? _matrix;

    FilterOperation(FilterKind kind, float sx, float sy, float dx, float dy, Color color, float[]? matrix)
    {
        Kind = kind;
        StdDeviationX = Sanitize(sx);
        StdDeviationY = Sanitize(sy);
        OffsetX = float.IsNaN(dx) || float.IsInfinity(dx) ? 0 : dx;
        OffsetY = float.IsNaN(dy) || float.IsInfinity(dy) ? 0 : dy;
        Color = color;
        _matrix = matrix;
    }

    /// <summary>负数、NaN 按 0；无穷大按 <see cref="float.MaxValue"/>（后端再钳制）。</summary>
    static float Sanitize(float v) => float.IsNaN(v) || v < 0 ? 0 : float.IsPositiveInfinity(v) ? float.MaxValue : v;

    public FilterKind Kind { get; }

    /// <summary>模糊的水平标准差（局部坐标）；投影的模糊同样用它。</summary>
    public float StdDeviationX { get; }

    /// <summary>模糊的垂直标准差（局部坐标）。</summary>
    public float StdDeviationY { get; }

    /// <summary>投影的偏移（局部坐标）。</summary>
    public float OffsetX { get; }
    public float OffsetY { get; }

    /// <summary>投影的颜色（非预乘）。</summary>
    public Color Color { get; }

    /// <summary>颜色矩阵的 20 个系数（<see cref="FilterKind.ColorMatrix"/>；其他种类为空）。</summary>
    public IReadOnlyList<float> Matrix => _matrix ?? Array.Empty<float>();

    // ------------------------------------------------------------ 工厂

    /// <summary>高斯模糊（CSS <c>blur(σ)</c>：长度就是标准差）。</summary>
    public static FilterOperation Blur(float stdDeviation) => new(FilterKind.Blur, stdDeviation, stdDeviation, 0, 0, default, null);

    /// <summary>各向异性的高斯模糊。</summary>
    public static FilterOperation Blur(float stdDeviationX, float stdDeviationY) => new(FilterKind.Blur, stdDeviationX, stdDeviationY, 0, 0, default, null);

    /// <summary>投影（CSS <c>drop-shadow(color dx dy blur)</c> 的标准差是模糊半径的一半，由调用方换算）。</summary>
    public static FilterOperation DropShadow(float offsetX, float offsetY, float stdDeviation, Color color) =>
        new(FilterKind.DropShadow, stdDeviation, stdDeviation, offsetX, offsetY, color, null);

    /// <summary>任意 4×5 颜色矩阵（行主序 20 个系数；不足 20 个时抛出异常）。非有限系数按 0。</summary>
    public static FilterOperation ColorMatrix(IReadOnlyList<float> matrix)
    {
        if (matrix is null) throw new ArgumentNullException(nameof(matrix));
        if (matrix.Count != 20) throw new ArgumentException("A color matrix has exactly 20 coefficients.", nameof(matrix));
        var m = new float[20];
        for (int i = 0; i < 20; i++)
        {
            float v = matrix[i];
            m[i] = float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;
        }
        return new FilterOperation(FilterKind.ColorMatrix, 0, 0, 0, 0, default, m);
    }

    /// <summary><c>brightness(a)</c>：每个颜色分量乘以 a。</summary>
    public static FilterOperation Brightness(float amount) => Linear(NonNegative(amount), 0);

    /// <summary><c>contrast(a)</c>：C' = a·C + (0.5 − 0.5·a)。</summary>
    public static FilterOperation Contrast(float amount)
    {
        float a = NonNegative(amount);
        return Linear(a, 0.5f - 0.5f * a);
    }

    /// <summary><c>invert(a)</c>（a 钳制到 [0, 1]）：C' = a + C·(1 − 2a)。</summary>
    public static FilterOperation Invert(float amount)
    {
        float a = Clamp01(amount);
        return Linear(1 - 2 * a, a);
    }

    /// <summary><c>opacity(a)</c>（a 钳制到 [0, 1]）：A' = a·A。</summary>
    public static FilterOperation Opacity(float amount)
    {
        var m = Identity();
        m[18] = Clamp01(amount);
        return new FilterOperation(FilterKind.ColorMatrix, 0, 0, 0, 0, default, m);
    }

    /// <summary><c>grayscale(a)</c>（a 钳制到 [0, 1]）。</summary>
    public static FilterOperation Grayscale(float amount)
    {
        float k = 1 - Clamp01(amount);
        return Rgb(
            0.2126f + 0.7874f * k, 0.7152f - 0.7152f * k, 0.0722f - 0.0722f * k,
            0.2126f - 0.2126f * k, 0.7152f + 0.2848f * k, 0.0722f - 0.0722f * k,
            0.2126f - 0.2126f * k, 0.7152f - 0.7152f * k, 0.0722f + 0.9278f * k);
    }

    /// <summary><c>sepia(a)</c>（a 钳制到 [0, 1]）。</summary>
    public static FilterOperation Sepia(float amount)
    {
        float k = 1 - Clamp01(amount);
        return Rgb(
            0.393f + 0.607f * k, 0.769f - 0.769f * k, 0.189f - 0.189f * k,
            0.349f - 0.349f * k, 0.686f + 0.314f * k, 0.168f - 0.168f * k,
            0.272f - 0.272f * k, 0.534f - 0.534f * k, 0.131f + 0.869f * k);
    }

    /// <summary><c>saturate(s)</c>（s ≥ 0，可大于 1）。</summary>
    public static FilterOperation Saturate(float amount)
    {
        float s = NonNegative(amount);
        return Rgb(
            0.213f + 0.787f * s, 0.715f - 0.715f * s, 0.072f - 0.072f * s,
            0.213f - 0.213f * s, 0.715f + 0.285f * s, 0.072f - 0.072f * s,
            0.213f - 0.213f * s, 0.715f - 0.715f * s, 0.072f + 0.928f * s);
    }

    /// <summary><c>hue-rotate(θ)</c>（度）。</summary>
    public static FilterOperation HueRotate(float degrees)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees)) degrees = 0;
        double r = (degrees % 360.0) * Math.PI / 180.0;
        float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
        return Rgb(
            0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f,
            0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f,
            0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f);
    }

    static float NonNegative(float v) => float.IsNaN(v) || v < 0 ? 0 : float.IsInfinity(v) ? 1e6f : v;

    static float Clamp01(float v) => float.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;

    static float[] Identity()
    {
        var m = new float[20];
        m[0] = m[6] = m[12] = m[18] = 1;
        return m;
    }

    static FilterOperation Linear(float slope, float intercept)
    {
        var m = new float[20];
        m[0] = m[6] = m[12] = slope;
        m[4] = m[9] = m[14] = intercept;
        m[18] = 1;
        return new FilterOperation(FilterKind.ColorMatrix, 0, 0, 0, 0, default, m);
    }

    static FilterOperation Rgb(float a00, float a01, float a02, float a10, float a11, float a12, float a20, float a21, float a22)
    {
        var m = new float[20];
        m[0] = a00; m[1] = a01; m[2] = a02;
        m[5] = a10; m[6] = a11; m[7] = a12;
        m[10] = a20; m[11] = a21; m[12] = a22;
        m[18] = 1;
        return new FilterOperation(FilterKind.ColorMatrix, 0, 0, 0, 0, default, m);
    }

    /// <summary>
    /// 本操作让内容向四周扩展的距离（局部坐标；模糊按 3σ 计）：左、上、右、下。
    /// 颜色矩阵不扩展（透明像素经本库的矩阵仍然透明——alpha 行的偏移不为 0 的自定义矩阵除外，那时结果仍只在输入范围内计算）。
    /// </summary>
    public void GetOutset(out float left, out float top, out float right, out float bottom)
    {
        left = top = right = bottom = 0;
        float ex = Reach(StdDeviationX), ey = Reach(StdDeviationY);
        switch (Kind)
        {
            case FilterKind.Blur:
                left = right = ex;
                top = bottom = ey;
                break;
            case FilterKind.DropShadow:
                left = Math.Max(0, ex - OffsetX);
                right = Math.Max(0, ex + OffsetX);
                top = Math.Max(0, ey - OffsetY);
                bottom = Math.Max(0, ey + OffsetY);
                break;
        }
    }

    static float Reach(float sigma) => sigma > 0 ? Math.Min(sigma * 3, 1e7f) : 0;

    public bool Equals(FilterOperation? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Kind != o.Kind || !StdDeviationX.Equals(o.StdDeviationX) || !StdDeviationY.Equals(o.StdDeviationY)
            || !OffsetX.Equals(o.OffsetX) || !OffsetY.Equals(o.OffsetY) || Color != o.Color) return false;
        if (_matrix is null || o._matrix is null) return _matrix is null && o._matrix is null;
        for (int i = 0; i < 20; i++) if (!_matrix[i].Equals(o._matrix[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is FilterOperation f && Equals(f);

    public override int GetHashCode()
    {
        int h = HashCodeCompat.Combine((int)Kind, StdDeviationX.GetHashCode(), StdDeviationY.GetHashCode(), OffsetX.GetHashCode());
        h = HashCodeCompat.Combine(h, OffsetY.GetHashCode(), Color.GetHashCode());
        if (_matrix is not null) foreach (var v in _matrix) h = HashCodeCompat.Combine(h, v.GetHashCode());
        return h;
    }

    public override string ToString() => Kind switch
    {
        FilterKind.Blur => $"blur({StdDeviationX}, {StdDeviationY})",
        FilterKind.DropShadow => $"drop-shadow({OffsetX}, {OffsetY}, {StdDeviationX}, {Color})",
        _ => "color-matrix(" + string.Join(" ", Matrix) + ")",
    };
}

/// <summary>
/// 滤镜链（DisplayList 的 Filter 资源，design/13）：按顺序作用的 <see cref="FilterOperation"/>，每一步的输入是上一步的输出，
/// 第一步的输入是图层内容（<see cref="LayerOptions.Filter"/>）或背景（<see cref="DrawingContext.DrawBackdrop"/>）。不可变。
/// 范围外的像素是透明黑；每一步的结果按 8 位预乘颜色保存（两个后端相同）。
/// </summary>
public sealed class Filter : IEquatable<Filter>
{
    readonly FilterOperation[] _operations;

    public Filter(IReadOnlyList<FilterOperation> operations)
    {
        if (operations is null) throw new ArgumentNullException(nameof(operations));
        var list = new FilterOperation[operations.Count];
        for (int i = 0; i < list.Length; i++) list[i] = operations[i] ?? throw new ArgumentException("Null filter operation.", nameof(operations));
        _operations = list;
    }

    public Filter(params FilterOperation[] operations) : this((IReadOnlyList<FilterOperation>)operations) { }

    public IReadOnlyList<FilterOperation> Operations => _operations;

    public bool IsEmpty => _operations.Length == 0;

    /// <summary>
    /// 整条链让内容向四周扩展的距离（局部坐标，逐步累加）：左、上、右、下。
    /// 用于录制器的保守包围盒与 UI 的墨迹 / 损伤计算。
    /// </summary>
    public void GetOutset(out float left, out float top, out float right, out float bottom)
    {
        left = top = right = bottom = 0;
        foreach (var op in _operations)
        {
            op.GetOutset(out float l, out float t, out float r, out float b);
            left = Math.Min(left + l, 1e7f);
            top = Math.Min(top + t, 1e7f);
            right = Math.Min(right + r, 1e7f);
            bottom = Math.Min(bottom + b, 1e7f);
        }
    }

    /// <summary>把 <paramref name="rect"/> 按 <see cref="GetOutset"/> 外扩。</summary>
    public Rect Inflate(Rect rect)
    {
        GetOutset(out float l, out float t, out float r, out float b);
        return Rect.FromLTRB(rect.Left - l, rect.Top - t, rect.Right + r, rect.Bottom + b);
    }

    public bool Equals(Filter? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || o._operations.Length != _operations.Length) return false;
        for (int i = 0; i < _operations.Length; i++) if (!_operations[i].Equals(o._operations[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Filter f && Equals(f);

    public override int GetHashCode()
    {
        int h = _operations.Length;
        foreach (var op in _operations) h = HashCodeCompat.Combine(h, op.GetHashCode());
        return h;
    }

    public override string ToString() => string.Join(" ", (object[])_operations);
}
