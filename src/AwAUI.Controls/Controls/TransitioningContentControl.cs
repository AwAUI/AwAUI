using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Swaps content by identity and animates the new content into place.</summary>
/// <remarks>
/// Initial content is shown immediately. When the key changes, the previous content stays mounted (inert and non-interactive)
/// underneath the new content until <see cref="TransitionDuration"/> elapses, then it is removed. Without a clock the previous
/// content is removed immediately. Focus inside the outgoing content moves to the new content as soon as it is laid out.
/// </remarks>
public sealed partial class TransitioningContentControl : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-content-transition-defaults {
          @keyframes aw-content-enter {
            from { opacity: .75; transform: translateY(4px); }
          }
          @keyframes aw-content-exit {
            from { opacity: 1; }
          }
          :where(.aw-transitioning-content) {
            position: relative;
          }
          :where(.aw-transitioning-content > [part="content"][data-entering]) {
            animation: aw-content-enter var(--aw-content-transition-duration, 150ms) ease-out;
          }
          :where(.aw-transitioning-content > [part="content"][data-exiting]) {
            position: absolute;
            inset: 0;
            opacity: 0;
            pointer-events: none;
            animation: aw-content-exit var(--aw-content-transition-duration, 150ms) ease-in;
          }
          @media (prefers-reduced-motion: reduce) {
            :where(.aw-transitioning-content > [part="content"][data-entering]),
            :where(.aw-transitioning-content > [part="content"][data-exiting]) { animation: none; }
          }
        }
        """;

    readonly Ref<Element> _content = new();
    static readonly FocusOptions s_focus = new() { PreventScroll = true };
    object? _lastKey;
    bool _initialized;
    bool _animateEntry;
    object _instanceKey = new();

    // 退出内容：上一次构建的实例键与子节点在键变化时移入这里，定时器到期后重建并移除
    readonly State<int> _exitVersion = new(0);
    Div? _outgoing;
    NodeList? _contentChildren;
    IDisposable? _exitTimer;

    /// <summary>
    /// Changes to this key replace the current content. If omitted, data content is compared by equality,
    /// node content by its <see cref="Node.Key"/>, and unkeyed nodes or <see cref="ContentControl.Children"/> keep their identity.
    /// </summary>
    public object? ContentKey { get; set; }

    public bool IsTransitionEnabled { get; set; } = true;

    /// <summary>退出与进入动画的时长（默认 150 ms）；<see cref="TimeSpan.Zero"/> 或负值关闭退出阶段。</summary>
    public TimeSpan TransitionDuration { get; set; } = TimeSpan.FromMilliseconds(150);

    protected override Node BuildTemplate()
    {
        _ = _exitVersion.Value;
        var key = ContentKey ?? (Content is Node node ? node.Key : Content);
        var root = Root;
        if (!_initialized || !Equals(key, _lastKey))
        {
            if (_initialized && root?.FocusManager.FocusedElement is { } focused && _content.Current?.IsInclusiveAncestorOf(focused) == true)
                root.RequestAfterLayout(() => RestoreFocus(root));
            _animateEntry = _initialized && IsTransitionEnabled;
            if (_initialized && IsTransitionEnabled && TransitionDuration > TimeSpan.Zero && root?.Clock is not null
                && _contentChildren is { } previousChildren)
                BeginExit(previousChildren);
            else
                FinishExit();
            _instanceKey = new object();
            _lastKey = key;
            _initialized = true;
        }
        if (!IsTransitionEnabled) _animateEntry = false;

        var duration = $"--aw-content-transition-duration: {TransitionDuration.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}ms";
        var content = new Div { Key = _instanceKey, Ref = _content, TabIndex = -1, Children = BuildContent(), Style = duration };
        content.Attributes["part"] = "content";
        content.Attributes["data-entering"] = _animateEntry ? "" : null;
        _animateEntry = false;
        _contentChildren = content.Children;

        var children = new NodeList();
        if (_outgoing is { } outgoing)
        {
            outgoing.Style = duration;
            children.Add(outgoing);
        }
        children.Add(content);
        return Apply(new Div { Children = children }, "aw-transitioning-content");
    }

    /// <summary>
    /// 把当前内容作为退出层继续挂载：复用实例键与子节点，元素不会卸载（输入 / 滚动状态仍在），
    /// 只加上 <c>data-exiting</c>；<c>aria-hidden</c>、<c>inert</c> 与 <c>pointer-events</c> 让它不可交互。
    /// </summary>
    void BeginExit(NodeList previousChildren)
    {
        _exitTimer?.Dispose();
        _exitTimer = null;
        var outgoing = new Div { Key = _instanceKey, TabIndex = -1, Children = previousChildren };
        outgoing.Attributes["part"] = "content";
        outgoing.Attributes["data-exiting"] = "";
        outgoing.Attributes["aria-hidden"] = "true";
        outgoing.Attributes["inert"] = "";
        _outgoing = outgoing;
        _exitTimer = Root!.Clock!.Schedule(TransitionDuration, EndExit);
    }

    void EndExit()
    {
        _exitTimer = null;
        FinishExit();
        // 触发重建，把退出层从树里移除
        _exitVersion.Value++;
    }

    void FinishExit()
    {
        _exitTimer?.Dispose();
        _exitTimer = null;
        _outgoing = null;
    }

    void RestoreFocus(UIRoot root)
    {
        if (root.FocusManager.FocusedElement is null && _content.Current is { IsVisible: true } next
            && ReferenceEquals(next.Root, root) && !root.IsPointerBlocked(next))
            next.Focus(s_focus);
    }

    protected override void OnUnmount()
    {
        _initialized = false;
        _lastKey = null;
        _animateEntry = false;
        FinishExit();
    }
}
