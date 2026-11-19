using System;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Svg.Rendering;

/// <summary>
/// 一个已解析的画法：固定的 <see cref="Paint"/>，或依赖调用方 currentColor 的工厂。
/// 工厂结果按颜色缓存最近一次（对象引用的原子替换，多线程绘制安全）。
/// </summary>
internal sealed class RenderPaint
{
    sealed class Cache
    {
        public Cache(Color color, Paint paint) { Color = color; Paint = paint; }
        public readonly Color Color;
        public readonly Paint Paint;
    }

    readonly Paint? _fixed;
    readonly Func<Color, Paint>? _factory;
    Cache? _cache;

    public RenderPaint(Paint paint) => _fixed = paint;
    public RenderPaint(Func<Color, Paint> factory) => _factory = factory;

    public bool DependsOnCurrentColor => _factory != null;

    public Paint Resolve(Color currentColor)
    {
        if (_fixed != null) return _fixed;
        var c = _cache;
        if (c != null && c.Color == currentColor) return c.Paint;
        var p = _factory!(currentColor);
        _cache = new Cache(currentColor, p);
        return p;
    }
}

/// <summary>渲染树节点。全部在构建时确定，绘制时只发出命令。</summary>
internal abstract class RenderNode
{
    /// <summary>节点自身坐标系 → 父坐标系。</summary>
    public Matrix3x2 Transform = Matrix3x2.Identity;

    /// <summary>视口裁剪（嵌套 svg / symbol），在 <see cref="Transform"/> 之后的坐标系中。</summary>
    public Rect? ClipRect;

    /// <summary>clip-path 解析出的裁剪路径，在 <see cref="Transform"/> 之后的坐标系中。</summary>
    public VectorPath? Clip;

    /// <summary>组不透明度；小于 1 时用离屏图层绘制。</summary>
    public float Opacity = 1f;

    /// <summary>几何包围盒（不含描边），在自身坐标系中。用于 objectBoundingBox。</summary>
    public Rect LocalBBox = Rect.Empty;

    /// <summary>绘制范围的保守估计（含描边），在父坐标系中。</summary>
    public Rect Bounds = Rect.Empty;

    public void Emit(DrawingContext dc, Color currentColor)
    {
        bool layer = Opacity < 1f;
        bool state = !Transform.IsIdentity || ClipRect.HasValue || Clip != null;
        if (layer) dc.PushLayer(new LayerOptions(Opacity, BlendMode.SrcOver, Bounds.IsEmpty || !Bounds.IsFinite ? null : Bounds));
        else if (state) dc.Save();
        if (!Transform.IsIdentity) dc.Transform(Transform);
        if (ClipRect is { } r) dc.ClipRect(r);
        if (Clip != null) dc.ClipPath(Clip);
        EmitContent(dc, currentColor);
        if (layer) dc.PopLayer();
        else if (state) dc.Restore();
    }

    protected abstract void EmitContent(DrawingContext dc, Color currentColor);

    public abstract bool DependsOnCurrentColor { get; }
}

internal sealed class RenderGroup : RenderNode
{
    public RenderGroup(RenderNode[] children) => Children = children;

    public RenderNode[] Children { get; }

    protected override void EmitContent(DrawingContext dc, Color currentColor)
    {
        foreach (var c in Children) c.Emit(dc, currentColor);
    }

    public override bool DependsOnCurrentColor
    {
        get
        {
            foreach (var c in Children) if (c.DependsOnCurrentColor) return true;
            return false;
        }
    }
}

internal sealed class RenderShape : RenderNode
{
    public RenderShape(VectorPath path, RenderPaint? fill, RenderPaint? stroke, Stroke? strokeStyle, bool strokeFirst)
    {
        Path = path;
        Fill = fill;
        StrokePaint = stroke;
        StrokeStyle = strokeStyle;
        StrokeFirst = strokeFirst;
    }

    public VectorPath Path { get; }
    public RenderPaint? Fill { get; }
    public RenderPaint? StrokePaint { get; }
    public Stroke? StrokeStyle { get; }
    public bool StrokeFirst { get; }

    protected override void EmitContent(DrawingContext dc, Color currentColor)
    {
        if (StrokeFirst) EmitStroke(dc, currentColor);
        if (Fill != null) dc.FillPath(Path, Fill.Resolve(currentColor));
        if (!StrokeFirst) EmitStroke(dc, currentColor);
    }

    void EmitStroke(DrawingContext dc, Color currentColor)
    {
        if (StrokePaint != null && StrokeStyle != null) dc.StrokePath(Path, StrokePaint.Resolve(currentColor), StrokeStyle);
    }

    public override bool DependsOnCurrentColor =>
        Fill is { DependsOnCurrentColor: true } || StrokePaint is { DependsOnCurrentColor: true };
}

/// <summary>&lt;image&gt;：位图的源矩形画到目标矩形（都在自身坐标系中，preserveAspectRatio 已折算，slice 已裁成源矩形）。</summary>
internal sealed class RenderImage : RenderNode
{
    public RenderImage(Bitmap image, Rect source, Rect destination, ImageSampling sampling, float opacity)
    {
        Image = image;
        Source = source;
        Destination = destination;
        Sampling = sampling;
        ImageOpacity = opacity;
    }

    public Bitmap Image { get; }
    public Rect Source { get; }
    public Rect Destination { get; }
    public ImageSampling Sampling { get; }

    /// <summary>元素的 opacity 直接乘到图像上（单个图像不需要透明度组）。</summary>
    public float ImageOpacity { get; }

    protected override void EmitContent(DrawingContext dc, Color currentColor) =>
        dc.DrawImage(Image, Source, Destination, Sampling, ImageOpacity);

    public override bool DependsOnCurrentColor => false;
}
