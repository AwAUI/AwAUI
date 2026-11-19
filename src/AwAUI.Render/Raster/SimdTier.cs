using System;
using System.Numerics;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace AwAUI.Render.Raster;

/// <summary>
/// 光栅器热循环的内核档位（design/11「性能分级」）。三档输出逐字节一致，只有速度不同。
/// </summary>
internal enum SimdTier
{
    /// <summary>标量基线：不使用任何向量类型，是其他两档的参考实现。</summary>
    Scalar,

    /// <summary><see cref="Vector{T}"/>：netstandard2.0 / .NET Framework 可用。</summary>
    Vector,

    /// <summary><c>Vector128</c> 硬件内建（net8.0+，SSE / NEON / WASM SIMD）。</summary>
    Vector128,
}

/// <summary>选择内核档位。默认取当前运行时可用的最高档；测试与基准可以强制指定。</summary>
internal static partial class Kernels
{
    static SimdTier? s_forced;
    static SimdTier s_active = Best;

    /// <summary>当前运行时与编译目标下硬件加速的最高档。</summary>
    public static SimdTier Best
    {
        get
        {
            // 向量内核按小端字节序把像素当作 uint 处理
            if (!BitConverter.IsLittleEndian) return SimdTier.Scalar;
#if NET8_0_OR_GREATER
            if (Vector128.IsHardwareAccelerated) return SimdTier.Vector128;
#endif
            if (Vector.IsHardwareAccelerated && Vector<float>.Count >= 4) return SimdTier.Vector;
            return SimdTier.Scalar;
        }
    }

    /// <summary>
    /// 强制使用某一档（null 恢复默认）。请求的档位在当前编译目标下不存在时（例如 netstandard2.0 上的
    /// Vector128）降到下一档；没有硬件加速的档位仍可强制使用（软件模拟，结果相同），供一致性测试使用。
    /// 这是进程级开关，不应在渲染进行中切换。
    /// </summary>
    public static SimdTier? ForcedTier
    {
        get => s_forced;
        set
        {
            s_forced = value;
            s_active = value is { } tier ? Clamp(tier) : Best;
        }
    }

    /// <summary>实际生效的档位。</summary>
    public static SimdTier Active => s_active;

    static SimdTier Clamp(SimdTier tier)
    {
        if (!BitConverter.IsLittleEndian) return SimdTier.Scalar;
#if !NET8_0_OR_GREATER
        if (tier == SimdTier.Vector128) tier = SimdTier.Vector;
#endif
        return tier;
    }
}
