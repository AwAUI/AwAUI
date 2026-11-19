using System;
using System.Threading;

namespace AwAUI.Diagnostics;

/// <summary>接收一种诊断事件的回调。事件以 <c>in</c> 传递，订阅者不得保存其引用。</summary>
public delegate void DiagnosticHandler<TEvent>(in TEvent e) where TEvent : struct;

/// <summary>
/// 诊断事件总线（design/06 ①）。探针写法：
/// <code>if (DiagnosticHub.IsEnabled(DiagnosticCategory.Layout)) DiagnosticHub.Write(DiagnosticCategory.Layout, new LayoutEvent(...));</code>
/// 未启用时只有一次静态只读字段判断 + 一次掩码读取；整体开关关闭且经裁剪后，探针代码被完全移除。
/// </summary>
/// <remarks>
/// 类别的开启是引用计数的：每次 <see cref="Enable"/> 返回一个令牌，全部令牌释放后类别关闭。
/// 事件按 struct 类型分发到订阅者，不装箱、不使用反射。所有方法线程安全。
/// </remarks>
public static class DiagnosticHub
{
    static long s_mask;
    static readonly int[] s_counts = new int[64];
    static readonly object s_gate = new();

    /// <summary>
    /// 该类别的探针当前是否需要产生事件。超出 0..63 的类别值永远不会被开启（<see cref="Enable"/> 拒绝它们），这里恒为 false。
    /// </summary>
    public static bool IsEnabled(DiagnosticCategory category) =>
        DiagnosticsFeature.IsEnabled && (uint)category < 64u && (Volatile.Read(ref s_mask) & (1L << (int)category)) != 0;

    /// <summary>当前开启的类别位掩码（第 n 位对应值为 n 的类别）。</summary>
    public static long EnabledMask => Volatile.Read(ref s_mask);

    /// <summary>
    /// 开启一个类别。返回的令牌释放时撤销这次开启。诊断被特性开关禁用时返回空操作令牌。
    /// 类别值必须在 0..63 之内（即 <c>Custom + n</c> 中 n 有上限），否则抛 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    public static IDisposable Enable(DiagnosticCategory category)
    {
        int index = (int)category;
        if ((uint)index >= 64) throw new ArgumentOutOfRangeException(nameof(category));
        if (!DiagnosticsFeature.IsEnabled) return EnableToken.None;
        lock (s_gate)
        {
            if (s_counts[index]++ == 0) Volatile.Write(ref s_mask, s_mask | (1L << index));
        }
        return new EnableToken(index);
    }

    static void Release(int index)
    {
        lock (s_gate)
        {
            if (s_counts[index] == 0) return;
            if (--s_counts[index] == 0) Volatile.Write(ref s_mask, s_mask & ~(1L << index));
        }
    }

    /// <summary>订阅某种事件。返回的令牌释放时取消订阅。订阅本身不开启任何类别。</summary>
    public static IDisposable Subscribe<TEvent>(DiagnosticHandler<TEvent> handler) where TEvent : struct
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        Subscribers<TEvent>.Add(handler);
        return new Subscription<TEvent>(handler);
    }

    /// <summary>
    /// 写入一个事件。调用方应先用 <see cref="IsEnabled"/> 判断，以免在关闭时构造事件。
    /// 订阅者抛出的异常被吞掉，探针永远不会因诊断而失败。
    /// </summary>
    public static void Write<TEvent>(DiagnosticCategory category, in TEvent e) where TEvent : struct
    {
        if (!IsEnabled(category)) return;
        var handlers = Subscribers<TEvent>.Handlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            try { handlers[i](in e); }
            catch (Exception) { /* 诊断不得影响被诊断的代码 */ }
        }
    }

    /// <summary>某种事件当前是否有订阅者。</summary>
    public static bool HasSubscribers<TEvent>() where TEvent : struct => Subscribers<TEvent>.Handlers.Length != 0;

    // 每种事件类型一张写时复制的订阅表（静态泛型类，无反射）
    static class Subscribers<TEvent> where TEvent : struct
    {
        public static DiagnosticHandler<TEvent>[] Handlers = Array.Empty<DiagnosticHandler<TEvent>>();

        public static void Add(DiagnosticHandler<TEvent> h)
        {
            lock (s_gate)
            {
                var old = Handlers;
                var n = new DiagnosticHandler<TEvent>[old.Length + 1];
                Array.Copy(old, n, old.Length);
                n[old.Length] = h;
                Volatile.Write(ref Handlers, n);
            }
        }

        public static void Remove(DiagnosticHandler<TEvent> h)
        {
            lock (s_gate)
            {
                var old = Handlers;
                int i = Array.IndexOf(old, h);
                if (i < 0) return;
                var n = new DiagnosticHandler<TEvent>[old.Length - 1];
                Array.Copy(old, 0, n, 0, i);
                Array.Copy(old, i + 1, n, i, old.Length - i - 1);
                Volatile.Write(ref Handlers, n);
            }
        }
    }

    sealed class EnableToken : IDisposable
    {
        public static readonly IDisposable None = new EnableToken(-1);
        int _index;
        public EnableToken(int index) => _index = index;
        public void Dispose()
        {
            int i = Interlocked.Exchange(ref _index, -1);
            if (i >= 0) Release(i);
        }
    }

    sealed class Subscription<TEvent> : IDisposable where TEvent : struct
    {
        DiagnosticHandler<TEvent>? _handler;
        public Subscription(DiagnosticHandler<TEvent> handler) => _handler = handler;
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h != null) Subscribers<TEvent>.Remove(h);
        }
    }
}

/// <summary>日志级别（Console 域）。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>日志探针事件（类别 <see cref="DiagnosticCategory.Log"/>）。Console 域启用时订阅它并转成 <c>Console.entry</c>。</summary>
public readonly struct LogEvent
{
    public LogEvent(LogLevel level, string message, Exception? exception = null, string? sourceFile = null, int sourceLine = 0)
    {
        Level = level; Message = message; Exception = exception; SourceFile = sourceFile; SourceLine = sourceLine;
    }

    public LogLevel Level { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    /// <summary>可选的源文件。</summary>
    public string? SourceFile { get; }
    /// <summary>源文件行号（1 起；0 表示未知）。</summary>
    public int SourceLine { get; }
}
