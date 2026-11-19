using System.Numerics;
using System.Runtime.InteropServices;

namespace AwAUI.Render.Rhi;

/// <summary>
/// <see cref="GpuPipelineKind.Quad"/> 管线的实例数据（176 字节，按 16 字节对齐的 11 个四元组）。
/// 这是 RHI 与各图形 API 着色器之间的契约：每个后端的着色器都必须按这里的语义实现。
/// <para>
/// 坐标一律为「设备像素」（渲染目标所在的全局像素坐标，像素中心在 .5）。一个实例画一个轴对齐的四边形
/// <see cref="Bounds"/>（整数坐标，调用方已与裁剪矩形求交），片元着色器在其中按 <see cref="QuadShape"/> 求覆盖率、
/// 按 <see cref="QuadPaint"/> 求预乘颜色，再乘以可选的裁剪蒙版：
/// </para>
/// <code>
/// cov  = shapeCoverage(p) · (Clip.z != 0 ? clipTexture.read(floor(p) + Clip.xy).r : 1)
///                        · (Clip.w != 0 ? roundRectCoverage(clipShapes[Clip.w − 1], p) : 1)
/// src  = paint(p)                                    // 预乘
/// 输出 = Kinds.w == 0 ? src · cov                    // 交给固定功能混合（通常 PremultipliedOver）
///                     : lerp(d, blend(src, d), cov)  // d = dstTexture.read(floor(p) − Uniforms.Dst.xy)，固定功能为 Replace
/// </code>
/// <para>纹理槽位：0 覆盖率或图层纹理，1 渐变查找表，2 裁剪蒙版，3 目标拷贝（读目标的混合模式）。读取均为整数纹素，不做过滤。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuQuadInstance
{
    /// <summary>要光栅化的四边形：left, top, right, bottom（整数）。</summary>
    public Vector4 Bounds;

    /// <summary>形状矩形 left, top, right, bottom（矩形、圆角矩形、外圈、阴影形状）。</summary>
    public Vector4 Shape;

    /// <summary>四角水平半径：左上、右上、右下、左下。阴影种类中为模糊形状的半径。</summary>
    public Vector4 RadiiX;

    /// <summary>四角垂直半径，顺序同 <see cref="RadiiX"/>。</summary>
    public Vector4 RadiiY;

    /// <summary>纯色画刷的预乘颜色；<see cref="QuadShape.Layer"/> 时 x 为图层不透明度。</summary>
    public Vector4 Color;

    /// <summary>
    /// 渐变参数第一行：q.x = dot(xyz, (p, 1))；w 为查找表行号。
    /// 阴影种类中为原始形状矩形（外阴影要挖掉的盒子 / 内阴影所在的盒子）。
    /// </summary>
    public Vector4 Paint0;

    /// <summary>
    /// 渐变参数第二行：q.y = dot(xyz, (p, 1))；锥形渐变的 w 为起始角（弧度），双点锥形渐变的 w 为 a（见 <see cref="QuadPaint.TwoPointConical"/>）。
    /// 阴影种类中为原始形状的水平半径。
    /// </summary>
    public Vector4 Paint1;

    /// <summary>
    /// 覆盖率 / 图层纹理的映射：纹素 = floor(p) + xy。
    /// <see cref="QuadShape.Ring"/> 中为内圈矩形 left, top, right, bottom；阴影种类中为原始形状的垂直半径。
    /// </summary>
    public Vector4 Texture;

    /// <summary>
    /// 裁剪：xy = 蒙版映射（纹素 = floor(p) + xy），z ≠ 0 表示启用蒙版；
    /// w ≠ 0 表示解析裁剪形状，取 <see cref="GpuQuadUniforms.ClipShapeSlot"/> 处数组的第 w − 1 项。
    /// </summary>
    public Vector4 Clip;

    /// <summary>种类相关参数：阴影种类中 x = 高斯 σ（设备像素）；双点锥形渐变中为 (cd.x, cd.y, r0, dr)。</summary>
    public Vector4 Extra;

    /// <summary>x = <see cref="QuadShape"/>；y = <see cref="QuadPaint"/> | (扩展模式 &lt;&lt; 4) | (不抗锯齿 &lt;&lt; 8)；z = 保留；w = 0 或 混合模式 + 1（读目标）。</summary>
    public QuadKinds Kinds;

    public const int SizeInBytes = 176;
}

/// <summary>四个 32 位无符号整数（与着色器的 uint4 对应）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct QuadKinds
{
    public uint Shape;
    public uint Paint;
    public uint Reserved;
    public uint Blend;
}

/// <summary><see cref="GpuQuadInstance.Kinds"/>.x：覆盖率的求法。</summary>
public static class QuadShape
{
    /// <summary>轴对齐矩形的精确面积覆盖率（水平重叠 × 垂直重叠）。</summary>
    public const uint Rect = 0;
    /// <summary>四角独立的椭圆角圆角矩形，角区按到椭圆的距离求半平面覆盖率。</summary>
    public const uint RoundRect = 1;
    /// <summary>
    /// 外圈圆角矩形减去内圈（描边矩形 / 圆角矩形）。内圈矩形在 Texture；内圈半径 = max(外圈半径 − 线宽, 0)，
    /// 水平线宽 = 内圈 left − 外圈 left，垂直线宽 = 内圈 top − 外圈 top。
    /// </summary>
    public const uint Ring = 2;
    /// <summary>A8 覆盖率纹理（路径掩码、字形）：纹素 = floor(p) + Texture.xy。</summary>
    public const uint Mask = 3;
    /// <summary>RGBA 图层纹理（预乘）作为颜色，覆盖率 = Color.x（不透明度）。</summary>
    public const uint Layer = 4;
    /// <summary>外阴影：模糊的形状（Shape/Radii，σ = Extra.x）×（1 − 原始形状覆盖率）。</summary>
    public const uint OuterShadow = 5;
    /// <summary>内阴影：（1 − 模糊的洞）× 原始形状覆盖率。</summary>
    public const uint InsetShadow = 6;
}

/// <summary><see cref="GpuQuadInstance.Kinds"/>.y 的低 4 位：画刷种类。</summary>
public static class QuadPaint
{
    public const uint Solid = 0;
    public const uint Linear = 1;
    public const uint Radial = 2;
    public const uint Conic = 3;

    /// <summary>
    /// 双点锥形渐变（带焦点的径向渐变，Canvas 2D 的 createRadialGradient 语义）。q = (Paint0·(p,1), Paint1·(p,1)) 是以起始圆圆心为原点、
    /// 归一化后的渐变空间坐标；Extra = (cd.x, cd.y, r0, dr)：结束圆圆心、起始圆半径、半径差（同一单位），Paint1.w = a = cd·cd − dr²。
    /// 解 a·t² − 2b·t + c = 0（b = q·cd + r0·dr，c = q·q − r0²），取 r0 + t·dr ≥ 0 的最大根；|a| &lt; 1e-5 时为一次方程 t = c / 2b。
    /// 无解的像素输出透明。t 再按扩展模式映射后查表。
    /// </summary>
    public const uint TwoPointConical = 4;

    /// <summary>
    /// 位图采样（DisplayList 的 DrawImage）。纹理绑定在渐变表的槽位（<see cref="GpuQuadUniforms.GradientTextureSlot"/>，按整数纹素读取）。
    /// (u, v) = (Paint0·(p,1), Paint1·(p,1)) 是设备像素中心 p 对应的纹素坐标；Extra = (x0, y0, x1, y1) 是纹素下标的钳制范围（含两端，
    /// 源矩形之外的纹素不会被读到）；Paint1.w = 不透明度（乘到预乘颜色上）。扩展模式位（<see cref="SpreadShift"/>）为采样方式：
    /// 0 = 最近（纹素 ⌊u⌋, ⌊v⌋），1 = 双线性（纹素中心在 i + 0.5，读 ⌊u − 0.5⌋ 与其右 / 下邻的四个纹素按小数部分插值）。
    /// 下标先在浮点域钳制再转为整数。语义与软件后端的 ImageShader 相同；缩小用的 mip 级别由 CPU 构造后作为普通纹理上传。
    /// </summary>
    public const uint Image = 5;

    /// <summary>扩展模式所在的位移（0 = Pad、1 = Repeat、2 = Reflect）。</summary>
    public const int SpreadShift = 4;

    /// <summary>不抗锯齿标志：覆盖率按 0.5 取阈值。</summary>
    public const uint Aliased = 1u << 8;
}

/// <summary><see cref="GpuPipelineKind.Quad"/> 管线的常量（32 字节），绑定在 <see cref="Slot"/>。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuQuadUniforms
{
    /// <summary>xy = 附件左上角纹素对应的设备像素坐标；zw = 2 / 附件宽、2 / 附件高。</summary>
    public Vector4 Target;

    /// <summary>xy = 目标拷贝纹理左上角对应的设备像素坐标。</summary>
    public Vector4 Dst;

    /// <summary>实例数组的缓冲槽位。</summary>
    public const int InstanceSlot = 0;

    /// <summary><see cref="GpuClipShape"/> 数组的缓冲槽位（没有解析裁剪时也要绑定一个缓冲）。</summary>
    public const int ClipShapeSlot = 2;

    /// <summary>常量的槽位。</summary>
    public const int Slot = 1;

    public const int CoverageTextureSlot = 0;
    public const int GradientTextureSlot = 1;
    public const int ClipTextureSlot = 2;
    public const int DstTextureSlot = 3;

    /// <summary>每个实例的顶点数（三角形带）。</summary>
    public const int VertexCount = 4;
}

/// <summary>
/// 解析裁剪形状（64 字节）：轴对齐的圆角矩形（半径全为 0 时即抗锯齿的矩形裁剪），覆盖率求法与 <see cref="QuadShape.RoundRect"/> 相同。
/// 一帧的所有裁剪形状放在一个数组里，实例经 <see cref="GpuQuadInstance.Clip"/>.w 引用。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuClipShape
{
    /// <summary>left, top, right, bottom（设备像素）。</summary>
    public Vector4 Rect;
    public Vector4 RadiiX;
    public Vector4 RadiiY;
    /// <summary>x ≠ 0：不抗锯齿（覆盖率按 0.5 取阈值）。</summary>
    public Vector4 Flags;

    public const int SizeInBytes = 64;
}
