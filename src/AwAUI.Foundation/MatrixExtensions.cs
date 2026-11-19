using System;
using System.Numerics;

namespace AwAUI;

/// <summary>2D 仿射矩阵的辅助方法。约定与 System.Numerics 一致：行向量右乘，p' = p · M。</summary>
public static class MatrixExtensions
{
    /// <summary>没有旋转与错切（只有缩放与平移）。</summary>
    public static bool IsScaleTranslate(this in Matrix3x2 m) => m.M12 == 0 && m.M21 == 0;

    /// <summary>变换的平均缩放因子，用于把设备空间的容差换算到局部空间。</summary>
    public static float AverageScale(this in Matrix3x2 m)
    {
        float det = MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21);
        return det > 0 ? MathF.Sqrt(det) : 0;
    }

    /// <summary>变换的最大缩放因子（奇异值的较大者）。</summary>
    public static float MaxScale(this in Matrix3x2 m)
    {
        float a = m.M11, b = m.M12, c = m.M21, d = m.M22;
        float s1 = a * a + b * b + c * c + d * d;
        float det = a * d - b * c;
        float disc = MathF.Sqrt(MathF.Max(0, s1 * s1 - 4 * det * det));
        return MathF.Sqrt((s1 + disc) * 0.5f);
    }

    public static Vector2 TransformVector(this in Matrix3x2 m, Vector2 v) =>
        new(v.X * m.M11 + v.Y * m.M21, v.X * m.M12 + v.Y * m.M22);
}
