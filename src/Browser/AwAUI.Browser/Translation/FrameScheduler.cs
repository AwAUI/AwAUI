using System;

namespace AwAUI.Browser;

/// <summary>
/// requestAnimationFrame 帧节拍的状态机（design/04「帧节拍来自平台」、design/18 帧调度）。
/// <para>
/// 只在有帧请求、且 canvas 可见（页面未隐藏、canvas 与视口相交）时才挂一个 rAF 回调；回调到达时若需求已消失
/// 就不再续挂，空闲页面不占 CPU。每次最多挂一个回调；在 OnFrame 内再次请求（动画）会挂下一帧的回调。
/// </para>
/// 方法的返回值告诉调用方要不要真正调用 JS 的 requestAnimationFrame / cancelAnimationFrame，
/// 这样状态机本身不依赖 JS，可以在普通 .NET 上测试。
/// </summary>
internal sealed class FrameScheduler
{
    double _lastFrameMs = double.NegativeInfinity;

    /// <summary>框架请求了下一帧、尚未交付。</summary>
    public bool FrameRequested { get; private set; }

    /// <summary>已挂上一个 rAF 回调、尚未到达。</summary>
    public bool CallbackPending { get; private set; }

    /// <summary>页面可见且 canvas 与视口相交。</summary>
    public bool Visible { get; private set; } = true;

    public bool Closed { get; private set; }

    /// <summary>累计到达的 rAF 回调数与实际交付的帧数（诊断用：空闲时两者都不再增长）。</summary>
    public long CallbacksReceived { get; private set; }
    public long FramesDelivered { get; private set; }

    /// <summary>请求一帧。返回 true 表示调用方需要挂一个 rAF 回调。</summary>
    public bool Request()
    {
        FrameRequested = true;
        return TrySchedule();
    }

    /// <summary>rAF 回调到达。返回 true 表示应交付 <c>OnFrame(timestamp)</c>；时间戳保证单调不减。</summary>
    public bool OnAnimationFrame(double timeMs, out TimeSpan timestamp)
    {
        CallbacksReceived++;
        CallbackPending = false;
        timestamp = TimeSpan.Zero;
        if (Closed || !Visible || !FrameRequested) return false;
        FrameRequested = false; // 先清除：OnFrame 内的请求挂到下一帧
        if (!DomProtocol.IsFinite(timeMs)) timeMs = _lastFrameMs > 0 ? _lastFrameMs : 0;
        if (timeMs < _lastFrameMs) timeMs = _lastFrameMs;
        _lastFrameMs = timeMs;
        timestamp = DomProtocol.ToTimeSpan(timeMs);
        FramesDelivered++;
        return true;
    }

    /// <summary>
    /// 可见性变化。返回 <see cref="VisibilityAction"/>：隐藏时取消已挂的回调，重新可见且仍有需求时挂回调。
    /// </summary>
    public VisibilityAction SetVisible(bool visible)
    {
        if (visible == Visible) return VisibilityAction.None;
        Visible = visible;
        if (!visible)
        {
            if (!CallbackPending) return VisibilityAction.None;
            CallbackPending = false;
            return VisibilityAction.Cancel;
        }
        return TrySchedule() ? VisibilityAction.Schedule : VisibilityAction.None;
    }

    /// <summary>窗口关闭。返回 true 表示需要取消已挂的回调。</summary>
    public bool Close()
    {
        Closed = true;
        FrameRequested = false;
        bool pending = CallbackPending;
        CallbackPending = false;
        return pending;
    }

    bool TrySchedule()
    {
        if (Closed || !Visible || CallbackPending || !FrameRequested) return false;
        CallbackPending = true;
        return true;
    }
}

internal enum VisibilityAction : byte { None, Schedule, Cancel }
