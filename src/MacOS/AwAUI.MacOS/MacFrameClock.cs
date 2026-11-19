using System;
using System.Collections.Generic;
using System.Threading;
using AwAUI.MacOS.Interop;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>窗口使用的帧节拍来源。</summary>
public enum MacFrameClockKind
{
    /// <summary>没有显示器同步的节拍源，退回 setNeedsDisplay: 由 AppKit 的显示周期合并重绘。</summary>
    None,

    /// <summary>NSView -displayLinkWithTarget:selector:（macOS 14+），在主线程 run loop 上触发。</summary>
    CADisplayLink,

    /// <summary>CoreVideo CVDisplayLink（旧系统），在后台线程触发后转到主线程。</summary>
    CVDisplayLink,
}

/// <summary>
/// 显示器同步的帧节拍。每次垂直同步在主线程上以 <c>awTick:</c> 消息发给视图，
/// 由 <see cref="MacWindow"/> 决定是否出帧；不需要帧时 <see cref="Stop"/>，应用完全静止。
/// 时间统一为主机时间（秒，见 <see cref="HostTime"/>）。
/// </summary>
internal abstract class MacFrameClock : IDisposable
{
    public const string TickSelector = "awTick:";

    public abstract MacFrameClockKind Kind { get; }
    public bool Running { get; private set; }

    /// <summary>开始（或继续）接收节拍。幂等。</summary>
    public void Start()
    {
        if (Running) return;
        Running = true;
        OnStart();
    }

    /// <summary>暂停节拍。幂等。</summary>
    public void Stop()
    {
        if (!Running) return;
        Running = false;
        OnStop();
    }

    protected abstract void OnStart();
    protected abstract void OnStop();

    /// <summary>窗口移到另一块显示器上。</summary>
    public virtual void ScreenChanged(IntPtr window) { }

    /// <summary>
    /// 主线程上收到 <c>awTick:</c>。返回这一拍的预计呈现时间与刷新间隔（主机时间，秒）；
    /// 返回 false 表示这条消息已过期（例如节拍已停止）。
    /// </summary>
    public abstract bool ReadTick(IntPtr arg, out double targetTime, out double interval);

    public abstract void Dispose();

    /// <summary>按系统能力与环境变量 AWAUI_MAC_FRAME_CLOCK（cadisplaylink / cvdisplaylink / none）创建节拍源。</summary>
    public static MacFrameClock? Create(IntPtr view)
    {
        string? forced = null;
        try { forced = Environment.GetEnvironmentVariable("AWAUI_MAC_FRAME_CLOCK")?.Trim().ToLowerInvariant(); } catch { }
        if (forced == "none") return null;

        if (forced != "cvdisplaylink" && SendByte(view, Sel("respondsToSelector:"), Sel("displayLinkWithTarget:selector:")) != 0)
        {
            var clock = CADisplayLinkClock.TryCreate(view);
            if (clock != null) return clock;
        }
        return CVDisplayLinkClock.TryCreate(view);
    }
}

/// <summary>macOS 14+：NSView 提供的 CADisplayLink，自动跟随视图所在的显示器，回调在主线程。</summary>
internal sealed class CADisplayLinkClock : MacFrameClock
{
    IntPtr _link;

    CADisplayLinkClock(IntPtr link) => _link = link;

    public override MacFrameClockKind Kind => MacFrameClockKind.CADisplayLink;

    public static CADisplayLinkClock? TryCreate(IntPtr view)
    {
        // 目标是视图本身：链接会持有视图，关闭窗口时 invalidate 解除
        var link = Send(view, Sel("displayLinkWithTarget:selector:"), view, Sel(TickSelector));
        if (link == IntPtr.Zero) return null;
        Send(link, Sel("retain"));
        SendBool(link, Sel("setPaused:"), 1);
        const string foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
        var runLoop = Send(Class("NSRunLoop"), Sel("mainRunLoop"));
        // 公共模式：窗口拖动缩放、菜单跟踪等模态循环中也能收到节拍
        var mode = CG.GetConstant(foundation, "NSRunLoopCommonModes");
        if (mode == IntPtr.Zero) mode = CG.GetConstant(foundation, "NSDefaultRunLoopMode");
        Send(link, Sel("addToRunLoop:forMode:"), runLoop, mode);
        return new CADisplayLinkClock(link);
    }

    protected override void OnStart() { if (_link != IntPtr.Zero) SendBool(_link, Sel("setPaused:"), 0); }
    protected override void OnStop() { if (_link != IntPtr.Zero) SendBool(_link, Sel("setPaused:"), 1); }

    public override bool ReadTick(IntPtr arg, out double targetTime, out double interval)
    {
        targetTime = interval = 0;
        if (arg == IntPtr.Zero || arg != _link || !Running) return false;
        double timestamp = SendDouble(arg, Sel("timestamp"));
        targetTime = SendDouble(arg, Sel("targetTimestamp"));
        interval = targetTime - timestamp;
        if (!(interval > 0)) interval = SendDouble(arg, Sel("duration"));
        return true;
    }

    public override void Dispose()
    {
        if (_link == IntPtr.Zero) return;
        Send(_link, Sel("invalidate"));
        Release(_link);
        _link = IntPtr.Zero;
    }
}

/// <summary>
/// 旧系统：CVDisplayLink。输出回调在 CoreVideo 的后台线程上，只记录时间戳并经
/// performSelectorOnMainThread 把 <c>awTick:</c> 投递给视图；未处理完的节拍会合并，主线程忙时不会堆积。
/// </summary>
internal sealed class CVDisplayLinkClock : MacFrameClock
{
    // userInfo → 实例。后台线程查表并在锁内投递，Dispose 在锁内摘除，保证不会对已释放的实例投递
    static readonly Dictionary<IntPtr, CVDisplayLinkClock> s_live = new();
    static long s_nextId;

    readonly IntPtr _view;
    readonly IntPtr _id;
    IntPtr _link;
    long _targetBits, _intervalBits;
    int _posted;

    CVDisplayLinkClock(IntPtr view, IntPtr link, IntPtr id) { _view = view; _link = link; _id = id; }

    public override MacFrameClockKind Kind => MacFrameClockKind.CVDisplayLink;

    public static unsafe CVDisplayLinkClock? TryCreate(IntPtr view)
    {
        IntPtr link;
        if (CV.CVDisplayLinkCreateWithActiveCGDisplays(&link) != 0 || link == IntPtr.Zero) return null;
        Callbacks.OnDisplayLinkOutput ??= Output;
        var id = (IntPtr)Interlocked.Increment(ref s_nextId);
        var clock = new CVDisplayLinkClock(view, link, id);
        lock (s_live) s_live[id] = clock;
        if (CV.CVDisplayLinkSetOutputCallback(link, Callbacks.DisplayLinkOutput, id) != 0)
        {
            clock.Dispose();
            return null;
        }
        return clock;
    }

    /// <summary>CoreVideo 后台线程。</summary>
    static unsafe void Output(IntPtr userInfo, IntPtr outputTime)
    {
        lock (s_live)
        {
            if (!s_live.TryGetValue(userInfo, out var clock)) return;
            var ts = (CVTimeStampHead*)outputTime;
            double target = HostTime.ToSeconds(ts->HostTime);
            double interval = ts->VideoTimeScale > 0 ? (double)ts->VideoRefreshPeriod / ts->VideoTimeScale : 0;
            Volatile.Write(ref clock._targetBits, BitConverter.DoubleToInt64Bits(target));
            Volatile.Write(ref clock._intervalBits, BitConverter.DoubleToInt64Bits(interval));
            if (Interlocked.Exchange(ref clock._posted, 1) != 0) return; // 上一拍还没被主线程处理，合并
            var pool = objc_autoreleasePoolPush();
            Send(clock._view, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"), Sel(TickSelector), IntPtr.Zero, 0);
            objc_autoreleasePoolPop(pool);
        }
    }

    protected override void OnStart()
    {
        if (_link == IntPtr.Zero) return;
        Volatile.Write(ref _posted, 0);
        CV.CVDisplayLinkStart(_link);
    }

    protected override void OnStop()
    {
        if (_link != IntPtr.Zero) CV.CVDisplayLinkStop(_link);
    }

    public override void ScreenChanged(IntPtr window)
    {
        if (_link == IntPtr.Zero || window == IntPtr.Zero) return;
        var screen = Send(window, Sel("screen"));
        if (screen == IntPtr.Zero) return;
        var key = NewString("NSScreenNumber");
        var number = Send(Send(screen, Sel("deviceDescription")), Sel("objectForKey:"), key);
        Release(key);
        if (number == IntPtr.Zero) return;
        uint displayId = (uint)SendULong(number, Sel("unsignedLongLongValue"));
        CV.CVDisplayLinkSetCurrentCGDisplay(_link, displayId);
    }

    public override bool ReadTick(IntPtr arg, out double targetTime, out double interval)
    {
        Interlocked.Exchange(ref _posted, 0);
        targetTime = BitConverter.Int64BitsToDouble(Volatile.Read(ref _targetBits));
        interval = BitConverter.Int64BitsToDouble(Volatile.Read(ref _intervalBits));
        return Running && _link != IntPtr.Zero;
    }

    public override void Dispose()
    {
        lock (s_live) s_live.Remove(_id);
        if (_link == IntPtr.Zero) return;
        CV.CVDisplayLinkStop(_link); // 等待进行中的回调结束
        CV.CVDisplayLinkRelease(_link);
        _link = IntPtr.Zero;
    }
}
