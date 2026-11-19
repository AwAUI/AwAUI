using System;
using System.Collections.Generic;
using System.IO;
using AwAUI.Graphics;

namespace AwAUI.Imaging;

/// <summary>一帧显示结束、下一帧绘制之前如何处理这一帧覆盖的区域。</summary>
public enum FrameDisposal
{
    /// <summary>保留（GIF 处置方式 0/1，WebP 不处置）。</summary>
    None,
    /// <summary>把帧区域清为全透明（GIF 处置方式 2 按浏览器惯例清为透明而非背景色；WebP 处置到背景）。</summary>
    Background,
    /// <summary>把帧区域恢复为绘制这一帧之前的内容（GIF 处置方式 3）。</summary>
    Previous,
}

/// <summary>帧像素如何与画布上已有内容合成。</summary>
public enum FrameBlending
{
    /// <summary>按 alpha 叠加到已有内容上（GIF 的透明色不覆盖已有像素；WebP 的 alpha 混合）。</summary>
    Over,
    /// <summary>直接覆盖帧区域（WebP 的「不混合」）。</summary>
    Source,
}

/// <summary>动画中一帧的描述（像素需经 <see cref="AnimatedImage.RenderFrame(int)"/> 合成后取得）。</summary>
public sealed class AnimationFrame
{
    internal AnimationFrame(int index, PixelRect bounds, TimeSpan duration, FrameDisposal disposal, FrameBlending blending)
    {
        Index = index;
        Bounds = bounds;
        Duration = duration;
        Disposal = disposal;
        Blending = blending;
    }

    public int Index { get; }

    /// <summary>帧在画布中的区域（文件中声明的值，可能超出画布，超出部分不绘制）。</summary>
    public PixelRect Bounds { get; }

    /// <summary>
    /// 文件中声明的显示时长（GIF 精度为 10 ms）。原样返回，不做浏览器那样的「过短按 100 ms 处理」，由播放方决定。
    /// </summary>
    public TimeSpan Duration { get; }

    public FrameDisposal Disposal { get; }

    public FrameBlending Blending { get; }
}

/// <summary>
/// 解码后的动画（GIF、动画 WebP；静态图片为单帧）。
/// <para>
/// 选择「描述 + 合成器」而不是一次性给出全部整画布帧：容器在构造时即完整解析与校验，帧像素按需解码并按顺序合成到
/// 一张非预乘 RGBA 画布上，内存只与画布大小成正比（WebP 另需一张同尺寸的处置后画布，GIF 需要保存「恢复」区域）。
/// <see cref="RenderFrame(int)"/> 返回的是处置、混合都已生效后的整张画布。顺序访问每帧 O(帧面积)；向前跳转
/// （包括循环回第 0 帧）会从头重新合成。帧数据损坏时在合成到该帧时抛出 <see cref="InvalidDataException"/>。
/// </para>
/// <para>GIF 与动画 WebP 的实例保留对输入字节数组的引用（帧在合成时才从中解码）：创建之后不要修改或复用该数组
/// （例如归还给数组池），否则之后的帧会解出错误内容或抛出 <see cref="InvalidDataException"/>。</para>
/// <para>实例不是线程安全的。</para>
/// </summary>
public sealed class AnimatedImage
{
    readonly AnimationCompositor _compositor;
    readonly AnimationFrame[] _frames;
    int _current = -1;

    internal AnimatedImage(PixelSize size, int loopCount, AnimationFrame[] frames, AnimationCompositor compositor, Color? backgroundColor = null)
    {
        Size = size;
        LoopCount = loopCount;
        _frames = frames;
        _compositor = compositor;
        BackgroundColor = backgroundColor;
        long ticks = 0;
        foreach (var f in frames) ticks += f.Duration.Ticks;
        TotalDuration = TimeSpan.FromTicks(ticks);
    }

    /// <summary>画布尺寸。</summary>
    public PixelSize Size { get; }
    public int Width => Size.Width;
    public int Height => Size.Height;

    /// <summary>
    /// 总共播放的次数，0 表示无限循环。GIF 的 NETSCAPE 循环次数 N（N &gt; 0）按浏览器惯例表示「再重复 N 次」，
    /// 因此这里为 N + 1；没有该扩展时为 1。WebP 的 ANIM 循环次数原样返回。静态图片为 1。
    /// </summary>
    public int LoopCount { get; }

    /// <summary>文件声明的背景色（WebP ANIM 块）。仅供参考：与浏览器一致，合成时画布从全透明开始。</summary>
    public Color? BackgroundColor { get; }

    public IReadOnlyList<AnimationFrame> Frames => _frames;
    public int FrameCount => _frames.Length;
    public TimeSpan TotalDuration { get; }

    /// <summary>合成第 <paramref name="index"/> 帧并返回整张画布（预乘 RGBA）。</summary>
    /// <exception cref="InvalidDataException">该帧或之前某帧的数据损坏。</exception>
    /// <exception cref="NotSupportedException">帧使用了不支持的特性。</exception>
    public Bitmap RenderFrame(int index)
    {
        var bitmap = new Bitmap(Size);
        try
        {
            RenderFrame(index, bitmap);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>合成第 <paramref name="index"/> 帧并写入同尺寸的位图（按目标的像素格式预乘写入）。</summary>
    public void RenderFrame(int index, Bitmap destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (destination.Size != Size) throw new ArgumentException("Destination size must match the animation canvas.", nameof(destination));
        Seek(index);
        _compositor.CopyTo(destination);
    }

    /// <summary>合成第 <paramref name="index"/> 帧，返回非预乘 RGBA 字节（行紧密排列）的副本。</summary>
    public byte[] RenderFrameRgba(int index)
    {
        Seek(index);
        return (byte[])_compositor.Canvas.Clone();
    }

    void Seek(int index)
    {
        if ((uint)index >= (uint)_frames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _current) return;
        try
        {
            if (index < _current || _current < 0)
            {
                _current = -1;
                _compositor.Reset();
            }
            while (_current < index)
            {
                _compositor.Compose(_current + 1);
                _current++;
            }
        }
        catch (Exception e)
        {
            // 合成到一半的画布不可用，下次从头开始
            _current = -1;
            if (DecodeGuard.IsInternalFailure(e)) throw DecodeGuard.Wrap("Image", e);
            throw;
        }
    }

    /// <summary>把单张静态位图包装成单帧动画（复制预乘像素，不经过非预乘画布；调用方仍负责释放位图）。</summary>
    internal static AnimatedImage FromStill(Bitmap bitmap)
    {
        var frame = new AnimationFrame(0, new PixelRect(0, 0, bitmap.Width, bitmap.Height), TimeSpan.Zero, FrameDisposal.None, FrameBlending.Source);
        return new AnimatedImage(bitmap.Size, 1, new[] { frame }, new StillCompositor(bitmap));
    }

    /// <summary>把非预乘 RGBA 包装成单帧动画。</summary>
    internal static AnimatedImage FromRgba(byte[] rgba, PixelSize size)
    {
        var frame = new AnimationFrame(0, new PixelRect(0, 0, size.Width, size.Height), TimeSpan.Zero, FrameDisposal.None, FrameBlending.Source);
        return new AnimatedImage(size, 1, new[] { frame }, new RgbaCompositor(rgba, size));
    }

    sealed class RgbaCompositor : AnimationCompositor
    {
        public RgbaCompositor(byte[] rgba, PixelSize size) : base(size) => rgba.CopyTo(Canvas, 0);
        public override void Reset() { }
        public override void Compose(int index) { }
    }

    /// <summary>保存预乘 RGBA 字节的单帧合成器。</summary>
    sealed class StillCompositor : AnimationCompositor
    {
        readonly byte[] _premultiplied;

        public StillCompositor(Bitmap bitmap) : base(bitmap.Size, allocateCanvas: false)
        {
            int w = bitmap.Width;
            _premultiplied = new byte[checked(w * bitmap.Height * 4)];
            bool bgra = bitmap.Format == PixelFormat.Bgra8888;
            for (int y = 0; y < bitmap.Height; y++)
            {
                var row = bitmap.GetRow(y);
                var dst = _premultiplied.AsSpan(y * w * 4, w * 4);
                row.CopyTo(dst);
                if (bgra)
                    for (int x = 0; x < dst.Length; x += 4) (dst[x], dst[x + 2]) = (dst[x + 2], dst[x]);
            }
        }

        public override byte[] Canvas
        {
            get
            {
                var r = new byte[_premultiplied.Length];
                for (int i = 0; i < r.Length; i += 4)
                {
                    int a = _premultiplied[i + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        int v = _premultiplied[i + c];
                        r[i + c] = (byte)(a == 0 || a == 255 ? v : Math.Min(255, (v * 255 + a / 2) / a));
                    }
                    r[i + 3] = (byte)a;
                }
                return r;
            }
        }

        public override void Reset() { }
        public override void Compose(int index) { }

        public override void CopyTo(Bitmap destination)
        {
            int w = CanvasSize.Width;
            bool bgra = destination.Format == PixelFormat.Bgra8888;
            for (int y = 0; y < CanvasSize.Height; y++)
            {
                var row = destination.GetRow(y);
                _premultiplied.AsSpan(y * w * 4, w * 4).CopyTo(row);
                if (bgra)
                    for (int x = 0; x < row.Length; x += 4) (row[x], row[x + 2]) = (row[x + 2], row[x]);
            }
        }
    }
}

/// <summary>按顺序把帧合成到非预乘 RGBA 画布上。各格式实现自己的处置与混合规则。</summary>
internal abstract class AnimationCompositor
{
    readonly byte[] _canvas;

    protected AnimationCompositor(PixelSize size, bool allocateCanvas = true)
    {
        CanvasSize = size;
        _canvas = allocateCanvas ? new byte[checked(size.Width * size.Height * 4)] : Array.Empty<byte>();
    }

    public PixelSize CanvasSize { get; }

    /// <summary>当前合成结果（非预乘 RGBA，行紧密排列）。</summary>
    public virtual byte[] Canvas => _canvas;

    /// <summary>回到第 0 帧之前的状态（画布全透明）。</summary>
    public abstract void Reset();

    /// <summary>在第 <paramref name="index"/> - 1 帧的合成结果上合成第 <paramref name="index"/> 帧。</summary>
    public abstract void Compose(int index);

    /// <summary>把当前画布预乘后写入目标位图。</summary>
    public virtual void CopyTo(Bitmap destination)
    {
        var canvas = Canvas;
        int w = CanvasSize.Width;
        bool bgra = destination.Format == PixelFormat.Bgra8888;
        for (int y = 0; y < CanvasSize.Height; y++)
        {
            var row = destination.GetRow(y);
            int s = y * w * 4;
            for (int x = 0; x < row.Length; x += 4, s += 4)
            {
                int r = canvas[s], g = canvas[s + 1], b = canvas[s + 2], a = canvas[s + 3];
                if (a != 255)
                {
                    r = (r * a + 127) / 255; g = (g * a + 127) / 255; b = (b * a + 127) / 255;
                }
                if (bgra) (r, b) = (b, r);
                row[x] = (byte)r; row[x + 1] = (byte)g; row[x + 2] = (byte)b; row[x + 3] = (byte)a;
            }
        }
    }
}
