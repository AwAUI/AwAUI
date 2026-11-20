using System;
using System.Numerics;
using AwAUI.Css.Values;

namespace AwAUI.Css.Animation;

/// <summary>
/// 变换列表的插值（CSS Transforms 2 §16）：
/// <list type="number">
/// <item>两端都是 <c>none</c>：<c>none</c>；一端是 <c>none</c> 时换成另一端各函数的恒等函数。</item>
/// <item>从头逐对比较：两个函数有共同的「基元」（translate / scale / rotate / skew / perspective，2D 与 3D 取较大者）时按基元的参数插值；
/// 较短的列表用对方函数的恒等函数补齐；<c>matrix()</c> 与 <c>matrix3d()</c> 逐对按矩阵分解插值。</item>
/// <item>遇到第一对没有共同基元的函数：从这里起两边剩下的函数各自合成一个矩阵，按矩阵分解（平移、缩放、错切、透视、四元数旋转）插值。
/// 剩余部分含百分比时矩阵依赖参考盒，结果是延迟的 <see cref="CssTransformFunctionKind.Interpolated"/> 函数，在绘制时按盒求值。</item>
/// </list>
/// 矩阵分解与重组按 CSS Transforms 2 §decomposing a 3D matrix（与 Chromium 相同，2D 矩阵也走这条路径，旋转按最短路径的四元数球面插值）；
/// 分解失败（奇异矩阵）时离散。矩阵约定与 <see cref="CssTransformList.ToMatrix"/> 相同（System.Numerics 行向量）。
/// </summary>
public static class TransformInterpolation
{
    /// <summary>延迟插值的最大嵌套层数（过渡反复反转、且含百分比时每次嵌套一层）：超过后按离散处理，保证求值开销有界。</summary>
    const int MaxDeferredDepth = 8;

    /// <summary>在进度 <paramref name="progress"/> 上插值两个变换列表。</summary>
    public static CssTransformList Interpolate(CssTransformList from, CssTransformList to, double progress)
    {
        if (from is null) throw new ArgumentNullException(nameof(from));
        if (to is null) throw new ArgumentNullException(nameof(to));
        if (from.Equals(to)) return from;
        if (from.IsNone && to.IsNone) return from;
        var fa = from.Functions;
        var fb = to.Functions;
        int n = Math.Max(fa.Count, fb.Count);
        var result = new CssTransformFunction[n];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var a = i < fa.Count ? fa[i] : Identity(fb[i]);
            var b = i < fb.Count ? fb[i] : Identity(a);
            var r = InterpolatePrimitive(a, b, progress);
            if (r is null)
            {
                // 从第 i 项起合成矩阵插值
                var restA = Slice(from, i);
                var restB = Slice(to, i);
                var m = InterpolateRest(restA, restB, progress);
                if (m is null) return progress < 0.5 ? from : to;
                result[k++] = m;
                Array.Resize(ref result, k);
                return new CssTransformList(result);
            }
            result[k++] = r;
        }
        return new CssTransformList(result);
    }

    /// <summary>加法合成：列表连接（<c>add</c>）。</summary>
    public static CssTransformList Concat(CssTransformList a, CssTransformList b)
    {
        if (a.IsNone) return b;
        if (b.IsNone) return a;
        var items = new CssTransformFunction[a.Functions.Count + b.Functions.Count];
        int k = 0;
        foreach (var f in a.Functions) items[k++] = f;
        foreach (var f in b.Functions) items[k++] = f;
        return new CssTransformList(items);
    }

    /// <summary>
    /// 累加（<c>accumulate</c>，CSS Transforms 2 §17）：函数逐对有共同基元时参数相加（缩放按 (a − 1) + (b − 1) + 1），否则退化为连接。
    /// </summary>
    public static CssTransformList Accumulate(CssTransformList a, CssTransformList b)
    {
        if (a.IsNone) return b;
        if (b.IsNone) return a;
        if (a.Functions.Count != b.Functions.Count) return Concat(a, b);
        var items = new CssTransformFunction[a.Functions.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var x = a.Functions[i];
            var y = b.Functions[i];
            var px = PrimitiveOf(x.Kind);
            if (px != PrimitiveOf(y.Kind) || px is Primitive.Matrix or Primitive.None) return Concat(a, b);
            switch (px)
            {
                case Primitive.Translate:
                    items[i] = Make(Merge(x.Kind, y.Kind), CssInterpolation.Combine(x.X, 1, y.X, 1), CssInterpolation.Combine(x.Y, 1, y.Y, 1), x.Z + y.Z, 0, 0, 0, 0);
                    break;
                case Primitive.Scale:
                    items[i] = Make(Merge(x.Kind, y.Kind), default, default, 0, x.A + y.A - 1, x.B + y.B - 1, x.C + y.C - 1, 0);
                    break;
                case Primitive.Skew:
                    items[i] = Make(Merge(x.Kind, y.Kind), default, default, 0, x.A + y.A, x.B + y.B, 0, 0);
                    break;
                case Primitive.Rotate:
                    if (!SameRotationAxis(x, y)) return Concat(a, b);
                    items[i] = Make(x.Kind, default, default, 0, x.A + y.A, x.B, x.C, x.D);
                    break;
                default:
                    return Concat(a, b);
            }
        }
        return new CssTransformList(items);
    }

    internal static bool Is3D(CssTransformList list)
    {
        foreach (var f in list.Functions) if (f.Is3D) return true;
        return false;
    }

    // ================= 基元 =================

    enum Primitive : byte { None, Translate, Scale, Rotate, Skew, Perspective, Matrix }

    static Primitive PrimitiveOf(CssTransformFunctionKind k) => k switch
    {
        CssTransformFunctionKind.Translate or CssTransformFunctionKind.TranslateX or CssTransformFunctionKind.TranslateY
            or CssTransformFunctionKind.TranslateZ or CssTransformFunctionKind.Translate3D => Primitive.Translate,
        CssTransformFunctionKind.Scale or CssTransformFunctionKind.ScaleX or CssTransformFunctionKind.ScaleY
            or CssTransformFunctionKind.ScaleZ or CssTransformFunctionKind.Scale3D => Primitive.Scale,
        CssTransformFunctionKind.Rotate or CssTransformFunctionKind.RotateX or CssTransformFunctionKind.RotateY
            or CssTransformFunctionKind.RotateZ or CssTransformFunctionKind.Rotate3D => Primitive.Rotate,
        CssTransformFunctionKind.Skew or CssTransformFunctionKind.SkewX or CssTransformFunctionKind.SkewY => Primitive.Skew,
        CssTransformFunctionKind.Perspective => Primitive.Perspective,
        CssTransformFunctionKind.Matrix or CssTransformFunctionKind.Matrix3D => Primitive.Matrix,
        _ => Primitive.None,
    };

    static bool Is3DKind(CssTransformFunctionKind k) => k is CssTransformFunctionKind.TranslateZ or CssTransformFunctionKind.Translate3D
        or CssTransformFunctionKind.ScaleZ or CssTransformFunctionKind.Scale3D or CssTransformFunctionKind.RotateX or CssTransformFunctionKind.RotateY
        or CssTransformFunctionKind.Rotate3D or CssTransformFunctionKind.Matrix3D;

    /// <summary>两个同基元的函数插值后的种类：相同保留原名，否则取基元（2D / 3D 取较大者）。</summary>
    static CssTransformFunctionKind Merge(CssTransformFunctionKind a, CssTransformFunctionKind b)
    {
        if (a == b) return a;
        bool is3D = Is3DKind(a) || Is3DKind(b);
        return PrimitiveOf(a) switch
        {
            Primitive.Translate => is3D ? CssTransformFunctionKind.Translate3D : CssTransformFunctionKind.Translate,
            Primitive.Scale => is3D ? CssTransformFunctionKind.Scale3D : CssTransformFunctionKind.Scale,
            Primitive.Rotate => is3D ? CssTransformFunctionKind.Rotate3D : CssTransformFunctionKind.Rotate,
            Primitive.Skew => CssTransformFunctionKind.Skew,
            _ => a,
        };
    }

    static CssTransformFunction Make(CssTransformFunctionKind kind, LengthPercentage x, LengthPercentage y, float z, float a, float b, float c, float d)
        => new(kind, x, y, z, a, b, c, d, null);

    static CssTransformFunction MakeMatrix(in Matrix4x4 m)
    {
        if (Is2DAffine(m)) return new CssTransformFunction(CssTransformFunctionKind.Matrix, default, default, 0, 0, 0, 0, 0, new[] { m.M11, m.M12, m.M21, m.M22, m.M41, m.M42 });
        return new CssTransformFunction(CssTransformFunctionKind.Matrix3D, default, default, 0, 0, 0, 0, 0,
            new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 });
    }

    static bool Is2DAffine(in Matrix4x4 m) =>
        m.M13 == 0 && m.M14 == 0 && m.M23 == 0 && m.M24 == 0 && m.M31 == 0 && m.M32 == 0 && m.M33 == 1 && m.M34 == 0 && m.M43 == 0 && m.M44 == 1;

    /// <summary>与 <paramref name="f"/> 对应的恒等函数（补齐较短的列表、替换 <c>none</c>）。</summary>
    static CssTransformFunction Identity(CssTransformFunction f)
    {
        switch (PrimitiveOf(f.Kind))
        {
            case Primitive.Translate: return Make(f.Kind, default, default, 0, 0, 0, 0, 0);
            case Primitive.Scale: return Make(f.Kind, default, default, 0, 1, 1, 1, 0);
            case Primitive.Rotate: return Make(f.Kind, default, default, 0, 0, f.B, f.C, f.D);
            case Primitive.Skew: return Make(f.Kind, default, default, 0, 0, 0, 0, 0);
            case Primitive.Perspective: return Make(f.Kind, default, default, 0, float.PositiveInfinity, 0, 0, 0);
            default: return MakeMatrix(Matrix4x4.Identity);
        }
    }

    static void RotationAxis(CssTransformFunction f, out double x, out double y, out double z)
    {
        switch (f.Kind)
        {
            case CssTransformFunctionKind.RotateX: x = 1; y = 0; z = 0; return;
            case CssTransformFunctionKind.RotateY: x = 0; y = 1; z = 0; return;
            case CssTransformFunctionKind.Rotate3D: x = f.B; y = f.C; z = f.D; return;
            default: x = 0; y = 0; z = 1; return;
        }
    }

    static bool SameRotationAxis(CssTransformFunction a, CssTransformFunction b)
    {
        RotationAxis(a, out var ax, out var ay, out var az);
        RotationAxis(b, out var bx, out var by, out var bz);
        double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
        if (la == 0 || lb == 0) return la == lb;
        double dx = ax / la - bx / lb, dy = ay / la - by / lb, dz = az / la - bz / lb;
        return dx * dx + dy * dy + dz * dz < 1e-12;
    }

    static float L(double a, double b, double p) => CalcMath.Finite(a + (b - a) * p);

    /// <summary>同基元的一对函数插值；没有共同基元时返回 null。</summary>
    static CssTransformFunction? InterpolatePrimitive(CssTransformFunction a, CssTransformFunction b, double p)
    {
        var pa = PrimitiveOf(a.Kind);
        if (pa != PrimitiveOf(b.Kind) || pa == Primitive.None) return null;
        var kind = Merge(a.Kind, b.Kind);
        switch (pa)
        {
            case Primitive.Translate:
                return Make(kind, CssInterpolation.LerpLength(a.X, b.X, p, false), CssInterpolation.LerpLength(a.Y, b.Y, p, false), L(a.Z, b.Z, p), 0, 0, 0, 0);
            case Primitive.Scale:
                return Make(kind, default, default, 0, L(a.A, b.A, p), L(a.B, b.B, p), L(a.C, b.C, p), 0);
            case Primitive.Skew:
                return Make(kind, default, default, 0, L(a.A, b.A, p), L(a.B, b.B, p), 0, 0);
            case Primitive.Perspective:
            {
                // 按倒数插值（none = +∞ 的倒数为 0），与 Chromium 相同
                double ia = float.IsInfinity(a.A) ? 0 : 1 / Math.Max(1, (double)a.A);
                double ib = float.IsInfinity(b.A) ? 0 : 1 / Math.Max(1, (double)b.A);
                double inv = ia + (ib - ia) * p;
                return Make(kind, default, default, 0, inv <= 0 ? float.PositiveInfinity : CalcMath.Finite(Math.Max(1, 1 / inv)), 0, 0, 0);
            }
            case Primitive.Rotate:
            {
                if (SameRotationAxis(a, b))
                {
                    var src = a.Kind == kind ? a : b.Kind == kind ? b : a;
                    RotationAxis(src, out var x, out var y, out var z);
                    return Make(kind, default, default, 0, L(a.A, b.A, p), (float)x, (float)y, (float)z);
                }
                // 轴不同：四元数球面插值，结果为 rotate3d()
                RotationAxis(a, out var ax, out var ay, out var az);
                RotationAxis(b, out var bx, out var by, out var bz);
                var q = Slerp(AxisAngleToQuaternion(ax, ay, az, a.A), AxisAngleToQuaternion(bx, by, bz, b.A), p);
                QuaternionToAxisAngle(q, out var rx, out var ry, out var rz, out var angle);
                return Make(CssTransformFunctionKind.Rotate3D, default, default, 0, CalcMath.Finite(angle), (float)rx, (float)ry, (float)rz);
            }
            default:
            {
                // matrix() / matrix3d()：按矩阵分解插值
                var m = InterpolateMatrices(a.ToMatrix(0, 0), b.ToMatrix(0, 0), p);
                return MakeMatrix(m);
            }
        }
    }

    static CssTransformList Slice(CssTransformList list, int start)
    {
        int n = list.Functions.Count - start;
        if (n <= 0) return CssTransformList.None;
        var items = new CssTransformFunction[n];
        for (int i = 0; i < n; i++) items[i] = list.Functions[start + i];
        return new CssTransformList(items);
    }

    static bool DependsOnReferenceBox(CssTransformList list)
    {
        foreach (var f in list.Functions)
        {
            if (f.Kind == CssTransformFunctionKind.Interpolated) return true;
            if (PrimitiveOf(f.Kind) == Primitive.Translate && (!f.X.IsFixed || !f.Y.IsFixed)) return true;
        }
        return false;
    }

    static int DeferredDepth(CssTransformList list, int depth)
    {
        int max = depth;
        if (depth > MaxDeferredDepth) return depth;
        foreach (var f in list.Functions)
        {
            if (f.Kind != CssTransformFunctionKind.Interpolated) continue;
            max = Math.Max(max, Math.Max(DeferredDepth(f.InterpolatedFrom!, depth + 1), DeferredDepth(f.InterpolatedTo!, depth + 1)));
        }
        return max;
    }

    /// <summary>剩余部分的矩阵插值；矩阵依赖参考盒时返回延迟函数。分解失败返回 null（整体离散）。</summary>
    static CssTransformFunction? InterpolateRest(CssTransformList a, CssTransformList b, double p)
    {
        if (DependsOnReferenceBox(a) || DependsOnReferenceBox(b))
        {
            if (Math.Max(DeferredDepth(a, 0), DeferredDepth(b, 0)) >= MaxDeferredDepth) return null;
            return new CssTransformFunction(a, b, p);
        }
        var ma = a.ToMatrix(0, 0);
        var mb = b.ToMatrix(0, 0);
        if (!Decompose(ma, out _) || !Decompose(mb, out _)) return null;
        return MakeMatrix(InterpolateMatrices(ma, mb, p));
    }

    // ================= 矩阵分解 =================

    internal struct Decomposed
    {
        public double TX, TY, TZ;
        public double SX, SY, SZ;
        public double SkewXY, SkewXZ, SkewYZ;
        public double PX, PY, PZ, PW;
        public Quat Q;
    }

    internal readonly struct Quat
    {
        public Quat(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public readonly double X, Y, Z, W;
    }

    /// <summary>
    /// 按分解插值两个矩阵：平移、缩放、错切、透视线性插值，旋转四元数球面插值，再重组。任一矩阵不可分解时按离散（进度 &lt; 0.5 取起点）。
    /// </summary>
    public static Matrix4x4 InterpolateMatrices(Matrix4x4 from, Matrix4x4 to, double progress)
    {
        if (!Decompose(from, out var a) || !Decompose(to, out var b)) return progress < 0.5 ? from : to;
        double p = progress;
        var d = new Decomposed
        {
            TX = a.TX + (b.TX - a.TX) * p,
            TY = a.TY + (b.TY - a.TY) * p,
            TZ = a.TZ + (b.TZ - a.TZ) * p,
            SX = a.SX + (b.SX - a.SX) * p,
            SY = a.SY + (b.SY - a.SY) * p,
            SZ = a.SZ + (b.SZ - a.SZ) * p,
            SkewXY = a.SkewXY + (b.SkewXY - a.SkewXY) * p,
            SkewXZ = a.SkewXZ + (b.SkewXZ - a.SkewXZ) * p,
            SkewYZ = a.SkewYZ + (b.SkewYZ - a.SkewYZ) * p,
            PX = a.PX + (b.PX - a.PX) * p,
            PY = a.PY + (b.PY - a.PY) * p,
            PZ = a.PZ + (b.PZ - a.PZ) * p,
            PW = a.PW + (b.PW - a.PW) * p,
            Q = Slerp(a.Q, b.Q, p),
        };
        return Recompose(d);
    }

    /// <summary>CSS Transforms 2 §decomposing a 3D matrix（行向量约定，m[i][j] 即 M(i+1)(j+1)）。</summary>
    internal static bool Decompose(in Matrix4x4 matrix, out Decomposed result)
    {
        result = default;
        var m = new double[4, 4]
        {
            { matrix.M11, matrix.M12, matrix.M13, matrix.M14 },
            { matrix.M21, matrix.M22, matrix.M23, matrix.M24 },
            { matrix.M31, matrix.M32, matrix.M33, matrix.M34 },
            { matrix.M41, matrix.M42, matrix.M43, matrix.M44 },
        };
        if (m[3, 3] == 0 || !IsFinite(m)) return false;
        double w = m[3, 3];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) m[i, j] /= w;

        // 透视矩阵：上 3×3 的奇异性检查
        var pm = (double[,])m.Clone();
        for (int i = 0; i < 3; i++) pm[i, 3] = 0;
        pm[3, 3] = 1;
        if (Math.Abs(Determinant(pm)) < 1e-12) return false;

        if (m[0, 3] != 0 || m[1, 3] != 0 || m[2, 3] != 0)
        {
            var rhs = new[] { m[0, 3], m[1, 3], m[2, 3], m[3, 3] };
            if (!Invert(pm, out var inv)) return false;
            // perspective = rhs · transpose(inverse(pm))：行向量乘以转置，即 inverse 的每一行与 rhs 点积
            result.PX = inv[0, 0] * rhs[0] + inv[0, 1] * rhs[1] + inv[0, 2] * rhs[2] + inv[0, 3] * rhs[3];
            result.PY = inv[1, 0] * rhs[0] + inv[1, 1] * rhs[1] + inv[1, 2] * rhs[2] + inv[1, 3] * rhs[3];
            result.PZ = inv[2, 0] * rhs[0] + inv[2, 1] * rhs[1] + inv[2, 2] * rhs[2] + inv[2, 3] * rhs[3];
            result.PW = inv[3, 0] * rhs[0] + inv[3, 1] * rhs[1] + inv[3, 2] * rhs[2] + inv[3, 3] * rhs[3];
        }
        else
        {
            result.PW = 1;
        }

        result.TX = m[3, 0];
        result.TY = m[3, 1];
        result.TZ = m[3, 2];

        var row0 = new[] { m[0, 0], m[0, 1], m[0, 2] };
        var row1 = new[] { m[1, 0], m[1, 1], m[1, 2] };
        var row2 = new[] { m[2, 0], m[2, 1], m[2, 2] };

        result.SX = Length(row0);
        Normalize(row0);
        result.SkewXY = Dot(row0, row1);
        Combine(row1, row0, 1, -result.SkewXY);
        result.SY = Length(row1);
        Normalize(row1);
        if (result.SY != 0) result.SkewXY /= result.SY;
        result.SkewXZ = Dot(row0, row2);
        Combine(row2, row0, 1, -result.SkewXZ);
        result.SkewYZ = Dot(row1, row2);
        Combine(row2, row1, 1, -result.SkewYZ);
        result.SZ = Length(row2);
        Normalize(row2);
        if (result.SZ != 0)
        {
            result.SkewXZ /= result.SZ;
            result.SkewYZ /= result.SZ;
        }

        // 坐标系翻转：行列式为 -1 时取反
        var pdum3 = Cross(row1, row2);
        if (Dot(row0, pdum3) < 0)
        {
            result.SX = -result.SX;
            result.SY = -result.SY;
            result.SZ = -result.SZ;
            for (int i = 0; i < 3; i++)
            {
                row0[i] = -row0[i];
                row1[i] = -row1[i];
                row2[i] = -row2[i];
            }
        }

        double qx = 0.5 * Math.Sqrt(Math.Max(1 + row0[0] - row1[1] - row2[2], 0));
        double qy = 0.5 * Math.Sqrt(Math.Max(1 - row0[0] + row1[1] - row2[2], 0));
        double qz = 0.5 * Math.Sqrt(Math.Max(1 - row0[0] - row1[1] + row2[2], 0));
        double qw = 0.5 * Math.Sqrt(Math.Max(1 + row0[0] + row1[1] + row2[2], 0));
        if (row2[1] > row1[2]) qx = -qx;
        if (row0[2] > row2[0]) qy = -qy;
        if (row1[0] > row0[1]) qz = -qz;
        result.Q = new Quat(qx, qy, qz, qw);
        return true;
    }

    /// <summary>
    /// CSS Transforms 2 §recomposing to a 3D matrix（按 Chromium 的实现）：在列向量约定下合成 P·T·R·K<sub>yz</sub>·K<sub>xz</sub>·K<sub>xy</sub>·S，
    /// 再转置为行向量约定。分解中的 <c>row[i]</c> 是列向量约定矩阵的第 i 列（即行向量约定矩阵的第 i 行），两者一致。
    /// </summary>
    internal static Matrix4x4 Recompose(in Decomposed d)
    {
        // c[r, col]：列向量约定
        var c = Identity4();
        c[3, 0] = d.PX;
        c[3, 1] = d.PY;
        c[3, 2] = d.PZ;
        c[3, 3] = d.PW;
        var t = Identity4();
        t[0, 3] = d.TX;
        t[1, 3] = d.TY;
        t[2, 3] = d.TZ;
        c = Multiply(c, t);

        double x = d.Q.X, y = d.Q.Y, z = d.Q.Z, w = d.Q.W;
        var r = Identity4();
        r[0, 0] = 1 - 2 * (y * y + z * z);
        r[0, 1] = 2 * (x * y - z * w);
        r[0, 2] = 2 * (x * z + y * w);
        r[1, 0] = 2 * (x * y + z * w);
        r[1, 1] = 1 - 2 * (x * x + z * z);
        r[1, 2] = 2 * (y * z - x * w);
        r[2, 0] = 2 * (x * z - y * w);
        r[2, 1] = 2 * (y * z + x * w);
        r[2, 2] = 1 - 2 * (x * x + y * y);
        c = Multiply(c, r);

        if (d.SkewYZ != 0)
        {
            var k = Identity4();
            k[1, 2] = d.SkewYZ;
            c = Multiply(c, k);
        }
        if (d.SkewXZ != 0)
        {
            var k = Identity4();
            k[0, 2] = d.SkewXZ;
            c = Multiply(c, k);
        }
        if (d.SkewXY != 0)
        {
            var k = Identity4();
            k[0, 1] = d.SkewXY;
            c = Multiply(c, k);
        }
        var s = new[] { d.SX, d.SY, d.SZ };
        for (int col = 0; col < 3; col++)
            for (int row = 0; row < 4; row++) c[row, col] *= s[col];

        // 转置为行向量约定
        return new Matrix4x4(
            F(c[0, 0]), F(c[1, 0]), F(c[2, 0]), F(c[3, 0]),
            F(c[0, 1]), F(c[1, 1]), F(c[2, 1]), F(c[3, 1]),
            F(c[0, 2]), F(c[1, 2]), F(c[2, 2]), F(c[3, 2]),
            F(c[0, 3]), F(c[1, 3]), F(c[2, 3]), F(c[3, 3]));

        static float F(double v)
        {
            float f = CalcMath.Finite(v);
            return Math.Abs(f) < 1e-7f ? 0 : f;
        }
    }

    // ================= 四元数 =================

    /// <summary>轴角（度）转单位四元数 (axis·sin(θ/2), cos(θ/2))，与 <see cref="Recompose"/> 的旋转矩阵及 CSS 的 <c>rotate3d()</c> 一致。</summary>
    internal static Quat AxisAngleToQuaternion(double x, double y, double z, double degrees)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len == 0) return new Quat(0, 0, 0, 1);
        double half = degrees * Math.PI / 360;
        double s = Math.Sin(half) / len;
        return new Quat(x * s, y * s, z * s, Math.Cos(half));
    }

    internal static void QuaternionToAxisAngle(in Quat q, out double x, out double y, out double z, out double degrees)
    {
        double w = Math.Max(-1, Math.Min(1, q.W));
        double s = Math.Sqrt(Math.Max(0, 1 - w * w));
        degrees = 2 * Math.Acos(w) * 180 / Math.PI;
        if (s < 1e-9)
        {
            x = 0;
            y = 0;
            z = 1;
            degrees = 0;
            return;
        }
        x = q.X / s;
        y = q.Y / s;
        z = q.Z / s;
    }

    /// <summary>Hamilton 积 a⊗b：对应的旋转先应用 b，再应用 a。</summary>
    internal static Quat Multiply(in Quat a, in Quat b)
        => new(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>球面线性插值（CSS Transforms 2 §interpolation of decomposed 3D matrix values，取最短路径）。</summary>
    internal static Quat Slerp(in Quat a0, in Quat b, double p)
    {
        var a = a0;
        double product = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        // 半角超过 90°：q 与 −q 表示同一旋转，翻转起点以走最短路径（与 Chromium 的 Quaternion::Slerp 相同）
        if (product < 0)
        {
            a = new Quat(-a.X, -a.Y, -a.Z, -a.W);
            product = -product;
        }
        product = Math.Min(1, product);
        if (product >= 1 - 1e-12) return new Quat(a.X + (b.X - a.X) * p, a.Y + (b.Y - a.Y) * p, a.Z + (b.Z - a.Z) * p, a.W + (b.W - a.W) * p);
        double theta = Math.Acos(product);
        double w = Math.Sin(p * theta) / Math.Sqrt(1 - product * product);
        double ca = Math.Cos(p * theta) - product * w;
        return new Quat(a.X * ca + b.X * w, a.Y * ca + b.Y * w, a.Z * ca + b.Z * w, a.W * ca + b.W * w);
    }

    // ================= 矩阵辅助 =================

    static bool IsFinite(double[,] m)
    {
        foreach (var v in m) if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        return true;
    }

    static double[,] Identity4() => new double[4, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };

    /// <summary>矩阵乘积 a·b。</summary>
    static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                r[i, j] = s;
            }
        return r;
    }

    static double Determinant(double[,] m)
    {
        double s0 = m[0, 0] * m[1, 1] - m[1, 0] * m[0, 1];
        double s1 = m[0, 0] * m[1, 2] - m[1, 0] * m[0, 2];
        double s2 = m[0, 0] * m[1, 3] - m[1, 0] * m[0, 3];
        double s3 = m[0, 1] * m[1, 2] - m[1, 1] * m[0, 2];
        double s4 = m[0, 1] * m[1, 3] - m[1, 1] * m[0, 3];
        double s5 = m[0, 2] * m[1, 3] - m[1, 2] * m[0, 3];
        double c5 = m[2, 2] * m[3, 3] - m[3, 2] * m[2, 3];
        double c4 = m[2, 1] * m[3, 3] - m[3, 1] * m[2, 3];
        double c3 = m[2, 1] * m[3, 2] - m[3, 1] * m[2, 2];
        double c2 = m[2, 0] * m[3, 3] - m[3, 0] * m[2, 3];
        double c1 = m[2, 0] * m[3, 2] - m[3, 0] * m[2, 2];
        double c0 = m[2, 0] * m[3, 1] - m[3, 0] * m[2, 1];
        return s0 * c5 - s1 * c4 + s2 * c3 + s3 * c2 - s4 * c1 + s5 * c0;
    }

    static bool Invert(double[,] m, out double[,] inv)
    {
        var mm = new Matrix4x4(
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2], (float)m[0, 3],
            (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], (float)m[1, 3],
            (float)m[2, 0], (float)m[2, 1], (float)m[2, 2], (float)m[2, 3],
            (float)m[3, 0], (float)m[3, 1], (float)m[3, 2], (float)m[3, 3]);
        inv = new double[4, 4];
        if (!Matrix4x4.Invert(mm, out var r)) return false;
        inv[0, 0] = r.M11; inv[0, 1] = r.M12; inv[0, 2] = r.M13; inv[0, 3] = r.M14;
        inv[1, 0] = r.M21; inv[1, 1] = r.M22; inv[1, 2] = r.M23; inv[1, 3] = r.M24;
        inv[2, 0] = r.M31; inv[2, 1] = r.M32; inv[2, 2] = r.M33; inv[2, 3] = r.M34;
        inv[3, 0] = r.M41; inv[3, 1] = r.M42; inv[3, 2] = r.M43; inv[3, 3] = r.M44;
        return true;
    }

    static double Length(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    static void Normalize(double[] v)
    {
        double l = Length(v);
        if (l == 0) return;
        v[0] /= l;
        v[1] /= l;
        v[2] /= l;
    }

    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    static void Combine(double[] a, double[] b, double sa, double sb)
    {
        for (int i = 0; i < 3; i++) a[i] = a[i] * sa + b[i] * sb;
    }
}
