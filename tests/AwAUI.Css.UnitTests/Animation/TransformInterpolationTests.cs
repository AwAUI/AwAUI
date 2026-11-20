using System.Numerics;
using AwAUI.Css.Animation;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Animation.AnimationTestHelper;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>变换列表的插值（CSS Transforms 2 §16）：逐函数、补恒等函数、矩阵分解。</summary>
public class TransformInterpolationTests
{
    [Theory]
    [InlineData("translateX(0px)", "translateX(100px)", 0.5, "translateX(50px)")]
    [InlineData("none", "scale(2)", 0.5, "scale(1.5, 1.5)")]
    [InlineData("rotate(0deg)", "rotate(360deg)", 0.5, "rotate(180deg)")] // 同种函数：角度线性，不按最短路径
    [InlineData("translateX(10px)", "translateY(20px)", 0.5, "translate(5px, 10px)")] // 共同基元 translate
    [InlineData("scaleX(2)", "scale3d(1, 1, 3)", 0.5, "scale3d(1.5, 1, 2)")]
    [InlineData("translate(0px, 0px) rotate(0deg)", "translate(100px, 50%) rotate(90deg)", 0.5, "translate(50px, 25%) rotate(45deg)")]
    [InlineData("skewX(0deg)", "skewX(30deg)", 0.5, "skewX(15deg)")]
    [InlineData("translateX(0px)", "translateX(100px) rotate(90deg)", 0.5, "translateX(50px) rotate(45deg)")] // 较短的一端补恒等函数
    [InlineData("perspective(none)", "perspective(100px)", 0.5, "perspective(200px)")] // 按倒数插值
    public void Function_by_function(string from, string to, double p, string expected)
        => Assert.Equal(expected, Lerp("transform", from, to, p));

    [Fact]
    public void Mismatched_functions_fall_back_to_matrix_decomposition()
    {
        // rotate 与 translate 不同基元：合成矩阵后分解插值。scale(2) rotate(90deg) 到 none 的一半：scale 1.5、旋转 45°
        var r = Lerp("transform", "scale(2) rotate(90deg)", "rotate(0deg) scale(1)", 0.5);
        Assert.StartsWith("matrix(", r);
        Assert.True(CssProperties.TryGet("transform", out var t));
        CssInterpolation.Interpolate(t.Id, Computed("transform", "scale(2) rotate(90deg)"), Computed("transform", "translateX(0px)"), 0.5, out var v);
        var m = ((CssTransformList)v).ToMatrix(0, 0);
        var expected = Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateScale(1.5f, 1.5f, 1);
        AssertClose(expected, m);
    }

    [Fact]
    public void Rotation_takes_the_shortest_path_in_matrix_interpolation()
    {
        var a = Matrix4x4.CreateRotationZ(-170 * MathF.PI / 180);
        var b = Matrix4x4.CreateRotationZ(170 * MathF.PI / 180);
        AssertClose(Matrix4x4.CreateRotationZ(MathF.PI), TransformInterpolation.InterpolateMatrices(a, b, 0.5));
    }

    [Fact]
    public void Decomposition_round_trips()
    {
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var m = Matrix4x4.CreateScale((float)(rng.NextDouble() * 2 + 0.2), (float)(rng.NextDouble() * 2 + 0.2), (float)(rng.NextDouble() * 2 + 0.2))
                    * Matrix4x4.CreateFromYawPitchRoll((float)rng.NextDouble() * 3, (float)rng.NextDouble() * 3, (float)rng.NextDouble() * 3)
                    * Matrix4x4.CreateTranslation((float)rng.NextDouble() * 100, (float)rng.NextDouble() * 100, (float)rng.NextDouble() * 100);
            if (i % 3 == 0) m.M21 += 0.3f;
            if (i % 5 == 0) m.M34 = -0.002f;
            AssertClose(m, TransformInterpolation.InterpolateMatrices(m, m, 0.37), 1e-3f);
        }
    }

    [Fact]
    public void Singular_matrices_interpolate_discretely()
    {
        var r = Lerp("transform", "scale(0)", "rotate(45deg) translateX(10px)", 0.4);
        Assert.Equal("scale(0, 0)", r);
    }

    [Fact]
    public void Percentages_in_mismatched_lists_defer_to_the_reference_box()
    {
        Assert.True(CssProperties.TryGet("transform", out var t));
        CssInterpolation.Interpolate(t.Id, Computed("transform", "translateX(50%) rotate(0deg)"), Computed("transform", "rotate(90deg) translateX(10px)"), 0.5, out var v);
        var list = (CssTransformList)v;
        Assert.Single(list.Functions);
        Assert.Equal(CssTransformFunctionKind.Interpolated, list.Functions[0].Kind);
        // 参考盒 200 宽：起点矩阵 = translateX(100px)
        var expected = TransformInterpolation.InterpolateMatrices(Matrix4x4.CreateTranslation(100, 0, 0),
            Matrix4x4.CreateTranslation(10, 0, 0) * Matrix4x4.CreateRotationZ(MathF.PI / 2), 0.5);
        AssertClose(expected, list.ToMatrix(200, 100));
        // 同参考盒、同值的结果相等（样式比较不会每帧都认为变了）
        CssInterpolation.Interpolate(t.Id, Computed("transform", "translateX(50%) rotate(0deg)"), Computed("transform", "rotate(90deg) translateX(10px)"), 0.5, out var v2);
        Assert.Equal(v, v2);
    }

    [Fact]
    public void Nested_deferred_interpolation_is_bounded()
    {
        Assert.True(CssProperties.TryGet("transform", out var t));
        object a = Computed("transform", "translateX(50%) rotate(0deg)");
        object b = Computed("transform", "rotate(90deg)");
        object cur = a;
        for (int i = 0; i < 100; i++) CssInterpolation.Interpolate(t.Id, cur, i % 2 == 0 ? b : a, 0.5, out cur);
        var m = ((CssTransformList)cur).ToMatrix(100, 100);
        Assert.False(float.IsNaN(m.M11));
    }

    [Fact]
    public void Rotate3d_with_different_axes_slerps()
    {
        var r = Lerp("transform", "rotateX(0deg)", "rotateY(90deg)", 0.5);
        Assert.StartsWith("rotate3d(", r);
        Assert.True(CssProperties.TryGet("transform", out var t));
        CssInterpolation.Interpolate(t.Id, Computed("transform", "rotateX(0deg)"), Computed("transform", "rotateY(90deg)"), 0.5, out var v);
        AssertClose(Matrix4x4.CreateRotationY(MathF.PI / 4), ((CssTransformList)v).ToMatrix(0, 0));
    }

    [Fact]
    public void Tailwind_enter_keyframe_interpolates_to_none()
    {
        // tw-animate-css 的 enter：translate3d(0,0,0) scale3d(.95,.95,.95) rotate(0) → none
        var r = Lerp("transform", "translate3d(0px, 0px, 0px) scale3d(0.95, 0.95, 0.95) rotate(0deg)", "none", 0.5);
        Assert.Equal("translate3d(0px, 0px, 0px) scale3d(0.975, 0.975, 0.975) rotate(0deg)", r);
    }

    static void AssertClose(Matrix4x4 expected, Matrix4x4 actual, float tol = 1e-4f)
    {
        float[] e = { expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44 };
        float[] a = { actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44 };
        for (int i = 0; i < 16; i++) Assert.True(Math.Abs(e[i] - a[i]) <= tol * Math.Max(1, Math.Abs(e[i])), $"[{i}] expected {e[i]} actual {a[i]}\n{expected}\n{actual}");
    }
}
