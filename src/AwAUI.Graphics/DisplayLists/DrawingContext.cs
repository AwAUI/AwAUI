using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using AwAUI.Graphics.Commands;
using AwAUI.Graphics.Text;

namespace AwAUI.Graphics;

/// <summary>
/// 录制 API（design/02 的接缝①）。所有绘制都被编码成 <see cref="DisplayList"/>；
/// 这里不做任何光栅化。不是线程安全的，一次录制只在一个线程上进行。
/// </summary>
public sealed class DrawingContext : IDisposable
{
    byte[] _buffer;
    int _length;
    int _commandCount;
    readonly List<object> _resources = new();
    readonly Dictionary<object, int> _resourceIndex = new(ReferenceComparer.Instance);

    // 用于计算保守包围盒的状态（不影响编码）
    readonly Stack<State> _stack = new();
    State _state = new(Matrix3x2.Identity, null, false);
    Rect _bounds = Rect.Empty;
    bool _finished;

    readonly struct State
    {
        public State(Matrix3x2 ctm, Rect? clip, bool isLayer) { Ctm = ctm; Clip = clip; IsLayer = isLayer; }
        public Matrix3x2 Ctm { get; }
        public Rect? Clip { get; }       // 根坐标系下的裁剪包围盒
        public bool IsLayer { get; }
    }

    public DrawingContext(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(64, initialCapacity));
    }

    /// <summary>当前变换（相对录制根）。</summary>
    public Matrix3x2 CurrentTransform => _state.Ctm;

    /// <summary>当前 Save/PushLayer 嵌套深度。</summary>
    public int Depth => _stack.Count;

    // ---------------------------------------------------------------- 状态

    public void Save()
    {
        _stack.Push(_state);
        _state = new State(_state.Ctm, _state.Clip, false);
        WriteEmpty(DrawOp.Save);
    }

    public void Restore()
    {
        if (_stack.Count == 0) throw new InvalidOperationException("Restore without matching Save.");
        if (_state.IsLayer) throw new InvalidOperationException("Restore called while a layer is open; use PopLayer.");
        _state = _stack.Pop();
        WriteEmpty(DrawOp.Restore);
    }

    public void Translate(float dx, float dy)
    {
        if (dx == 0 && dy == 0) return;
        if (!IsFinite(dx) || !IsFinite(dy)) return;
        SetCtm(Matrix3x2.CreateTranslation(dx, dy) * _state.Ctm);
        Write(DrawOp.Translate, DrawFlags.None, new TranslateCmd { Dx = dx, Dy = dy });
    }

    /// <summary>把 <paramref name="matrix"/> 作用于其后的绘制（先应用 matrix，再应用已有变换）。</summary>
    public void Transform(in Matrix3x2 matrix)
    {
        if (matrix.IsIdentity) return;
        if (!IsFinite(matrix)) return;
        SetCtm(matrix * _state.Ctm);
        Write(DrawOp.Transform, DrawFlags.None, new TransformCmd { Matrix = matrix });
    }

    public void ClipRect(Rect rect, bool antiAlias = true)
    {
        if (!rect.IsFinite) return;
        IntersectClip(rect.TransformBounds(_state.Ctm));
        Write(DrawOp.ClipRect, antiAlias ? DrawFlags.AntiAlias : DrawFlags.None, new ClipRectCmd { Rect = rect });
    }

    public void ClipRoundRect(in RoundRect shape, bool antiAlias = true)
    {
        if (!shape.Rect.IsFinite) return;
        IntersectClip(shape.Rect.TransformBounds(_state.Ctm));
        Write(DrawOp.ClipRoundRect, antiAlias ? DrawFlags.AntiAlias : DrawFlags.None, new ClipRoundRectCmd { Shape = shape });
    }

    public void ClipPath(VectorPath path, bool antiAlias = true)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        IntersectClip(path.Bounds.TransformBounds(_state.Ctm));
        Write(DrawOp.ClipPath, antiAlias ? DrawFlags.AntiAlias : DrawFlags.None, new ClipPathCmd { PathRef = AddResource(path) });
    }

    // ---------------------------------------------------------------- 图元

    public void FillRect(Rect rect, Paint paint)
    {
        if (rect.IsEmpty || !rect.IsFinite || IsInvisible(paint)) return;
        Include(rect);
        Write(DrawOp.FillRect, Flags(paint), new FillRectCmd { Rect = rect, Paint = Encode(paint) });
    }

    public void FillRoundRect(in RoundRect shape, Paint paint)
    {
        if (shape.Rect.IsEmpty || !shape.Rect.IsFinite || IsInvisible(paint)) return;
        if (shape.IsRect) { FillRect(shape.Rect, paint); return; }
        Include(shape.Rect);
        Write(DrawOp.FillRoundRect, Flags(paint), new FillRoundRectCmd { Shape = shape, Paint = Encode(paint) });
    }

    public void StrokeRect(Rect rect, Paint paint, Stroke stroke)
    {
        if (!rect.IsFinite || !IsDrawable(stroke) || IsInvisible(paint)) return;
        Include(rect.Inflate(stroke.Outset));
        Write(DrawOp.StrokeRect, Flags(paint), new StrokeRectCmd { Rect = rect, Paint = Encode(paint), StrokeRef = AddResource(stroke) });
    }

    public void StrokeRoundRect(in RoundRect shape, Paint paint, Stroke stroke)
    {
        if (!shape.Rect.IsFinite || !IsDrawable(stroke) || IsInvisible(paint)) return;
        Include(shape.Rect.Inflate(stroke.Outset));
        Write(DrawOp.StrokeRoundRect, Flags(paint), new StrokeRoundRectCmd { Shape = shape, Paint = Encode(paint), StrokeRef = AddResource(stroke) });
    }

    public void DrawLine(Point p0, Point p1, Paint paint, Stroke stroke)
    {
        if (!IsFinite(p0.X) || !IsFinite(p0.Y) || !IsFinite(p1.X) || !IsFinite(p1.Y)) return;
        if (!IsDrawable(stroke) || IsInvisible(paint)) return;
        if (p0 == p1 && stroke.Cap == StrokeCap.Butt) return;
        var r = Rect.FromLTRB(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y));
        Include(r.Inflate(stroke.Outset));
        Write(DrawOp.DrawLine, Flags(paint), new DrawLineCmd { P0 = p0, P1 = p1, Paint = Encode(paint), StrokeRef = AddResource(stroke) });
    }

    public void FillPath(VectorPath path, Paint paint)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.IsEmpty || IsInvisible(paint)) return;
        Include(path.Bounds);
        Write(DrawOp.FillPath, Flags(paint), new FillPathCmd { PathRef = AddResource(path), Paint = Encode(paint) });
    }

    public void StrokePath(VectorPath path, Paint paint, Stroke stroke)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.IsEmpty || !IsDrawable(stroke) || IsInvisible(paint)) return;
        Include(path.Bounds.Inflate(stroke.Outset));
        Write(DrawOp.StrokePath, Flags(paint), new StrokePathCmd { PathRef = AddResource(path), Paint = Encode(paint), StrokeRef = AddResource(stroke) });
    }

    public void FillEllipse(Rect rect, Paint paint)
    {
        if (rect.IsEmpty) return;
        FillPath(VectorPath.FromEllipse(rect), paint);
    }

    public void FillCircle(Point center, float radius, Paint paint) =>
        FillEllipse(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2), paint);

    /// <summary>
    /// 绘制已整形的字形串。<paramref name="origin"/> 是字形位置的参照原点（通常为第一个字形在基线上的原点）。
    /// 字形串以引用进入资源表（不复制）；空字形串、字号为 0、原点非有限值或不可见的画刷不录制。
    /// </summary>
    public void DrawGlyphRun(GlyphRun run, Point origin, Paint paint)
    {
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (paint is null) throw new ArgumentNullException(nameof(paint));
        if (run.IsEmpty || !IsFinite(origin.X) || !IsFinite(origin.Y) || IsInvisible(paint)) return;
        var bounds = run.Bounds;
        if (!bounds.IsFinite) return;
        Include(bounds.Offset(origin.X, origin.Y));
        Write(DrawOp.DrawGlyphRun, Flags(paint), new DrawGlyphRunCmd { GlyphRunRef = AddResource(run), Origin = origin, Paint = Encode(paint) });
    }

    /// <summary>
    /// 把位图的 <paramref name="source"/> 部分（纹素坐标，可为小数）画到 <paramref name="destination"/>（局部坐标），受当前变换、裁剪约束，
    /// 以 SrcOver 合成并乘上 <paramref name="opacity"/>（design/13，操作码 34）。
    /// <para>
    /// 与 Canvas 2D 的 <c>drawImage</c> 相同，超出位图范围的 <paramref name="source"/> 先裁到位图内，<paramref name="destination"/> 按同一比例收缩。
    /// 采样只读取源矩形覆盖到的纹素（边缘按钳制处理，不会渗入源矩形之外的像素）；目标矩形的边缘按解析覆盖率抗锯齿。
    /// 位图以引用进入资源表，像素在渲染时读取：录制之后改写像素要调用 <see cref="Bitmap.Invalidate"/>。
    /// 空或非有限的矩形、非有限或不大于 0 的不透明度、已释放或空的位图不录制；不透明度大于 1 按 1。
    /// </para>
    /// </summary>
    public void DrawImage(Bitmap image, Rect source, Rect destination, ImageSampling sampling = ImageSampling.Linear, float opacity = 1)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        if (image.IsDisposed || image.Width <= 0 || image.Height <= 0) return;
        if (!(opacity > 0) || float.IsNaN(opacity)) return;
        if (opacity > 1) opacity = 1;
        if (sampling != ImageSampling.Nearest) sampling = ImageSampling.Linear;
        if (!source.IsFinite || !destination.IsFinite || source.IsEmpty || destination.IsEmpty) return;

        // 源矩形裁到位图内，目标按同一映射收缩（用 double 计算，极端比例下也不溢出）
        var clipped = source.Intersect(new Rect(0, 0, image.Width, image.Height));
        if (clipped.IsEmpty) return;
        if (clipped != source)
        {
            double sx = destination.Width / (double)source.Width, sy = destination.Height / (double)source.Height;
            double l = destination.X + (clipped.Left - (double)source.Left) * sx;
            double t = destination.Y + (clipped.Top - (double)source.Top) * sy;
            double r = destination.X + (clipped.Right - (double)source.Left) * sx;
            double b = destination.Y + (clipped.Bottom - (double)source.Top) * sy;
            destination = Rect.FromLTRB((float)l, (float)t, (float)r, (float)b);
            source = clipped;
            if (!destination.IsFinite || destination.IsEmpty) return;
        }
        Include(destination);
        Write(DrawOp.DrawImage, DrawFlags.AntiAlias, new DrawImageCmd(AddResource(image), source, destination, sampling, opacity));
    }

    /// <summary>把整张位图画到 <paramref name="destination"/>，见 <see cref="DrawImage(Bitmap, Rect, Rect, ImageSampling, float)"/>。</summary>
    public void DrawImage(Bitmap image, Rect destination, ImageSampling sampling = ImageSampling.Linear, float opacity = 1)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        DrawImage(image, new Rect(0, 0, image.Width, image.Height), destination, sampling, opacity);
    }

    /// <summary>CSS box-shadow。外阴影只画在形状之外，内阴影只画在形状之内。</summary>
    public void DrawBoxShadow(in RoundRect shape, in BoxShadow shadow)
    {
        if (!shape.Rect.IsFinite || shadow.Color.IsTransparent) return;
        if (shadow.Inset)
            Include(shape.Rect);
        else
            Include(shape.Rect.Inflate(Math.Max(0, shadow.Spread) + shadow.Blur).Offset(shadow.OffsetX, shadow.OffsetY));
        Write(DrawOp.DrawBoxShadow, DrawFlags.AntiAlias, new BoxShadowCmd
        {
            Shape = shape, OffsetX = shadow.OffsetX, OffsetY = shadow.OffsetY, Blur = shadow.Blur,
            Spread = shadow.Spread, Color = shadow.Color, Inset = shadow.Inset ? 1 : 0,
        });
    }

    // ---------------------------------------------------------------- 分组

    public void PushLayer(in LayerOptions options)
    {
        _stack.Push(_state);
        var clip = _state.Clip;
        if (options.Bounds is { } b)
        {
            var dev = b.TransformBounds(_state.Ctm);
            clip = clip is { } c ? c.Intersect(dev) : dev;
        }
        _state = new State(_state.Ctm, clip, true);
        Write(DrawOp.PushLayer, DrawFlags.None, new PushLayerCmd
        {
            Bounds = options.Bounds ?? Rect.Empty,
            HasBounds = options.Bounds.HasValue ? (byte)1 : (byte)0,
            Opacity = Clamp01(options.Opacity),
            BlendMode = options.BlendMode,
        });
    }

    public void PopLayer()
    {
        if (!_state.IsLayer) throw new InvalidOperationException("PopLayer without matching PushLayer.");
        _state = _stack.Pop();
        WriteEmpty(DrawOp.PopLayer);
    }

    /// <summary>嵌入一个子 DisplayList（引用，不拷贝），在当前变换与裁剪下重放。</summary>
    public void DrawDisplayList(DisplayList child)
    {
        if (child is null) throw new ArgumentNullException(nameof(child));
        if (child.IsEmpty) return;
        Include(child.Bounds);
        Write(DrawOp.DrawDisplayList, DrawFlags.None, new DrawDisplayListCmd { DisplayListRef = AddResource(child) });
    }

    /// <summary>不产生像素：把其后的命令归属到某个元素，供 DevTools 与命中测试使用。</summary>
    public void Annotate(long elementId, int category = 0) =>
        Write(DrawOp.Annotation, DrawFlags.None, new AnnotationCmd { ElementId = elementId, Category = category });

    // ---------------------------------------------------------------- 完成

    /// <summary>冻结并返回 DisplayList。未配对的 Save / PushLayer 会被自动关闭。</summary>
    public DisplayList Finish()
    {
        if (_finished) throw new InvalidOperationException("DrawingContext has already been finished.");
        while (_stack.Count > 0)
        {
            if (_state.IsLayer) PopLayer(); else Restore();
        }
        _finished = true;
        var bytes = new byte[_length];
        Buffer.BlockCopy(_buffer, 0, bytes, 0, _length);
        var list = new DisplayList(bytes, _resources.ToArray(), _bounds, _commandCount);
        ReturnBuffer();
        return list;
    }

    public void Dispose() => ReturnBuffer();

    void ReturnBuffer()
    {
        if (_buffer.Length == 0) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
    }

    // ---------------------------------------------------------------- 内部

    void SetCtm(Matrix3x2 ctm) => _state = new State(ctm, _state.Clip, _state.IsLayer);

    void IntersectClip(Rect deviceRect) =>
        _state = new State(_state.Ctm, _state.Clip is { } c ? c.Intersect(deviceRect) : deviceRect, _state.IsLayer);

    void Include(Rect local)
    {
        var dev = local.TransformBounds(_state.Ctm);
        if (_state.Clip is { } c) dev = dev.Intersect(c);
        _bounds = _bounds.Union(dev);
    }

    int AddResource(object resource)
    {
        if (_resourceIndex.TryGetValue(resource, out int idx)) return idx;
        idx = _resources.Count;
        _resources.Add(resource);
        _resourceIndex.Add(resource, idx);
        return idx;
    }

    PaintData Encode(Paint paint) =>
        paint.Brush is SolidColorBrush s
            ? new PaintData(s.Color, -1, paint.BlendMode)
            : new PaintData(default, AddResource(paint.Brush), paint.BlendMode);

    static DrawFlags Flags(Paint paint) => paint.AntiAlias ? DrawFlags.AntiAlias : DrawFlags.None;

    static bool IsInvisible(Paint paint) =>
        paint.Brush is SolidColorBrush s && s.Color.IsTransparent && paint.BlendMode == BlendMode.SrcOver;

    static bool IsDrawable(Stroke stroke) => stroke is not null && stroke.Width > 0 && IsFinite(stroke.Width);

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    static bool IsFinite(in Matrix3x2 m) =>
        IsFinite(m.M11) && IsFinite(m.M12) && IsFinite(m.M21) && IsFinite(m.M22) && IsFinite(m.M31) && IsFinite(m.M32);

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    void WriteEmpty(DrawOp op)
    {
        EnsureCapacity(4);
        _buffer[_length] = (byte)op;
        _buffer[_length + 1] = 0;
        _buffer[_length + 2] = 1;
        _buffer[_length + 3] = 0;
        _length += 4;
        _commandCount++;
    }

    unsafe void Write<T>(DrawOp op, DrawFlags flags, T payload) where T : unmanaged
    {
        if (_finished) throw new InvalidOperationException("DrawingContext has already been finished.");
        int size = 4 + ((sizeof(T) + 3) & ~3);
        EnsureCapacity(size);
        fixed (byte* p = &_buffer[_length])
        {
            new Span<byte>(p, size).Clear();
            *(CommandHeader*)p = new CommandHeader { Op = op, Flags = flags, Size4 = (ushort)(size / 4) };
            *(T*)(p + 4) = payload;
        }
        _length += size;
        _commandCount++;
    }

    void EnsureCapacity(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + extra));
        Buffer.BlockCopy(_buffer, 0, next, 0, _length);
        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
