using System;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Svg;

namespace AwAUI.UI;

/// <summary>
/// 图片来源（<see cref="Image.Source"/>）。相等的来源（同类型且 <see cref="Key"/> 相等）在同一个 <see cref="UIRoot"/> 内共享一份解码结果
/// （<see cref="ImageCache"/>），最后一个使用它的元素卸载时释放。
/// <para>
/// 内建来源：<see cref="FromBitmap"/>（已解码位图，调用方持有）、<see cref="FromBytes"/>（编码字节）、<see cref="FromStream"/>、
/// <see cref="FromFile"/>、<see cref="FromResource"/>（应用资源名，经 <see cref="ImageCache.ResourceResolver"/> 解析）、
/// <see cref="FromUri"/>（字符串隐式转换走它：<c>data:</c> URI、资源名或文件路径）、<see cref="FromSvg"/>。
/// 编码数据经 <c>AwAUI.Imaging</c>（PNG、JPEG、GIF、WebP、BMP、ICO）或 <c>AwAUI.Svg</c> 解码。
/// </para>
/// <para>派生类可实现自定义来源（网络、数据库……）：<see cref="Load"/> 可能在线程池上调用，必须线程安全且不访问元素树。</para>
/// </summary>
public abstract class ImageSource : IEquatable<ImageSource>
{
    /// <summary>缓存键：同类型、键相等的来源共享解码结果。</summary>
    public abstract object Key { get; }

    /// <summary>是否适合在 UI 线程之外解码（文件、流、较大的编码数据）。宿主有调度器时才会异步。</summary>
    public virtual bool PreferAsync => false;

    /// <summary>来源本身就是已解析的不可变文档（<see cref="FromSvg"/>）：<see cref="ImageCache.NotifyChanged"/> 无需重新加载。</summary>
    internal virtual bool IsDocumentSource => false;

    /// <summary>加载并解码。可能在线程池上调用。失败时抛出异常（元素显示为空，并报告诊断）。</summary>
    protected internal abstract ImageContent Load(ImageLoadContext context);

    public bool Equals(ImageSource? other) =>
        other is not null && (ReferenceEquals(this, other) || (other.GetType() == GetType() && Equals(Key, other.Key)));

    public override bool Equals(object? obj) => obj is ImageSource s && Equals(s);

    public override int GetHashCode() => Key.GetHashCode();

    public static bool operator ==(ImageSource? a, ImageSource? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(ImageSource? a, ImageSource? b) => !(a == b);

    /// <summary>字符串来源（标记中的 <c>src="..."</c>）：见 <see cref="FromUri"/>。</summary>
    public static implicit operator ImageSource?(string? uri) => uri is null ? null : FromUri(uri);

    /// <summary>
    /// 已解码的位图（调用方持有，框架不会释放它）。以位图对象本身为键。
    /// 显示期间不要释放位图。在原处改写像素后调用 <see cref="ImageCache.NotifyChanged"/>（<c>root.Images.NotifyChanged(source)</c>，
    /// 相等的来源即可）：它让位图的 <see cref="Bitmap.Version"/> 加一（GPU 纹理与 mip 缓存据此刷新）并重绘使用者；
    /// 只调用 <see cref="Bitmap.Invalidate"/> 不会触发重绘。改写与通知须在 UI 线程上、两帧之间进行。
    /// </summary>
    public static ImageSource FromBitmap(Bitmap bitmap) => new BitmapImageSource(bitmap ?? throw new ArgumentNullException(nameof(bitmap)));

    /// <summary>编码的图片数据（PNG、JPEG、GIF、WebP、BMP、ICO 或 SVG 文本）。<paramref name="key"/> 为 null 时以数组对象本身为键；数组之后不得修改。</summary>
    public static ImageSource FromBytes(byte[] data, string? key = null) =>
        new BytesImageSource(data ?? throw new ArgumentNullException(nameof(data)), key);

    /// <summary>
    /// 从流读取编码数据：第一次加载时（可能在解码线程上）读完并释放流。不超过 <see cref="MaxRetainedStreamBytes"/> 的数据由来源保留，
    /// 因此同一个来源可以反复加载（元素卸载后重新挂载、多个 <see cref="UIRoot"/>）；更大的数据不保留，再次加载失败并报告 AW1010
    /// （此时请改用 <see cref="FromBytes"/> 或 <see cref="FromFile"/>）。<paramref name="key"/> 为 null 时以流对象为键。
    /// </summary>
    public static ImageSource FromStream(Stream stream, string? key = null) =>
        new StreamImageSource(stream ?? throw new ArgumentNullException(nameof(stream)), key);

    /// <summary><see cref="FromStream"/> 的来源最多保留多少字节的编码数据以便重复加载（16 MiB）。</summary>
    public const int MaxRetainedStreamBytes = 16 * 1024 * 1024;

    /// <summary>文件路径（相对路径按 <see cref="ImageCache.BaseDirectory"/> 解析）。</summary>
    public static ImageSource FromFile(string path) =>
        new UriImageSource(path ?? throw new ArgumentNullException(nameof(path)), UriImageSource.Kind.File);

    /// <summary>应用资源名，经 <see cref="ImageCache.ResourceResolver"/> 打开。</summary>
    public static ImageSource FromResource(string name) =>
        new UriImageSource(name ?? throw new ArgumentNullException(nameof(name)), UriImageSource.Kind.Resource);

    /// <summary>
    /// 字符串来源：<c>data:[类型][;base64],数据</c> 直接解码；其余先交给 <see cref="ImageCache.ResourceResolver"/>（若设置且能打开），
    /// 否则按文件路径（可带 <c>file://</c> 前缀）读取。
    /// </summary>
    public static ImageSource FromUri(string uri)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return new DataUriImageSource(uri);
        return new UriImageSource(uri, UriImageSource.Kind.Auto);
    }

    /// <summary>已解析的 SVG 文档（作为图片显示：与浏览器的 <c>&lt;img src=svg&gt;</c> 一样，currentColor 为黑色）。</summary>
    public static ImageSource FromSvg(SvgDocument document) => new SvgImageSource(document ?? throw new ArgumentNullException(nameof(document)));

    // ------------------------------------------------------------ 内建来源

    sealed class BitmapImageSource : ImageSource
    {
        readonly Bitmap _bitmap;

        public BitmapImageSource(Bitmap bitmap) => _bitmap = bitmap;

        public override object Key => _bitmap;

        protected internal override ImageContent Load(ImageLoadContext context) => ImageContent.FromBitmap(_bitmap, takeOwnership: false);

        public override string ToString() => "bitmap " + _bitmap.Width + "×" + _bitmap.Height;
    }

    sealed class SvgImageSource : ImageSource
    {
        readonly SvgDocument _document;

        public SvgImageSource(SvgDocument document) => _document = document;

        public override object Key => _document;

        internal override bool IsDocumentSource => true;

        protected internal override ImageContent Load(ImageLoadContext context) => ImageContent.FromSvg(_document);

        public override string ToString() => "svg document";
    }

    sealed class BytesImageSource : ImageSource
    {
        readonly byte[] _data;
        readonly string? _key;

        public BytesImageSource(byte[] data, string? key)
        {
            _data = data;
            _key = key;
        }

        public override object Key => (object?)_key ?? _data;

        public override bool PreferAsync => _data.Length >= ImageLoadContext.AsyncThresholdBytes;

        protected internal override ImageContent Load(ImageLoadContext context)
        {
            if (_data.LongLength > context.MaxEncodedBytes) throw new InvalidDataException("图片数据超过 MaxEncodedBytes。");
            return ImageContent.Decode(_data, context);
        }

        public override string ToString() => _key ?? ("bytes[" + _data.Length + "]");
    }

    /// <summary>
    /// 流来源：流只读一次（读完即释放），编码数据不超过 <see cref="MaxRetainedStreamBytes"/> 时保留以便再次加载。
    /// 读取在锁内进行（多个 <see cref="UIRoot"/> 可能同时在线程池上加载同一个来源）。
    /// </summary>
    sealed class StreamImageSource : ImageSource
    {
        readonly Stream _stream;
        readonly string? _key;
        readonly object _gate = new();
        byte[]? _data;
        string? _consumed;

        public StreamImageSource(Stream stream, string? key)
        {
            _stream = stream;
            _key = key;
        }

        public override object Key => (object?)_key ?? _stream;

        public override bool PreferAsync => true;

        protected internal override ImageContent Load(ImageLoadContext context)
        {
            byte[] data;
            lock (_gate)
            {
                if (_data is null)
                {
                    if (_consumed is not null) throw new InvalidOperationException(_consumed);
                    try
                    {
                        using (_stream) data = ImageLoadContext.ReadAll(_stream, context.MaxEncodedBytes);
                    }
                    catch (Exception ex)
                    {
                        _consumed = "图片流在第一次加载时读取失败（" + ex.Message + "），流已释放，无法再次加载；请创建新的来源。";
                        throw;
                    }
                    if (data.Length <= MaxRetainedStreamBytes) _data = data;
                    else _consumed = "图片流的数据（" + data.Length + " 字节）超过 ImageSource.MaxRetainedStreamBytes，第一次加载后没有保留，" +
                                     "无法再次加载；请改用 ImageSource.FromBytes 或 FromFile。";
                }
                else
                {
                    data = _data;
                }
            }
            if (data.LongLength > context.MaxEncodedBytes) throw new InvalidDataException("图片数据超过 MaxEncodedBytes。");
            return ImageContent.Decode(data, context);
        }

        public override string ToString() => _key ?? "stream";
    }

    sealed class DataUriImageSource : ImageSource
    {
        readonly string _uri;

        public DataUriImageSource(string uri) => _uri = uri;

        public override object Key => _uri;

        public override bool PreferAsync => _uri.Length >= ImageLoadContext.AsyncThresholdBytes;

        protected internal override ImageContent Load(ImageLoadContext context) => ImageContent.Decode(ParseDataUri(_uri, context.MaxEncodedBytes), context);

        /// <summary>解析 <c>data:</c> URI（RFC 2397）：base64 或百分号编码。</summary>
        internal static byte[] ParseDataUri(string uri, long maxBytes)
        {
            int comma = uri.IndexOf(',');
            if (comma < 0) throw new InvalidDataException("data: URI 缺少逗号。");
            var meta = uri.Substring(5, comma - 5);
            var payload = uri.Substring(comma + 1);
            if (payload.Length / 4L * 3 > maxBytes || (!meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) && payload.Length > maxBytes))
                throw new InvalidDataException("图片数据超过 MaxEncodedBytes。");
            if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.FromBase64String(payload);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("data: URI 的 base64 无效。", ex);
                }
            }
            var unescaped = Uri.UnescapeDataString(payload);
            return System.Text.Encoding.UTF8.GetBytes(unescaped);
        }

        public override string ToString() => _uri.Length <= 48 ? _uri : _uri.Substring(0, 48) + "…";
    }

    sealed class UriImageSource : ImageSource
    {
        internal enum Kind : byte { Auto, File, Resource }

        readonly string _uri;
        readonly Kind _kind;
        readonly string _key;

        public UriImageSource(string uri, Kind kind)
        {
            _uri = uri;
            _kind = kind;
            _key = kind switch { Kind.File => "file:", Kind.Resource => "res:", _ => "uri:" } + uri;
        }

        public override object Key => _key;

        public override bool PreferAsync => true;

        protected internal override ImageContent Load(ImageLoadContext context)
        {
            if (_kind != Kind.File && context.ResourceResolver is { } resolve)
            {
                var stream = resolve(_uri);
                if (stream is not null)
                {
                    using (stream) return ImageContent.Decode(ImageLoadContext.ReadAll(stream, context.MaxEncodedBytes), context);
                }
            }
            if (_kind == Kind.Resource) throw new FileNotFoundException("找不到图片资源：" + _uri);
            var path = _uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(_uri).LocalPath : _uri;
            if (!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.Combine(context.BaseDirectory ?? AppContext.BaseDirectory, path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ImageContent.Decode(ImageLoadContext.ReadAll(fs, context.MaxEncodedBytes), context);
        }

        public override string ToString() => _uri;
    }
}

/// <summary>加载图片时可用的环境（来自 <see cref="ImageCache"/> 的设置；可能在线程池上读取，内容不可变）。</summary>
public sealed class ImageLoadContext
{
    /// <summary>编码数据达到这个大小（字节）的内存来源在 UI 线程之外解码。</summary>
    public const int AsyncThresholdBytes = 64 * 1024;

    internal ImageLoadContext(Func<string, Stream?>? resolver, string? baseDirectory, long maxEncodedBytes, SvgParseOptions? svgOptions)
    {
        ResourceResolver = resolver;
        BaseDirectory = baseDirectory;
        MaxEncodedBytes = maxEncodedBytes;
        SvgOptions = svgOptions;
    }

    /// <summary>应用资源解析器（资源名 → 流；不认识时返回 null）。</summary>
    public Func<string, Stream?>? ResourceResolver { get; }

    /// <summary>相对文件路径的基准目录；null 时为 <see cref="AppContext.BaseDirectory"/>。</summary>
    public string? BaseDirectory { get; }

    /// <summary>编码数据的大小上限（字节）。</summary>
    public long MaxEncodedBytes { get; }

    /// <summary>解析 SVG 的限制；null 为 <see cref="SvgParseOptions.Default"/>。</summary>
    public SvgParseOptions? SvgOptions { get; }

    /// <summary>读完流（不超过 <paramref name="maxBytes"/>，超出时抛出 <see cref="InvalidDataException"/>）。</summary>
    public static byte[] ReadAll(Stream stream, long maxBytes)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (stream.CanSeek)
        {
            long len = stream.Length - stream.Position;
            if (len > maxBytes || len > int.MaxValue) throw new InvalidDataException("图片数据超过 MaxEncodedBytes。");
        }
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new InvalidDataException("图片数据超过 MaxEncodedBytes。");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// 解码后的图片内容：静态位图、动画（GIF / WebP，带当前帧位图）或矢量（SVG）。由 <see cref="ImageSource.Load"/> 产生，
/// 归 <see cref="ImageCache"/> 管理：最后一个使用者卸载后（下一次绘制之后）释放其拥有的位图。
/// </summary>
public sealed class ImageContent
{
    Bitmap? _bitmap;
    readonly bool _ownsBitmap;

    ImageContent(Bitmap? bitmap, bool ownsBitmap, AnimatedImage? animation, SvgDocument? vector)
    {
        _bitmap = bitmap;
        _ownsBitmap = ownsBitmap;
        Animation = animation;
        Vector = vector;
    }

    /// <summary>位图（动画为当前帧）；矢量内容为 null。</summary>
    public Bitmap? Bitmap => _bitmap;

    /// <summary>位图归内容所有（解码结果）；为 false 时是调用方持有的位图（<see cref="FromBitmap"/> 且不转移所有权）。</summary>
    internal bool OwnsBitmap => _ownsBitmap;

    /// <summary>多帧动画；静态图片为 null。</summary>
    public AnimatedImage? Animation { get; }

    /// <summary>矢量内容；位图为 null。</summary>
    public SvgDocument? Vector { get; }

    /// <summary>固有尺寸（CSS 像素：一个图片像素对应一个逻辑像素；SVG 为其固有尺寸）。</summary>
    public Size NaturalSize =>
        Vector is { } v ? v.IntrinsicSize
        : Animation is { } a ? new Size(a.Width, a.Height)
        : _bitmap is { } b ? new Size(b.Width, b.Height)
        : default;

    /// <summary>已解码的位图。<paramref name="takeOwnership"/> 为 true 时内容释放时一并释放位图。</summary>
    public static ImageContent FromBitmap(Bitmap bitmap, bool takeOwnership) =>
        new(bitmap ?? throw new ArgumentNullException(nameof(bitmap)), takeOwnership, null, null);

    /// <summary>动画：立即合成第一帧到一张新位图（之后播放时在同一张位图上更新）。只有一帧时按静态位图处理。</summary>
    public static ImageContent FromAnimation(AnimatedImage animation)
    {
        if (animation is null) throw new ArgumentNullException(nameof(animation));
        var frame = new Bitmap(animation.Size);
        try
        {
            animation.RenderFrame(0, frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
        return new ImageContent(frame, true, animation.FrameCount > 1 ? animation : null, null);
    }

    /// <summary>矢量图（SVG 文档）。</summary>
    public static ImageContent FromSvg(SvgDocument document) => new(null, false, null, document ?? throw new ArgumentNullException(nameof(document)));

    /// <summary>
    /// 解码编码数据：按文件头识别位图格式（GIF / WebP 可能是动画），识别不了而内容像 XML / SVG 文本时按 SVG 解析。
    /// 位图解码的耗时与内存受 <c>AwAUI.Imaging</c> 的限制约束（与输入大小成比例），SVG 受 <see cref="SvgParseOptions"/> 约束。
    /// </summary>
    public static ImageContent Decode(byte[] data, ImageLoadContext? context = null)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        var format = ImageDecoder.DetectFormat(data);
        switch (format)
        {
            case ImageFormat.Unknown:
                if (LooksLikeSvg(data)) return FromSvg(SvgDocument.Parse(data, context?.SvgOptions));
                throw new NotSupportedException("无法识别的图片格式。");
            case ImageFormat.Gif:
            case ImageFormat.WebP:
                return FromAnimation(ImageDecoder.DecodeAnimation(data));
            default:
                return FromBitmap(ImageDecoder.Decode(data), takeOwnership: true);
        }
    }

    /// <summary>跳过 BOM 与空白后以 <c>&lt;</c> 开头（XML 声明、注释或 <c>&lt;svg</c>）。</summary>
    static bool LooksLikeSvg(byte[] data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        else if (data.Length >= 2 && (data[0] == 0xFE || data[0] == 0xFF)) return true; // UTF-16 文本，交给 SVG 解析器判断
        while (i < data.Length && i < 4096 && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < data.Length && data[i] == (byte)'<';
    }

    /// <summary>释放拥有的位图。</summary>
    internal void Release()
    {
        if (_ownsBitmap) _bitmap?.Dispose();
        _bitmap = null;
    }
}
