using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Swaps content by identity and animates the new content into place.</summary>
/// <remarks>Initial content is shown immediately. The outgoing content is removed when it is replaced, and does not remain interactive during the entrance.</remarks>
public sealed partial class TransitioningContentControl : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-content-transition-defaults {
          @keyframes aw-content-enter {
            from { opacity: .75; transform: translateY(4px); }
          }
          :where(.aw-transitioning-content > [part="content"][data-entering]) {
            animation: aw-content-enter var(--aw-content-transition-duration, 150ms) ease-out;
          }
          @media (prefers-reduced-motion: reduce) {
            :where(.aw-transitioning-content > [part="content"][data-entering]) { animation: none; }
          }
        }
        """;

    readonly Ref<Element> _content = new();
    static readonly FocusOptions s_focus = new() { PreventScroll = true };
    object? _lastKey;
    bool _initialized;
    bool _animateEntry;
    object _instanceKey = new();

    /// <summary>
    /// Changes to this key replace the current content. If omitted, data content is compared by equality,
    /// node content by its <see cref="Node.Key"/>, and unkeyed nodes or <see cref="ContentControl.Children"/> keep their identity.
    /// </summary>
    public object? ContentKey { get; set; }

    public bool IsTransitionEnabled { get; set; } = true;

    protected override Node BuildTemplate()
    {
        var key = ContentKey ?? (Content is Node node ? node.Key : Content);
        var root = Root;
        if (!_initialized || !Equals(key, _lastKey))
        {
            if (_initialized && root?.FocusManager.FocusedElement is { } focused && _content.Current?.IsInclusiveAncestorOf(focused) == true)
                root.RequestAfterLayout(() => RestoreFocus(root));
            _animateEntry = _initialized && IsTransitionEnabled;
            _instanceKey = new object();
            _lastKey = key;
            _initialized = true;
        }
        if (!IsTransitionEnabled) _animateEntry = false;

        var content = new Div { Key = _instanceKey, Ref = _content, TabIndex = -1, Children = BuildContent() };
        content.Attributes["part"] = "content";
        content.Attributes["data-entering"] = _animateEntry ? "" : null;
        return Apply(new Div { Children = [content] }, "aw-transitioning-content");
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
    }
}
