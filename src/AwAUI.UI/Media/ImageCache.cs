using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Svg;

namespace AwAUI.UI;

/// <summary>
/// 一个 <see cref="UIRoot"/> 的图片缓存（<see cref="UIRoot.Images"/>）：按 <see cref="ImageSource"/> 的键共享解码结果，引用计数，
/// 最后一个使用者卸载后释放（位图在下一次绘制之后才释放，保证仍在光栅化的帧不会引用已释放的像素）。
/// <list type="bullet">
/// <item><b>异步解码</b>：宿主有调度器（<see cref="UIRoot"/> 构造时给定）且来源 <see cref="ImageSource.PreferAsync"/> 时在线程池上加载与解码，
/// 完成后经调度器回到 UI 线程，使用它的元素重新布局与绘制；加载期间元素没有固有尺寸、什么也不画（占位）。
/// 没有调度器（测试、单线程无窗口渲染）时同步解码。</item>
/// <item><b>动画</b>（GIF / WebP）：整个缓存项共享一条时间线（与浏览器对同一图片的处理一致）。只在最近一次绘制中可见时推进：
/// 绘制时按当前帧剩余时长在 <see cref="UIRoot.Clock"/> 上安排一次定时器，到期推进到应显示的帧并让使用者重绘，不做忙轮询；
/// 不可见（被剔除、display:none、visibility:hidden）时不再安排，回到可见时从当前帧继续。帧时长 ≤ 10 ms 按 100 ms 播放（浏览器惯例），
/// 遵守循环次数（播完停在最后一帧）。</item>
/// </list>
/// 只能在 UI 线程上使用。
/// </summary>
public sealed class ImageCache
{
    /// <summary>浏览器把不超过这个时长的帧延迟按 <see cref="ClampedFrameDuration"/> 播放（防止 0 延迟的 GIF 占满 CPU）。</summary>
    public static readonly TimeSpan MinFrameDuration = TimeSpan.FromMilliseconds(10);

    /// <summary>过短帧延迟的替代时长。</summary>
    public static readonly TimeSpan ClampedFrameDuration = TimeSpan.FromMilliseconds(100);

    readonly UIRoot _root;
    readonly Dictionary<CacheKey, ImageEntry> _entries = new();
    readonly List<ImageContent> _releaseAfterPaint = new();
    readonly List<ImageContent> _releaseNow = new();
    List<(ImageSource Source, Exception? Error, UIDiagnostic? Diagnostic)> _notifications = new();
    List<(ImageSource Source, Exception? Error, UIDiagnostic? Diagnostic)> _notificationsSpare = new();
    bool _flushPosted;
    long _paintSerial = 1;

    internal ImageCache(UIRoot root) => _root = root;

    /// <summary>应用资源解析器：资源名 → 流（不认识时返回 null）。<see cref="ImageSource.FromResource"/> 与字符串来源使用它。可能在线程池上调用。</summary>
    public Func<string, Stream?>? ResourceResolver { get; set; }

    /// <summary>相对文件路径的基准目录；null 时为 <see cref="AppContext.BaseDirectory"/>。</summary>
    public string? BaseDirectory { get; set; }

    /// <summary>编码数据的大小上限（字节，默认 64 MB）；超出时加载失败。解码后的像素另受 <c>AwAUI.Imaging</c> 的限制。</summary>
    public long MaxEncodedBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// 解析作为图片的 SVG 时的选项；null 为 <see cref="ImagingSvgImageResolver.DefaultOptions"/>（默认上限，<c>&lt;image&gt;</c> 的
    /// <c>data:</c> URI 用 AwAUI.Imaging 解码）。给定的选项原样使用：要显示嵌入的位图，请同时设置 <see cref="SvgParseOptions.ImageResolver"/>。
    /// </summary>
    public SvgParseOptions? SvgOptions { get; set; }

    /// <summary>是否允许在线程池上解码（需要调度器）。为 false 时总是同步解码。</summary>
    public bool AsyncDecoding { get; set; } = true;

    /// <summary>当前缓存（被至少一个元素使用）的图片数。</summary>
    public int Count => _entries.Count;

    /// <summary>正在加载的图片数。</summary>
    public int PendingLoads { get; private set; }

    /// <summary>
    /// 某个图片加载完成（成功或失败）。在 UI 线程上触发，且从不在布局或绘制过程中触发：异步解码的完成经调度器回到 UI 线程后触发；
    /// 同步解码（发生在帧的布局阶段）的完成推迟到本帧绘制结束之后（有调度器时也会投递一次，二者以先到者为准，每次加载只触发一次）。
    /// 处理器可以修改状态或元素树，改动在下一帧生效；推迟触发时处理器的异常按事件处理器的异常处理（错误边界 / <see cref="UIRoot.UnhandledError"/>）。
    /// 加载失败的 AW1010 诊断同样推迟。
    /// </summary>
    public event Action<ImageSource, Exception?>? Loaded;

    ImageLoadContext CreateContext() => new(ResourceResolver, BaseDirectory, MaxEncodedBytes, SvgOptions ?? ImagingSvgImageResolver.DefaultOptions);

    // ------------------------------------------------------------ 使用者

    internal ImageEntry Acquire(ImageSource source, ReplacedElement user)
    {
        var key = new CacheKey(source);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new ImageEntry(this, source);
            _entries.Add(key, entry);
            entry.Users.Add(user);
            StartLoad(entry);
            return entry;
        }
        entry.Users.Add(user);
        return entry;
    }

    internal void Release(ImageEntry entry, ReplacedElement user)
    {
        if (!entry.Users.Remove(user) || entry.Users.Count > 0) return;
        _entries.Remove(new CacheKey(entry.Source));
        entry.Released = true;
        entry.Timer?.Dispose();
        entry.Timer = null;
        if (entry.Content is { } c)
        {
            entry.Content = null;
            _releaseAfterPaint.Add(c);
        }
    }

    /// <summary>
    /// 通知图片来源的数据已改变，使用它的元素随之更新。返回该来源当前是否在缓存中（没有元素使用它时什么也不做，下次使用时自然读到新数据）。
    /// <list type="bullet">
    /// <item>调用方持有的位图（<see cref="ImageSource.FromBitmap"/>，或自定义来源以 <c>takeOwnership: false</c> 返回的位图）在原处被改写：
    /// 调用 <see cref="Bitmap.Invalidate"/>（内容版本加一，渲染器按版本缓存的 GPU 纹理与 mip 随之失效），使用者重绘。
    /// 改写像素与本调用都必须在 UI 线程上、两帧之间进行（仍在光栅化的帧会读到改写中的像素）。</item>
    /// <item>其他来源（文件、资源、字节、自定义来源）：重新加载与解码。新内容就绪之前继续显示旧内容；加载失败时显示失败占位并报告 AW1010。
    /// 动画从第一帧重新播放。</item>
    /// </list>
    /// 只能在 UI 线程上调用。
    /// </summary>
    public bool NotifyChanged(ImageSource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!_entries.TryGetValue(new CacheKey(source), out var entry)) return false;
        if (entry.Content is { OwnsBitmap: false, Bitmap: { } bitmap, Animation: null, Vector: null })
        {
            bitmap.Invalidate();
            foreach (var u in entry.Users) u.OnContentChanged(layout: false);
            return true;
        }
        if (entry.Content?.Vector is not null && entry.Source.IsDocumentSource) return true; // 已解析的文档不可变，没有可重新加载的数据
        entry.Generation++;
        entry.Timer?.Dispose();
        entry.Timer = null;
        StartLoad(entry);
        // 同步加载在这里已经完成；异步加载完成时再次通知使用者
        foreach (var u in entry.Users) u.OnContentChanged(layout: true);
        return true;
    }

    void StartLoad(ImageEntry entry)
    {
        var context = CreateContext();
        var dispatcher = _root.Dispatcher;
        int generation = entry.Generation;
        if (AsyncDecoding && dispatcher is not null && entry.Source.PreferAsync)
        {
            // 重新加载时旧内容在新内容就绪之前继续显示
            if (entry.Content is null) entry.State = ImageLoadState.Loading;
            PendingLoads++;
            var source = entry.Source;
            Task.Run(() => source.Load(context)).ContinueWith(t =>
            {
                ImageContent? content = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                Exception? error = t.Exception?.InnerException ?? (Exception?)t.Exception;
                if (content is null && error is null) error = new InvalidOperationException("图片加载被取消。");
                dispatcher.Post(() => Complete(entry, content, error, generation: generation));
            }, TaskScheduler.Default);
            return;
        }
        ImageContent? result = null;
        Exception? err = null;
        try
        {
            result = entry.Source.Load(context);
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            err = ex;
        }
        Complete(entry, result, err, synchronous: true, generation: generation);
    }

    /// <summary>解码失败的异常（坏数据、不支持的格式、I/O、超限）；其他异常（程序错误）照常抛出。</summary>
    static bool IsLoadFailure(Exception ex) =>
        ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException
            or SvgParseException or ArgumentException or OutOfMemoryException or InvalidOperationException;

    void Complete(ImageEntry entry, ImageContent? content, Exception? error, bool synchronous = false, int generation = 0)
    {
        if (!synchronous) PendingLoads--;
        if (entry.Released || generation != entry.Generation)
        {
            // 加载期间最后一个使用者已卸载，或之后又开始了一次重新加载（这次的结果已过时）
            content?.Release();
            return;
        }
        // 重新加载：旧内容可能仍被已录制的帧引用，绘制之后再释放
        if (entry.Content is { } old && !ReferenceEquals(old, content)) _releaseAfterPaint.Add(old);
        entry.Timer?.Dispose();
        entry.Timer = null;
        entry.Content = content;
        entry.Error = error;
        entry.State = content is not null ? ImageLoadState.Ready : ImageLoadState.Failed;
        entry.FrameIndex = 0;
        entry.FrameStart = null;
        entry.LoopsDone = 0;
        entry.Finished = false;
        var diagnostic = error is null ? null : new UIDiagnostic("AW1010", DiagnosticSeverity.Warning,
            "图片加载失败（" + entry.Source + "）：" + error.Message, null);
        if (!synchronous)
        {
            foreach (var u in entry.Users) u.OnContentChanged(layout: true);
            if (diagnostic is not null) _root.ReportDiagnostic(diagnostic);
            Loaded?.Invoke(entry.Source, error);
            return;
        }
        // 同步解码发生在布局阶段：用户处理器（Loaded、Diagnostic）推迟到绘制结束之后，不在布局中途运行
        if (diagnostic is null && Loaded is null) return;
        _notifications.Add((entry.Source, error, diagnostic));
        if (!_flushPosted && _root.Dispatcher is { } dispatcher)
        {
            _flushPosted = true;
            dispatcher.Post(FlushNotifications);
        }
    }

    /// <summary>触发推迟的加载通知（绘制结束后，或调度器回调时）。处理器中新产生的通知留到下一次。</summary>
    void FlushNotifications()
    {
        _flushPosted = false;
        if (_notifications.Count == 0) return;
        var list = _notifications;
        _notifications = _notificationsSpare;
        _notificationsSpare = list;
        try
        {
            foreach (var (source, error, diagnostic) in list)
            {
                // 处理器的异常与事件处理器一样交给错误边界 / UnhandledError（没有订阅者时在帧末尾重新抛出），其余通知照常触发
                try
                {
                    if (diagnostic is not null) _root.ReportDiagnostic(diagnostic);
                    Loaded?.Invoke(source, error);
                }
                catch (Exception ex)
                {
                    _root.ReportError(ex, null, ErrorPhase.EventHandler);
                }
            }
        }
        finally
        {
            list.Clear();
        }
    }

    // ------------------------------------------------------------ 绘制

    /// <summary>一次绘制结束（<see cref="RenderPipeline"/> 调用）：释放上一轮之前卸载的图片，绘制轮次加一。</summary>
    internal void AfterPaint()
    {
        // 上一轮卸载的内容此时已不被任何帧引用（本轮的帧已重录，不含它们）
        foreach (var c in _releaseNow) c.Release();
        _releaseNow.Clear();
        _releaseNow.AddRange(_releaseAfterPaint);
        _releaseAfterPaint.Clear();
        _paintSerial++;
        FlushNotifications();
    }

    /// <summary>缓存项在本轮绘制中可见：动画据此安排下一帧的定时器。</summary>
    internal void NotePainted(ImageEntry entry)
    {
        entry.PaintedSerial = _paintSerial;
        if (entry.Content?.Animation is not { } anim || entry.Finished || entry.Timer is not null || entry.Released) return;
        var clock = _root.Clock;
        if (clock is null) return;
        var now = clock.Now;
        if (entry.FrameStart is not { } start || now - start > FrameDuration(anim, entry.FrameIndex))
        {
            // 第一次显示，或暂停（不可见）之后恢复：当前帧从现在起完整显示一个时长
            entry.FrameStart = start = now;
        }
        var remaining = FrameDuration(anim, entry.FrameIndex) - (now - start);
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        entry.Timer = clock.Schedule(remaining, () => OnFrameTimer(entry));
    }

    /// <summary>帧时长（≤ 10 ms 按 100 ms）。</summary>
    public static TimeSpan FrameDuration(AnimatedImage animation, int index)
    {
        if (animation is null) throw new ArgumentNullException(nameof(animation));
        var d = animation.Frames[index].Duration;
        return d <= MinFrameDuration ? ClampedFrameDuration : d;
    }

    void OnFrameTimer(ImageEntry entry)
    {
        entry.Timer = null;
        if (entry.Released || entry.Content is not { Animation: { } anim, Bitmap: { } frame } content) return;
        // 最近一轮绘制中不可见：暂停，回到可见时由 NotePainted 恢复
        if (entry.PaintedSerial < _paintSerial - 1) return;
        var clock = _root.Clock!;
        var now = clock.Now;
        var start = entry.FrameStart ?? now;
        int index = entry.FrameIndex;
        int count = anim.FrameCount;
        // 推进到现在应显示的帧（定时器可能晚到）；最多走一轮，过期太久时从当前帧重新计时
        for (int steps = 0; steps < count; steps++)
        {
            var d = FrameDuration(anim, index);
            if (now - start < d) break;
            start += d;
            if (index + 1 < count)
            {
                index++;
                continue;
            }
            entry.LoopsDone++;
            if (anim.LoopCount != 0 && entry.LoopsDone >= anim.LoopCount)
            {
                entry.Finished = true;
                break;
            }
            index = 0;
        }
        if (now - start >= FrameDuration(anim, index)) start = now;
        entry.FrameStart = start;
        if (index == entry.FrameIndex)
        {
            // 定时器比帧时长早到（系统定时器与单调时钟的粒度不同）：没有换帧也就没有重绘与 NotePainted，必须自己续上，否则动画就此停住
            if (!entry.Finished)
            {
                var remaining = FrameDuration(anim, index) - (now - start);
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                entry.Timer = clock.Schedule(remaining, () => OnFrameTimer(entry));
            }
            return;
        }
        entry.FrameIndex = index;
        try
        {
            anim.RenderFrame(index, frame);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            entry.Finished = true;
            _root.ReportDiagnostic(new UIDiagnostic("AW1010", DiagnosticSeverity.Warning,
                "动画图片的第 " + index + " 帧解码失败（" + entry.Source + "）：" + ex.Message, null));
            // 不返回：像素可能已部分改写，照常让使用者重绘，软件（局部光栅）与 GPU 后端显示一致
        }
        finally
        {
            // 帧在同一张位图上改写：内容版本加一，渲染器按 (位图, 版本) 缓存的 GPU 纹理与 mip 随之失效（中途失败时像素也可能已改动）
            frame.Invalidate();
        }
        foreach (var u in entry.Users) u.OnContentChanged(layout: false);
    }

    readonly struct CacheKey : IEquatable<CacheKey>
    {
        public CacheKey(ImageSource source) => Source = source;

        public readonly ImageSource Source;

        public bool Equals(CacheKey other) => Source.Equals(other.Source);

        public override bool Equals(object? obj) => obj is CacheKey k && Equals(k);

        public override int GetHashCode() => HashCodeCompat.Combine(Source.GetType().GetHashCode(), Source.Key.GetHashCode());
    }
}

/// <summary>图片的加载状态。</summary>
public enum ImageLoadState : byte
{
    /// <summary>正在加载（异步解码中）。</summary>
    Loading,

    /// <summary>已解码。</summary>
    Ready,

    /// <summary>加载或解码失败。</summary>
    Failed,
}

/// <summary>缓存中的一张图片：解码结果、使用者与动画播放状态。</summary>
internal sealed class ImageEntry
{
    public ImageEntry(ImageCache cache, ImageSource source)
    {
        Cache = cache;
        Source = source;
    }

    public readonly ImageCache Cache;
    public readonly ImageSource Source;
    public readonly HashSet<ReplacedElement> Users = new(ElementReferenceComparer.Instance);
    public ImageLoadState State = ImageLoadState.Loading;
    public ImageContent? Content;
    public Exception? Error;
    public bool Released;

    /// <summary>加载的代数（<see cref="ImageCache.NotifyChanged"/> 加一）：过时的异步结果被丢弃。</summary>
    public int Generation;

    // 动画
    public int FrameIndex;
    public int LoopsDone;
    public bool Finished;
    public TimeSpan? FrameStart;
    public IDisposable? Timer;
    public long PaintedSerial;
}

internal sealed class ElementReferenceComparer : IEqualityComparer<ReplacedElement>
{
    public static readonly ElementReferenceComparer Instance = new();

    public bool Equals(ReplacedElement? x, ReplacedElement? y) => ReferenceEquals(x, y);

    public int GetHashCode(ReplacedElement obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
