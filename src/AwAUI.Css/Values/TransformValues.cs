using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>变换函数的种类（CSS Transforms 1/2）。</summary>
public enum CssTransformFunctionKind : byte
{
    Matrix,
    Matrix3D,
    Translate,
    TranslateX,
    TranslateY,
    TranslateZ,
    Translate3D,
    Scale,
    ScaleX,
    ScaleY,
    ScaleZ,
    Scale3D,
    Rotate,
    RotateX,
    RotateY,
    RotateZ,
    Rotate3D,
    Skew,
    SkewX,
    SkewY,
    Perspective,
    /// <summary>
    /// 动画引擎产生的插值结果（两个变换列表在某个进度上的矩阵插值）：列表含百分比时矩阵依赖参考盒，只能在使用时求值
    /// （<see cref="CssTransformFunction.ToMatrix"/>）。只出现在动画的计算值中，不能由 CSS 书写。
    /// </summary>
    Interpolated,
}

/// <summary>
/// 一个变换函数的计算值。长度已换算为 px，平移的百分比保留（相对参考盒，见 <see cref="CssTransforms"/>），角度为度。
/// <list type="bullet">
/// <item>平移：<see cref="X"/>、<see cref="Y"/>（长度百分比）、<see cref="Z"/>（px）。</item>
/// <item>缩放：<see cref="A"/>、<see cref="B"/>、<see cref="C"/> 为三个轴的比例。</item>
/// <item>旋转：<see cref="A"/> = 角度；<c>rotate3d</c> 的轴为 <see cref="B"/>、<see cref="C"/>、<see cref="D"/>。</item>
/// <item>错切：<see cref="A"/>、<see cref="B"/> 为两个角度。</item>
/// <item><c>perspective()</c>：<see cref="A"/> = 距离（px，<c>none</c> 为 +∞）。</item>
/// <item>矩阵：<see cref="Matrix"/>（<c>matrix()</c> 6 个、<c>matrix3d()</c> 16 个，按书写顺序）。</item>
/// </list>
/// </summary>
public sealed class CssTransformFunction : IEquatable<CssTransformFunction>
{
    internal CssTransformFunction(CssTransformFunctionKind kind, LengthPercentage x, LengthPercentage y, float z, float a, float b, float c, float d, float[]? matrix)
    {
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        A = a;
        B = b;
        C = c;
        D = d;
        _matrix = matrix;
    }

    readonly float[]? _matrix;
    readonly CssTransformList? _from, _to;
    readonly double _progress;

    /// <summary>延迟的矩阵插值（<see cref="CssTransformFunctionKind.Interpolated"/>）。</summary>
    internal CssTransformFunction(CssTransformList from, CssTransformList to, double progress)
    {
        Kind = CssTransformFunctionKind.Interpolated;
        _from = from;
        _to = to;
        _progress = progress;
    }

    /// <summary><see cref="CssTransformFunctionKind.Interpolated"/> 的起点列表。</summary>
    public CssTransformList? InterpolatedFrom => _from;

    /// <summary><see cref="CssTransformFunctionKind.Interpolated"/> 的终点列表。</summary>
    public CssTransformList? InterpolatedTo => _to;

    /// <summary><see cref="CssTransformFunctionKind.Interpolated"/> 的进度。</summary>
    public double InterpolatedProgress => _progress;

    public CssTransformFunctionKind Kind { get; }
    public LengthPercentage X { get; }
    public LengthPercentage Y { get; }
    public float Z { get; }
    public float A { get; }
    public float B { get; }
    public float C { get; }
    public float D { get; }

    /// <summary><c>matrix()</c> / <c>matrix3d()</c> 的参数。</summary>
    public IReadOnlyList<float> Matrix => _matrix ?? Array.Empty<float>();

    /// <summary>是否为 3D 函数（会使元素按 3D 渲染）。</summary>
    public bool Is3D => Kind is CssTransformFunctionKind.Matrix3D or CssTransformFunctionKind.TranslateZ or CssTransformFunctionKind.Translate3D
        or CssTransformFunctionKind.ScaleZ or CssTransformFunctionKind.Scale3D or CssTransformFunctionKind.RotateX or CssTransformFunctionKind.RotateY
        or CssTransformFunctionKind.Rotate3D or CssTransformFunctionKind.Perspective
        || (Kind == CssTransformFunctionKind.Interpolated && (Animation.TransformInterpolation.Is3D(_from!) || Animation.TransformInterpolation.Is3D(_to!)));

    /// <summary>按参考盒尺寸得到矩阵（System.Numerics 的行向量约定：p' = p · M）。</summary>
    public Matrix4x4 ToMatrix(float referenceWidth, float referenceHeight)
    {
        const float Deg = MathF.PI / 180;
        switch (Kind)
        {
            case CssTransformFunctionKind.Matrix:
            {
                var m = _matrix!;
                return new Matrix4x4(m[0], m[1], 0, 0, m[2], m[3], 0, 0, 0, 0, 1, 0, m[4], m[5], 0, 1);
            }
            case CssTransformFunctionKind.Matrix3D:
            {
                var m = _matrix!;
                return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            }
            case CssTransformFunctionKind.Translate:
            case CssTransformFunctionKind.TranslateX:
            case CssTransformFunctionKind.TranslateY:
            case CssTransformFunctionKind.TranslateZ:
            case CssTransformFunctionKind.Translate3D:
                return Matrix4x4.CreateTranslation(X.Resolve(referenceWidth), Y.Resolve(referenceHeight), Z);
            case CssTransformFunctionKind.Scale:
            case CssTransformFunctionKind.ScaleX:
            case CssTransformFunctionKind.ScaleY:
            case CssTransformFunctionKind.ScaleZ:
            case CssTransformFunctionKind.Scale3D:
                return Matrix4x4.CreateScale(A, B, C);
            case CssTransformFunctionKind.Rotate:
            case CssTransformFunctionKind.RotateZ:
                return Matrix4x4.CreateRotationZ(A * Deg);
            case CssTransformFunctionKind.RotateX:
                return Matrix4x4.CreateRotationX(A * Deg);
            case CssTransformFunctionKind.RotateY:
                return Matrix4x4.CreateRotationY(A * Deg);
            case CssTransformFunctionKind.Rotate3D:
                return CssTransforms.Rotation3D(B, C, D, A);
            case CssTransformFunctionKind.Skew:
            case CssTransformFunctionKind.SkewX:
            case CssTransformFunctionKind.SkewY:
            {
                var m = Matrix4x4.Identity;
                m.M21 = MathF.Tan(A * Deg);
                m.M12 = MathF.Tan(B * Deg);
                return m;
            }
            case CssTransformFunctionKind.Interpolated:
                return Animation.TransformInterpolation.InterpolateMatrices(_from!.ToMatrix(referenceWidth, referenceHeight),
                    _to!.ToMatrix(referenceWidth, referenceHeight), _progress);
            default:
            {
                var m = Matrix4x4.Identity;
                if (!float.IsInfinity(A)) m.M34 = -1 / Math.Max(1, A);
                return m;
            }
        }
    }

    public bool Equals(CssTransformFunction? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Kind != o.Kind || !X.Equals(o.X) || !Y.Equals(o.Y) || Z != o.Z || A != o.A || B != o.B || C != o.C || D != o.D) return false;
        if (Kind == CssTransformFunctionKind.Interpolated) return _progress == o._progress && _from!.Equals(o._from) && _to!.Equals(o._to);
        if (_matrix is null) return o._matrix is null;
        if (o._matrix is null || o._matrix.Length != _matrix.Length) return false;
        for (int i = 0; i < _matrix.Length; i++) if (_matrix[i] != o._matrix[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssTransformFunction f && Equals(f);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, X.GetHashCode(), A.GetHashCode(), B.GetHashCode());

    public override string ToString()
    {
        static string N(float f) => ValueText.Number(f);
        static string Deg(float f) => ValueText.Number(f) + "deg";
        switch (Kind)
        {
            case CssTransformFunctionKind.Matrix:
            case CssTransformFunctionKind.Matrix3D:
            {
                var sb = new StringBuilder(Kind == CssTransformFunctionKind.Matrix ? "matrix(" : "matrix3d(");
                for (int i = 0; i < _matrix!.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(N(_matrix[i]));
                }
                return sb.Append(')').ToString();
            }
            case CssTransformFunctionKind.Translate: return "translate(" + X + ", " + Y + ")";
            case CssTransformFunctionKind.TranslateX: return "translateX(" + X + ")";
            case CssTransformFunctionKind.TranslateY: return "translateY(" + Y + ")";
            case CssTransformFunctionKind.TranslateZ: return "translateZ(" + ValueText.Px(Z) + ")";
            case CssTransformFunctionKind.Translate3D: return "translate3d(" + X + ", " + Y + ", " + ValueText.Px(Z) + ")";
            case CssTransformFunctionKind.Scale: return "scale(" + N(A) + ", " + N(B) + ")";
            case CssTransformFunctionKind.ScaleX: return "scaleX(" + N(A) + ")";
            case CssTransformFunctionKind.ScaleY: return "scaleY(" + N(B) + ")";
            case CssTransformFunctionKind.ScaleZ: return "scaleZ(" + N(C) + ")";
            case CssTransformFunctionKind.Scale3D: return "scale3d(" + N(A) + ", " + N(B) + ", " + N(C) + ")";
            case CssTransformFunctionKind.Rotate: return "rotate(" + Deg(A) + ")";
            case CssTransformFunctionKind.RotateX: return "rotateX(" + Deg(A) + ")";
            case CssTransformFunctionKind.RotateY: return "rotateY(" + Deg(A) + ")";
            case CssTransformFunctionKind.RotateZ: return "rotateZ(" + Deg(A) + ")";
            case CssTransformFunctionKind.Rotate3D: return "rotate3d(" + N(B) + ", " + N(C) + ", " + N(D) + ", " + Deg(A) + ")";
            case CssTransformFunctionKind.Skew: return "skew(" + Deg(A) + ", " + Deg(B) + ")";
            case CssTransformFunctionKind.SkewX: return "skewX(" + Deg(A) + ")";
            case CssTransformFunctionKind.SkewY: return "skewY(" + Deg(B) + ")";
            case CssTransformFunctionKind.Interpolated:
            {
                // 没有参考盒：百分比按 0 求值（getComputedStyle 在浏览器中按元素的盒求值）
                var m = ToMatrix(0, 0);
                return new CssTransformFunction(CssTransformFunctionKind.Matrix3D, default, default, 0, 0, 0, 0, 0,
                    new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }).ToString();
            }
            default: return float.IsInfinity(A) ? "perspective(none)" : "perspective(" + ValueText.Px(A) + ")";
        }
    }
}

/// <summary><c>transform</c> 的计算值：函数列表；<c>none</c> 为空列表。</summary>
public sealed class CssTransformList : IEquatable<CssTransformList>
{
    public static readonly CssTransformList None = new(Array.Empty<CssTransformFunction>());

    internal CssTransformList(CssTransformFunction[] functions) => _functions = functions;

    readonly CssTransformFunction[] _functions;

    public IReadOnlyList<CssTransformFunction> Functions => _functions;

    public bool IsNone => _functions.Length == 0;

    /// <summary>按书写顺序复合全部函数（行向量约定：先应用最右侧的函数，故矩阵为 Fn·…·F1）。</summary>
    public Matrix4x4 ToMatrix(float referenceWidth, float referenceHeight)
    {
        var m = Matrix4x4.Identity;
        for (int i = _functions.Length - 1; i >= 0; i--) m *= _functions[i].ToMatrix(referenceWidth, referenceHeight);
        return m;
    }

    public bool Equals(CssTransformList? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || o._functions.Length != _functions.Length) return false;
        for (int i = 0; i < _functions.Length; i++) if (!_functions[i].Equals(o._functions[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssTransformList l && Equals(l);

    public override int GetHashCode()
    {
        int h = _functions.Length;
        foreach (var f in _functions) h = HashCodeCompat.Combine(h, f.GetHashCode());
        return h;
    }

    public override string ToString() => IsNone ? "none" : string.Join(" ", (object[])_functions);
}

/// <summary><c>translate</c> 的计算值：<c>none</c>，或 x y z（x/y 为长度百分比，z 为 px）。</summary>
public readonly struct CssTranslate : IEquatable<CssTranslate>
{
    public CssTranslate(LengthPercentage x, LengthPercentage y, float z)
    {
        IsSpecified = true;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary><c>none</c>（<c>default</c>）。</summary>
    public static CssTranslate None => default;

    readonly bool IsSpecified;

    public bool IsNone => !IsSpecified;
    public LengthPercentage X { get; }
    public LengthPercentage Y { get; }
    public float Z { get; }

    public bool Equals(CssTranslate o) => IsSpecified == o.IsSpecified && X.Equals(o.X) && Y.Equals(o.Y) && Z == o.Z;
    public override bool Equals(object? obj) => obj is CssTranslate t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine(X.GetHashCode(), Y.GetHashCode(), Z.GetHashCode(), IsSpecified ? 1 : 0);

    public override string ToString()
    {
        if (IsNone) return "none";
        if (Z != 0) return X + " " + Y + " " + ValueText.Px(Z);
        if (Y.IsFixed && Y.Length == 0) return X.ToString();
        return X + " " + Y;
    }
}

/// <summary><c>rotate</c> 的计算值：<c>none</c>，或绕轴 (<see cref="AxisX"/>, <see cref="AxisY"/>, <see cref="AxisZ"/>) 旋转 <see cref="Angle"/> 度。</summary>
public readonly struct CssRotate : IEquatable<CssRotate>
{
    public CssRotate(float x, float y, float z, float angle)
    {
        IsSpecified = true;
        AxisX = x;
        AxisY = y;
        AxisZ = z;
        Angle = angle;
    }

    public static CssRotate None => default;

    readonly bool IsSpecified;

    public bool IsNone => !IsSpecified;
    public float AxisX { get; }
    public float AxisY { get; }
    public float AxisZ { get; }

    /// <summary>角度（度）。</summary>
    public float Angle { get; }

    /// <summary>是否为绕 z 轴的 2D 旋转。</summary>
    public bool Is2D => AxisX == 0 && AxisY == 0;

    public bool Equals(CssRotate o) => IsSpecified == o.IsSpecified && AxisX == o.AxisX && AxisY == o.AxisY && AxisZ == o.AxisZ && Angle == o.Angle;
    public override bool Equals(object? obj) => obj is CssRotate r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine(AxisX.GetHashCode(), AxisY.GetHashCode(), AxisZ.GetHashCode(), Angle.GetHashCode());

    public override string ToString()
    {
        if (IsNone) return "none";
        string a = ValueText.Number(Angle) + "deg";
        if (Is2D && AxisZ > 0) return a;
        if (AxisY == 0 && AxisZ == 0 && AxisX > 0) return "x " + a;
        if (AxisX == 0 && AxisZ == 0 && AxisY > 0) return "y " + a;
        if (AxisX == 0 && AxisY == 0) return "z " + a;
        return ValueText.Number(AxisX) + " " + ValueText.Number(AxisY) + " " + ValueText.Number(AxisZ) + " " + a;
    }
}

/// <summary><c>scale</c> 的计算值：<c>none</c>，或三个轴的比例。</summary>
public readonly struct CssScale : IEquatable<CssScale>
{
    public CssScale(float x, float y, float z)
    {
        IsSpecified = true;
        X = x;
        Y = y;
        Z = z;
    }

    public static CssScale None => default;

    readonly bool IsSpecified;

    public bool IsNone => !IsSpecified;
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public bool Equals(CssScale o) => IsSpecified == o.IsSpecified && X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object? obj) => obj is CssScale s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(X.GetHashCode(), Y.GetHashCode(), Z.GetHashCode(), IsSpecified ? 1 : 0);

    public override string ToString()
    {
        if (IsNone) return "none";
        if (Z != 1) return ValueText.Number(X) + " " + ValueText.Number(Y) + " " + ValueText.Number(Z);
        if (X == Y) return ValueText.Number(X);
        return ValueText.Number(X) + " " + ValueText.Number(Y);
    }
}

/// <summary><c>transform-origin</c> 的计算值：x、y（长度百分比，相对参考盒）与 z（px）。</summary>
public readonly struct CssTransformOrigin : IEquatable<CssTransformOrigin>
{
    public CssTransformOrigin(LengthPercentage x, LengthPercentage y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public LengthPercentage X { get; }
    public LengthPercentage Y { get; }
    public float Z { get; }

    public bool Equals(CssTransformOrigin o) => X.Equals(o.X) && Y.Equals(o.Y) && Z == o.Z;
    public override bool Equals(object? obj) => obj is CssTransformOrigin t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine(X.GetHashCode(), Y.GetHashCode(), Z.GetHashCode());
    public static bool operator ==(CssTransformOrigin a, CssTransformOrigin b) => a.Equals(b);
    public static bool operator !=(CssTransformOrigin a, CssTransformOrigin b) => !a.Equals(b);

    public override string ToString() => X + " " + Y + (Z != 0 ? " " + ValueText.Px(Z) : "");
}

/// <summary>
/// 变换矩阵的组合（CSS Transforms 2 §6「当前变换矩阵」）：供绘制、命中测试与合成器使用。
/// 矩阵遵循 System.Numerics 的行向量约定（p' = p · M），坐标单位为 px、y 轴向下。
/// </summary>
public static class CssTransforms
{
    /// <summary>元素是否有变换（<c>transform</c>、<c>translate</c>、<c>rotate</c>、<c>scale</c> 任一不为 none）。</summary>
    public static bool HasTransform(ComputedStyle style)
    {
        if (style is null) throw new ArgumentNullException(nameof(style));
        return !style.Transform.IsNone || !style.Translate.IsNone || !style.Rotate.IsNone || !style.Scale.IsNone;
    }

    /// <summary>
    /// 计算元素的变换矩阵（不含 <c>perspective</c> 属性，它作用于子元素，见 <see cref="GetChildPerspective"/>）。
    /// <paramref name="referenceBox"/> 是按 <c>transform-box</c> 选定的参考盒（在元素的局部坐标中，通常为边框盒 (0, 0, 宽, 高)）。
    /// 顺序：平移到 transform-origin → translate → rotate → scale → transform 列表 → 平移回原点。
    /// </summary>
    public static Matrix4x4 GetTransform(ComputedStyle style, Rect referenceBox)
    {
        if (style is null) throw new ArgumentNullException(nameof(style));
        float w = referenceBox.Width, h = referenceBox.Height;
        var origin = style.TransformOrigin;
        float ox = referenceBox.X + origin.X.Resolve(w), oy = referenceBox.Y + origin.Y.Resolve(h), oz = origin.Z;
        // 列向量的 T(o)·Translate·Rotate·Scale·F1…Fn·T(-o) 对应行向量的逆序乘积
        var m = Matrix4x4.CreateTranslation(-ox, -oy, -oz);
        if (!style.Transform.IsNone) m *= style.Transform.ToMatrix(w, h);
        var s = style.Scale;
        if (!s.IsNone) m *= Matrix4x4.CreateScale(s.X, s.Y, s.Z);
        var r = style.Rotate;
        if (!r.IsNone) m *= Rotation3D(r.AxisX, r.AxisY, r.AxisZ, r.Angle);
        var t = style.Translate;
        if (!t.IsNone) m *= Matrix4x4.CreateTranslation(t.X.Resolve(w), t.Y.Resolve(h), t.Z);
        m *= Matrix4x4.CreateTranslation(ox, oy, oz);
        return m;
    }

    /// <summary>
    /// <c>perspective</c> 属性给子元素的透视矩阵（以 <c>perspective-origin</c> 为中心）；<c>none</c> 返回 null。
    /// <paramref name="referenceBox"/> 为元素的边框盒。
    /// </summary>
    public static Matrix4x4? GetChildPerspective(ComputedStyle style, Rect referenceBox)
    {
        if (style is null) throw new ArgumentNullException(nameof(style));
        float d = style.Perspective;
        if (d < 0) return null;
        var o = style.PerspectiveOrigin;
        float ox = referenceBox.X + o.X.Resolve(referenceBox.Width), oy = referenceBox.Y + o.Y.Resolve(referenceBox.Height);
        var p = Matrix4x4.Identity;
        p.M34 = -1 / Math.Max(1, d);
        return Matrix4x4.CreateTranslation(-ox, -oy, 0) * p * Matrix4x4.CreateTranslation(ox, oy, 0);
    }

    /// <summary>投影到 2D 仿射矩阵（忽略 z 与透视；用于纯 2D 渲染路径）。</summary>
    public static Matrix3x2 To2D(in Matrix4x4 m) => new(m.M11, m.M12, m.M21, m.M22, m.M41, m.M42);

    /// <summary>矩阵是否只含 2D 仿射部分（可无损投影到 <see cref="Matrix3x2"/>）。</summary>
    public static bool Is2D(in Matrix4x4 m)
        => m.M13 == 0 && m.M14 == 0 && m.M23 == 0 && m.M24 == 0 && m.M31 == 0 && m.M32 == 0 && m.M33 == 1 && m.M34 == 0 && m.M43 == 0 && m.M44 == 1;

    /// <summary>CSS 的 rotate3d(x, y, z, angle)（轴自动归一化；零向量为单位矩阵）。</summary>
    internal static Matrix4x4 Rotation3D(float x, float y, float z, float degrees)
    {
        float len = MathF.Sqrt(x * x + y * y + z * z);
        if (len == 0 || float.IsNaN(len)) return Matrix4x4.Identity;
        return Matrix4x4.CreateFromAxisAngle(new Vector3(x / len, y / len, z / len), degrees * MathF.PI / 180);
    }
}

/// <summary>变换相关属性的解析与计算。</summary>
internal static class TransformParsers
{
    internal sealed class SpecifiedFunction
    {
        public CssTransformFunctionKind Kind;
        public CalcNode? X, Y, Z;
        public double A, B, C, D;
        public double[]? Matrix;
    }

    internal sealed class TransformListValue : CssValue
    {
        public TransformListValue(SpecifiedFunction[] functions, CssTransformList? computed)
        {
            Functions = functions;
            Computed = computed;
        }

        public SpecifiedFunction[] Functions { get; }
        public CssTransformList? Computed { get; }
    }

    internal sealed class TranslateValue : CssValue
    {
        public TranslateValue(CalcNode? x, CalcNode? y, CalcNode? z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>null 表示 none。</summary>
        public CalcNode? X { get; }
        public CalcNode? Y { get; }
        public CalcNode? Z { get; }
    }

    internal sealed class OriginValue : CssValue
    {
        public OriginValue(CalcNode x, CalcNode y, CalcNode? z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public CalcNode X { get; }
        public CalcNode Y { get; }
        public CalcNode? Z { get; }
    }

    const int MaxFunctions = 1024;

    public static CssValue? TransformList(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new TransformListValue(Array.Empty<SpecifiedFunction>(), CssTransformList.None);
        var list = new List<SpecifiedFunction>();
        while (!r.AtEnd)
        {
            if (r.Next() is not CssFunction f || list.Count >= MaxFunctions) return null;
            var fn = Function(f);
            if (fn is null) return null;
            list.Add(fn);
        }
        if (list.Count == 0) return null;
        var arr = list.ToArray();
        bool dependent = false;
        foreach (var fn in arr)
            if (fn.X is { IsContextDependent: true } || fn.Y is { IsContextDependent: true } || fn.Z is { IsContextDependent: true }) dependent = true;
        return new TransformListValue(arr, dependent ? null : Compute(arr, new LengthContext(16, 16, s_env)));
    }

    static readonly CssEnvironment s_env = new();

    internal static CssTransformList Compute(SpecifiedFunction[] arr, in LengthContext ctx)
    {
        var result = new CssTransformFunction[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var f = arr[i];
            var x = f.X is null ? default : f.X.Compute(ctx).ToLengthPercentage();
            var y = f.Y is null ? default : f.Y.Compute(ctx).ToLengthPercentage();
            float z = f.Z is null ? 0 : CalcMath.Finite(f.Z.Compute(ctx).Px);
            float[]? m = null;
            if (f.Matrix is not null)
            {
                m = new float[f.Matrix.Length];
                for (int k = 0; k < m.Length; k++) m[k] = (float)f.Matrix[k];
            }
            result[i] = new CssTransformFunction(f.Kind, x, y, z, (float)f.A, (float)f.B, (float)f.C, (float)f.D, m);
        }
        return new CssTransformList(result);
    }

    static SpecifiedFunction? Function(CssFunction f)
    {
        var name = CssSerializer.ToAsciiLower(f.Name);
        var args = SplitArgs(f.Arguments, out bool commas);
        if (args is null) return null;
        var s = new SpecifiedFunction();
        switch (name)
        {
            case "matrix":
            case "matrix3d":
            {
                int n = name == "matrix" ? 6 : 16;
                if (args.Count != n || !commas && n > 1) return null;
                s.Kind = name == "matrix" ? CssTransformFunctionKind.Matrix : CssTransformFunctionKind.Matrix3D;
                s.Matrix = new double[n];
                for (int i = 0; i < n; i++) if (!Number(args[i], out s.Matrix[i])) return null;
                return s;
            }
            case "translate":
                if (args.Count is < 1 or > 2) return null;
                s.Kind = CssTransformFunctionKind.Translate;
                s.X = Lp(args[0]);
                s.Y = args.Count > 1 ? Lp(args[1]) : CalcLeaf.Length(CssLength.Px(0));
                return s.X is null || s.Y is null ? null : s;
            case "translatex":
                if (args.Count != 1 || (s.X = Lp(args[0])) is null) return null;
                s.Kind = CssTransformFunctionKind.TranslateX;
                return s;
            case "translatey":
                if (args.Count != 1 || (s.Y = Lp(args[0])) is null) return null;
                s.Kind = CssTransformFunctionKind.TranslateY;
                return s;
            case "translatez":
                if (args.Count != 1 || (s.Z = Len(args[0])) is null) return null;
                s.Kind = CssTransformFunctionKind.TranslateZ;
                return s;
            case "translate3d":
                if (args.Count != 3 || (s.X = Lp(args[0])) is null || (s.Y = Lp(args[1])) is null || (s.Z = Len(args[2])) is null) return null;
                s.Kind = CssTransformFunctionKind.Translate3D;
                return s;
            case "scale":
                if (args.Count is < 1 or > 2 || !NumPct(args[0], out s.A)) return null;
                s.B = s.A;
                if (args.Count == 2 && !NumPct(args[1], out s.B)) return null;
                s.C = 1;
                s.Kind = CssTransformFunctionKind.Scale;
                return s;
            case "scalex":
                if (args.Count != 1 || !NumPct(args[0], out s.A)) return null;
                s.B = s.C = 1;
                s.Kind = CssTransformFunctionKind.ScaleX;
                return s;
            case "scaley":
                if (args.Count != 1 || !NumPct(args[0], out s.B)) return null;
                s.A = s.C = 1;
                s.Kind = CssTransformFunctionKind.ScaleY;
                return s;
            case "scalez":
                if (args.Count != 1 || !NumPct(args[0], out s.C)) return null;
                s.A = s.B = 1;
                s.Kind = CssTransformFunctionKind.ScaleZ;
                return s;
            case "scale3d":
                if (args.Count != 3 || !NumPct(args[0], out s.A) || !NumPct(args[1], out s.B) || !NumPct(args[2], out s.C)) return null;
                s.Kind = CssTransformFunctionKind.Scale3D;
                return s;
            case "rotate":
            case "rotatez":
            case "rotatex":
            case "rotatey":
                if (args.Count != 1 || !Angle(args[0], out s.A)) return null;
                s.Kind = name switch
                {
                    "rotate" => CssTransformFunctionKind.Rotate,
                    "rotatez" => CssTransformFunctionKind.RotateZ,
                    "rotatex" => CssTransformFunctionKind.RotateX,
                    _ => CssTransformFunctionKind.RotateY,
                };
                return s;
            case "rotate3d":
                if (args.Count != 4 || !Number(args[0], out s.B) || !Number(args[1], out s.C) || !Number(args[2], out s.D) || !Angle(args[3], out s.A)) return null;
                s.Kind = CssTransformFunctionKind.Rotate3D;
                return s;
            case "skew":
                if (args.Count is < 1 or > 2 || !Angle(args[0], out s.A)) return null;
                if (args.Count == 2 && !Angle(args[1], out s.B)) return null;
                s.Kind = CssTransformFunctionKind.Skew;
                return s;
            case "skewx":
                if (args.Count != 1 || !Angle(args[0], out s.A)) return null;
                s.Kind = CssTransformFunctionKind.SkewX;
                return s;
            case "skewy":
                if (args.Count != 1 || !Angle(args[0], out s.B)) return null;
                s.Kind = CssTransformFunctionKind.SkewY;
                return s;
            case "perspective":
                if (args.Count != 1) return null;
                s.Kind = CssTransformFunctionKind.Perspective;
                if (args[0] is CssToken pt && pt.IsIdent("none")) { s.A = double.PositiveInfinity; return s; }
                // perspective() 的长度只能是绝对的或可在计算期换算的：这里在解析期以默认上下文求值（em 按 16px），已足够
                var node = Len(args[0]);
                if (node is null || node is CalcLeaf { Value: < 0 }) return null;
                s.A = Math.Max(0, node.Compute(new LengthContext(16, 16, s_env)).Px);
                return s;
        }
        return null;
    }

    /// <summary>按逗号拆分参数（每个参数恰好一个组件值）；参数间没有逗号时按空白拆分（只对单参数函数有意义）。</summary>
    static List<CssComponentValue>? SplitArgs(IReadOnlyList<CssComponentValue> args, out bool commas)
    {
        var list = new List<CssComponentValue>();
        commas = false;
        var r = new ValueReader(args);
        while (!r.AtEnd)
        {
            if (list.Count > 0)
            {
                if (!r.TryComma()) return null;
                commas = true;
            }
            var v = r.Next();
            if (v is null || v is CssToken { Type: CssTokenType.Comma }) return null;
            list.Add(v);
            if (list.Count > 16) return null;
        }
        return list;
    }

    static CalcNode? Lp(CssComponentValue v) => ValueParsers.LengthPercentageNode(v, nonNegative: false);

    static CalcNode? Len(CssComponentValue v) => ValueParsers.LengthPercentageNode(v, nonNegative: false, allowPercent: false);

    static bool Number(CssComponentValue v, out double n)
    {
        n = 0;
        var node = ValueParsers.NumberNode(v, nonNegative: false);
        if (node is null) return false;
        n = CalcMath.FiniteDouble(node.Compute(new LengthContext(16, 16, s_env)).Number);
        return true;
    }

    /// <summary>数或百分比（百分比按比例：50% = 0.5）。</summary>
    internal static bool NumPct(CssComponentValue v, out double n)
    {
        n = 0;
        var node = CalcParser.ParseComponent(v, PercentMode.Number, allowUnitlessZero: false);
        if (node is null || node.Category != CalcCategory.Number) return false;
        n = CalcMath.FiniteDouble(node.Compute(new LengthContext(16, 16, s_env)).Number);
        return true;
    }

    static bool Angle(CssComponentValue v, out double degrees) => ValueParsers.TryAngle(v, out degrees);

    // ---------------- 独立变换属性 ----------------

    /// <summary><c>translate: none | &lt;length-percentage&gt; [ &lt;length-percentage&gt; &lt;length&gt;? ]?</c></summary>
    public static CssValue? Translate(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new TranslateValue(null, null, null);
        var x = Lp(r.Next()!);
        if (x is null) return null;
        CalcNode? y = null, z = null;
        if (!r.AtEnd)
        {
            y = r.Peek() is { } yv ? Lp(yv) : null;
            if (y is null) return null;
            r.Advance();
            if (!r.AtEnd)
            {
                z = r.Peek() is { } zv ? Len(zv) : null;
                if (z is null) return null;
                r.Advance();
            }
        }
        return new TranslateValue(x, y ?? CalcLeaf.Length(CssLength.Px(0)), z);
    }

    public static CssTranslate ComputeTranslate(CssValue v, in LengthContext ctx)
    {
        var t = (TranslateValue)v;
        if (t.X is null) return CssTranslate.None;
        return new CssTranslate(t.X.Compute(ctx).ToLengthPercentage(), t.Y!.Compute(ctx).ToLengthPercentage(), t.Z is null ? 0 : CalcMath.Finite(t.Z.Compute(ctx).Px));
    }

    /// <summary><c>rotate: none | &lt;angle&gt; | [ x | y | z | &lt;number&gt;{3} ] &amp;&amp; &lt;angle&gt;</c></summary>
    public static CssValue? Rotate(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new PrecomputedValue<CssRotate>(CssRotate.None);
        double? angle = null;
        double ax = 0, ay = 0, az = 1;
        bool axis = false;
        for (int i = 0; i < 2 && !r.AtEnd; i++)
        {
            var v = r.Peek()!;
            if (angle is null && Angle(v, out var a) && !(v is CssToken { Type: CssTokenType.Number })) { r.Advance(); angle = a; continue; }
            if (!axis)
            {
                var id = r.PeekIdentLower();
                if (id is "x" or "y" or "z")
                {
                    r.Advance();
                    (ax, ay, az) = id == "x" ? (1d, 0d, 0d) : id == "y" ? (0d, 1d, 0d) : (0d, 0d, 1d);
                    axis = true;
                    continue;
                }
                int save = r.Position;
                if (Number(r.Next()!, out ax) && !r.AtEnd && Number(r.Next()!, out ay) && !r.AtEnd && Number(r.Next()!, out az))
                {
                    axis = true;
                    continue;
                }
                r.Position = save;
            }
            // 单独的 0 作为角度
            if (angle is null && v is CssToken { Type: CssTokenType.Number, Number: 0 }) { r.Advance(); angle = 0; continue; }
            return null;
        }
        if (angle is null || !r.AtEnd) return null;
        return new PrecomputedValue<CssRotate>(new CssRotate((float)ax, (float)ay, (float)az, (float)angle.Value));
    }

    /// <summary><c>scale: none | [ &lt;number&gt; | &lt;percentage&gt; ]{1,3}</c></summary>
    public static CssValue? Scale(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new PrecomputedValue<CssScale>(CssScale.None);
        Span<double> v = stackalloc double[3];
        int n = 0;
        while (!r.AtEnd)
        {
            if (n == 3 || !NumPct(r.Next()!, out v[n])) return null;
            n++;
        }
        if (n == 0) return null;
        double x = v[0], y = n > 1 ? v[1] : x, z = n > 2 ? v[2] : 1;
        return new PrecomputedValue<CssScale>(new CssScale((float)x, (float)y, (float)z));
    }

    /// <summary>
    /// <c>transform-origin</c>：一个或两个 &lt;position&gt; 关键字/长度百分比，可再跟一个 z 长度（CSS Transforms 1 §7）。
    /// </summary>
    public static CssValue? Origin(ref ValueReader r)
    {
        var items = new List<CssComponentValue>(3);
        while (!r.AtEnd && items.Count < 4) items.Add(r.Next()!);
        if (items.Count is 0 or > 3) return null;
        CalcNode? x = null, y = null, z = null;
        string? K(CssComponentValue c) => c is CssToken { Type: CssTokenType.Ident } t ? CssSerializer.ToAsciiLower(t.Value) : null;
        if (items.Count == 1)
        {
            var k = K(items[0]);
            if (k is null) { x = Lp(items[0]); y = CalcLeaf.Percent(50); }
            else if (k is "top" or "bottom") { x = CalcLeaf.Percent(50); y = Edge(k); }
            else if (k is "left" or "right" or "center") { x = Edge(k); y = CalcLeaf.Percent(50); }
            if (x is null) return null;
            return new OriginValue(x, y!, null);
        }
        var k0 = K(items[0]);
        var k1 = K(items[1]);
        // 两个都是关键字时可交换（top left）；含长度时第一个是水平
        if (k0 is "top" or "bottom" || k1 is "left" or "right")
        {
            if (k0 is "left" or "right" || k1 is "top" or "bottom") return null;
            if ((k0 is null) || (k1 is null)) return null;
            (k0, k1) = (k1, k0);
            (items[0], items[1]) = (items[1], items[0]);
        }
        if (k0 is null) x = Lp(items[0]);
        else if (k0 is "left" or "right" or "center") x = Edge(k0);
        if (k1 is null) y = Lp(items[1]);
        else if (k1 is "top" or "bottom" or "center") y = Edge(k1);
        if (x is null || y is null) return null;
        if (items.Count == 3)
        {
            z = Len(items[2]);
            if (z is null) return null;
        }
        return new OriginValue(x, y, z);

        static CalcNode Edge(string k) => k switch
        {
            "left" or "top" => CalcLeaf.Percent(0),
            "right" or "bottom" => CalcLeaf.Percent(100),
            _ => CalcLeaf.Percent(50),
        };
    }

    public static CssTransformOrigin ComputeOrigin(CssValue v, in LengthContext ctx)
    {
        var o = (OriginValue)v;
        return new CssTransformOrigin(o.X.Compute(ctx).ToLengthPercentage(), o.Y.Compute(ctx).ToLengthPercentage(), o.Z is null ? 0 : CalcMath.Finite(o.Z.Compute(ctx).Px));
    }

    /// <summary><c>perspective: none | &lt;length [0,∞]&gt;</c>；计算值 px，none 为 -1。</summary>
    public static CssValue? Perspective(ref ValueReader r)
    {
        if (r.TryIdent("none")) return new NumericValue(CalcLeaf.Number(-1));
        var node = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: true, allowPercent: false);
        return node is null ? null : new NumericValue(node);
    }
}
