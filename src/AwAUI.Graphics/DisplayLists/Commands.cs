using System.Numerics;
using System.Runtime.InteropServices;

namespace AwAUI.Graphics.Commands;

// DisplayList 各命令的载荷。全部为固定布局的 blittable 结构体，大小为 4 字节的整数倍。
// 引用类型资源以索引（Ref）指向 DisplayList 的资源表；-1 表示无。

/// <summary>命令头：[op:u8][flags:u8][size:u16，以 4 字节为单位、含命令头]。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CommandHeader
{
    public DrawOp Op;
    public DrawFlags Flags;
    public ushort Size4;
}

/// <summary>绘制参数：纯色内联；其他画刷经 BrushRef 引用。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PaintData
{
    public Color Color;          // 非预乘；BrushRef >= 0 时忽略
    public int BrushRef;
    public BlendMode BlendMode;
    private byte _pad0, _pad1, _pad2;

    public PaintData(Color color, int brushRef, BlendMode blend)
    {
        Color = color; BrushRef = brushRef; BlendMode = blend; _pad0 = _pad1 = _pad2 = 0;
    }

    public bool IsSolid => BrushRef < 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct TranslateCmd { public float Dx, Dy; }

[StructLayout(LayoutKind.Sequential)]
public struct TransformCmd { public Matrix3x2 Matrix; }

[StructLayout(LayoutKind.Sequential)]
public struct ClipRectCmd { public Rect Rect; }

[StructLayout(LayoutKind.Sequential)]
public struct ClipRoundRectCmd { public RoundRect Shape; }

[StructLayout(LayoutKind.Sequential)]
public struct ClipPathCmd { public int PathRef; }

[StructLayout(LayoutKind.Sequential)]
public struct FillRectCmd { public Rect Rect; public PaintData Paint; }

[StructLayout(LayoutKind.Sequential)]
public struct FillRoundRectCmd { public RoundRect Shape; public PaintData Paint; }

[StructLayout(LayoutKind.Sequential)]
public struct StrokeRectCmd { public Rect Rect; public PaintData Paint; public int StrokeRef; }

[StructLayout(LayoutKind.Sequential)]
public struct StrokeRoundRectCmd { public RoundRect Shape; public PaintData Paint; public int StrokeRef; }

[StructLayout(LayoutKind.Sequential)]
public struct DrawLineCmd { public Point P0, P1; public PaintData Paint; public int StrokeRef; }

[StructLayout(LayoutKind.Sequential)]
public struct FillPathCmd { public int PathRef; public PaintData Paint; }

[StructLayout(LayoutKind.Sequential)]
public struct StrokePathCmd { public int PathRef; public PaintData Paint; public int StrokeRef; }

/// <summary>绘制字形串：<see cref="GlyphRunRef"/> 指向资源表中的 <see cref="Text.GlyphRun"/>，字形位置相对 <see cref="Origin"/>。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawGlyphRunCmd { public int GlyphRunRef; public Point Origin; public PaintData Paint; }

/// <summary>
/// 绘制位图（操作码 34，design/13）：位图的 <see cref="Src"/> 部分（纹素坐标）映射到 <see cref="Dst"/>（局部坐标），
/// 再经当前变换与裁剪绘制。<see cref="ImageRef"/> 指向资源表中的 <see cref="Bitmap"/>；后端在渲染时读取像素，
/// 按（位图对象，<see cref="Bitmap.Version"/>）缓存派生数据。录制器保证 Src 落在位图内、两个矩形非空且有限、0 &lt; Opacity ≤ 1。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawImageCmd
{
    public int ImageRef;
    public Rect Src;
    public Rect Dst;
    public float Opacity;
    public ImageSampling Sampling;
    private byte _pad0, _pad1, _pad2;

    public DrawImageCmd(int imageRef, Rect src, Rect dst, ImageSampling sampling, float opacity)
    {
        ImageRef = imageRef; Src = src; Dst = dst; Sampling = sampling; Opacity = opacity;
        _pad0 = _pad1 = _pad2 = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct BoxShadowCmd
{
    public RoundRect Shape;
    public float OffsetX, OffsetY, Blur, Spread;
    public Color Color;
    public int Inset;
}

/// <summary>
/// 开启隔离组（design/13）。<see cref="HasFilter"/> 与 <see cref="FilterRef"/> 是 <see cref="DisplayList.FormatVersion"/> 2 追加的：
/// 只认识旧前缀的读取器把它当作没有滤镜的普通图层（滤镜不生效，栈仍然配对）；旧格式的命令在 <see cref="HasFilter"/> 处是填充的 0。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PushLayerCmd
{
    public Rect Bounds;         // 局部坐标；HasBounds = 0 时忽略
    public float Opacity;
    public BlendMode BlendMode;
    public byte HasBounds;
    /// <summary>1 表示 <see cref="FilterRef"/> 有效（资源表中的 <see cref="Filter"/>）。</summary>
    public byte HasFilter;
    private byte _pad1;
    public int FilterRef;
}

/// <summary>
/// 背景滤镜（操作码 38，CSS <c>backdrop-filter</c>）：当前表面上已绘制的内容（背景）在 <see cref="Shape"/>（局部坐标，当前变换与裁剪下）
/// 之内被替换为经 <see cref="FilterRef"/> 处理后的结果：dst = lerp(dst, filter(dst), 覆盖率 × <see cref="Opacity"/>)。
/// 滤镜的输入范围是形状的设备包围盒按滤镜外扩量外扩（与整个渲染目标求交，不与裁剪求交）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawBackdropCmd
{
    public RoundRect Shape;
    public int FilterRef;
    public float Opacity;
}

[StructLayout(LayoutKind.Sequential)]
public struct DrawDisplayListCmd { public int DisplayListRef; }

[StructLayout(LayoutKind.Sequential)]
public struct AnnotationCmd { public long ElementId; public int Category; public int Reserved; }
