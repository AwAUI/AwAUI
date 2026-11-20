using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>How the expanded pane and the collapsed rail use the available width.</summary>
public enum SplitViewDisplayMode : byte
{
    Inline,
    Overlay,
    CompactInline,
    CompactOverlay,
}

/// <summary>The physical edge at which the pane appears.</summary>
public enum SplitViewPanePlacement : byte
{
    Left,
    Right,
}

/// <summary>A primary content area with an inline or overlaid side pane.</summary>
/// <remarks>
/// Compact modes reserve room for <see cref="CompactPane"/> while closed. The expanded and compact
/// slots have separate lifetimes: the inactive slot is unmounted, including any portals it owns.
/// The overlay is local to the control and does not trap focus. Give the main content or the
/// SplitView itself a height when the overlay needs more space than the main content provides.
/// There is no default motion.
/// </remarks>
public sealed partial class SplitView : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-split-view-defaults {
          :where(.aw-split-view) {
            display: flex;
            flex-direction: column;
            min-width: 0;
            min-height: 0;
          }
          :where(.aw-split-view > [part="trigger"]) { align-self: flex-start; flex-shrink: 0; }
          :where(.aw-split-view > [part="body"]) {
            display: flex;
            flex: 1 1 auto;
            min-width: 0;
            min-height: 0;
            position: relative;
            z-index: 0;
          }
          :where(.aw-split-view[data-pane-placement="right"] > [part="body"]) { flex-direction: row-reverse; }
          :where(.aw-split-view > [part="body"] > [part="pane"],
                 .aw-split-view > [part="body"] > [part="pane-track"]) {
            flex: 0 1 auto;
            min-width: 0;
            min-height: 0;
            max-width: 100%;
            box-sizing: border-box;
          }
          :where(.aw-split-view > [part="body"] > [part="pane"]) { position: relative; overflow: auto; }
          :where(.aw-split-view > [part="body"] > [part="pane"][data-overlay]) {
            position: absolute;
            top: 0;
            bottom: 0;
            left: 0;
            z-index: 1;
          }
          :where(.aw-split-view[data-pane-placement="right"] > [part="body"] > [part="pane"][data-overlay]) {
            left: auto;
            right: 0;
          }
          :where(.aw-split-view > [part="body"] > [part="content"]) {
            flex: 1 1 0%;
            min-width: 0;
            min-height: 0;
            position: relative;
            z-index: 0;
          }
        }
        """;

    readonly ControllableState<bool> _open = new();
    readonly Ref<Element> _trigger = new();
    readonly Ref<Element> _pane = new();
    readonly Ref<Element> _content = new();
    readonly string _paneId = PrimitiveIds.Next("split-view-pane");
    static readonly FocusOptions s_focusOptions = new() { PreventScroll = true };
    Element? _focusBeforeOverlay;

    public SplitViewDisplayMode DisplayMode { get; set; } = SplitViewDisplayMode.Inline;
    public SplitViewPanePlacement PanePlacement { get; set; } = SplitViewPanePlacement.Left;

    /// <summary>Expanded pane width in logical pixels.</summary>
    public double OpenPaneLength { get; set; } = 320;

    /// <summary>Collapsed rail width in logical pixels, capped at <see cref="OpenPaneLength"/>.</summary>
    public double CompactPaneLength { get; set; } = 48;

    public NodeList? Pane { get; set; }
    public NodeList? CompactPane { get; set; }
    public string? PaneAriaLabel { get; set; }

    /// <summary>Text or noninteractive content for the optional toggle above the pane and main content.</summary>
    public object? Trigger { get; set; }
    public string? TriggerAriaLabel { get; set; }

    /// <summary>Set to control the open state, or leave null for an internally managed pane.</summary>
    public bool? IsPaneOpen { get; set; }
    public bool DefaultIsPaneOpen { get; set; }
    public Action<bool>? IsPaneOpenChanged { get; set; }

    /// <summary>Dismiss the overlay on a primary pointer press outside it or when keyboard focus moves out of it.</summary>
    public bool IsLightDismissEnabled { get; set; } = true;

    protected override Node BuildTemplate()
    {
        if (!(OpenPaneLength > 0 && OpenPaneLength <= float.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(OpenPaneLength));
        if (!(CompactPaneLength >= 0 && CompactPaneLength <= float.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(CompactPaneLength));

        bool open = _open.Sync(IsPaneOpen, DefaultIsPaneOpen, IsPaneOpenChanged);
        bool compact = DisplayMode is SplitViewDisplayMode.CompactInline or SplitViewDisplayMode.CompactOverlay;
        bool overlayOpen = open && DisplayMode is (SplitViewDisplayMode.Overlay or SplitViewDisplayMode.CompactOverlay);
        double compactLength = compact ? Math.Min(CompactPaneLength, OpenPaneLength) : 0;
        bool paneVisible = open || compactLength > 0;
        string state = PartProps.OpenState(open);

        SchedulePaneFocus(open, overlayOpen, paneVisible);

        var pane = new Div
        {
            Ref = _pane,
            Id = _paneId,
            Role = Role.Group,
            AriaLabel = PaneAriaLabel,
            TabIndex = overlayOpen ? -1 : null,
            Style = paneVisible ? Width(open ? OpenPaneLength : compactLength) : "display:none;",
        };
        pane.Attributes["part"] = "pane";
        pane.Attributes["data-state"] = state;
        pane.Attributes["data-overlay"] = overlayOpen ? "true" : null;
        pane.Attributes["aria-hidden"] = paneVisible ? null : "true";
        if (paneVisible && (open ? Pane : CompactPane) is { } nodes)
            pane.Children.Add(new Fragment(nodes) { Key = state });

        var layer = new DismissableLayer
        {
            Active = overlayOpen,
            OnPointerDownOutside = HandlePointerDownOutside,
            OnFocusOutside = HandleFocusOutside,
            OnDismiss = _ => _open.Set(false),
            Children = [pane],
        };
        var track = new Div { Style = overlayOpen && compactLength > 0 ? Width(compactLength) : "display:none;" };
        track.Attributes["part"] = "pane-track";
        track.Attributes["aria-hidden"] = "true";

        var content = new Div { Ref = _content, TabIndex = -1, Children = BuildContent() };
        content.Attributes["part"] = "content";
        var body = new Div { Key = "body", Children = [layer, track, content] };
        body.Attributes["part"] = "body";

        var box = new Div();
        box.Attributes["data-state"] = state;
        box.Attributes["data-display-mode"] = DisplayMode switch
        {
            SplitViewDisplayMode.Overlay => "overlay",
            SplitViewDisplayMode.CompactInline => "compact-inline",
            SplitViewDisplayMode.CompactOverlay => "compact-overlay",
            _ => "inline",
        };
        box.Attributes["data-pane-placement"] = PanePlacement == SplitViewPanePlacement.Right ? "right" : "left";
        if (Trigger is { } label)
        {
            var trigger = new UIButton
            {
                Key = "trigger",
                Class = "aw-button",
                Ref = _trigger,
                AriaLabel = TriggerAriaLabel,
                Children = [Present(label, null)],
            };
            trigger.Attributes["part"] = "trigger";
            trigger.Attributes["aria-expanded"] = PartProps.Bool(open);
            trigger.Attributes["aria-controls"] = _paneId;
            trigger.Attributes["data-state"] = state;
            ControlParts.OnActivate(trigger, () => _open.Set(!_open.Value));
            box.Children.Add(trigger);
        }
        box.Children.Add(body);
        return Apply(box, "aw-split-view");
    }

    void HandlePointerDownOutside(DismissableLayerEvent<Element> e)
    {
        // The trigger handles its own click. Dismissing on its pointer-down would reopen on click.
        if (!IsLightDismissEnabled || IsTrigger(e.Target) || IsInPane(e.Target)
            || e.OriginalEvent is not PointerEvent { Button: PointerButtons.Left } || PartProps.IsRightClick(e))
            e.PreventDefault();
    }

    void HandleFocusOutside(DismissableLayerEvent<Element> e)
    {
        // A pointer press already made its dismissal decision. Keep a newly focused outside control focused.
        if (!IsLightDismissEnabled || IsTrigger(e.Target) || IsInPane(e.Target)
            || e.OriginalEvent is not FocusChangedEventArgs { Origin: FocusOrigin.Keyboard } focus
            || !(IsInPane(focus.OldFocus) || IsTrigger(focus.OldFocus)))
            e.PreventDefault();
    }

    void SchedulePaneFocus(bool open, bool overlayOpen, bool paneVisible)
    {
        var before = Root?.FocusManager.FocusedElement;
        bool wasInside = IsInPane(before);
        UseEffect(() =>
        {
            var previous = _focusBeforeOverlay;
            _focusBeforeOverlay = overlayOpen && !wasInside ? before : null;
            if ((!overlayOpen && !wasInside) || Root is not { } root) return null;

            // Only resolved, current geometry can receive focus. A subsequent mode change or unmount cancels this work.
            bool current = true;
            root.RequestAfterLayout(() =>
            {
                if (!current) return;
                var focused = root.FocusManager.FocusedElement;
                if (overlayOpen)
                {
                    if (focused is not null && (IsInPane(focused) || !ReferenceEquals(focused, before))) return;
                    if (_pane.Current is not { IsVisible: true } livePane || root.IsPointerBlocked(livePane)) return;
                    foreach (var candidate in root.FocusManager.GetTabOrder(livePane))
                        if (TryFocus(root, candidate)) return;
                    TryFocus(root, livePane);
                }
                else if (focused is null || (!open && ReferenceEquals(focused, before) && wasInside))
                {
                    if (!TryFocus(root, previous) && !TryFocus(root, _trigger.Current)) TryFocus(root, _content.Current);
                }
            });
            return () => current = false;
        }, open, overlayOpen, paneVisible);
    }

    bool IsTrigger(Element? element) => element is not null && _trigger.Current?.IsInclusiveAncestorOf(element) == true;

    bool IsInPane(Element? element)
    {
        if (element is null || _pane.Current is not { Root: { } root } pane) return false;
        if (pane.IsInclusiveAncestorOf(element)) return true;
        // The pane's layer owns its portalled descendants even when they are outside its element tree.
        for (var owner = root.Overlay.LayerOf(element)?.Owner; owner is not null; owner = owner.ParentComponent)
            if (ReferenceEquals(owner, pane.Owner)) return true;
        return false;
    }

    static bool TryFocus(UIRoot root, Element? element) => element is { IsVisible: true }
        && ReferenceEquals(element.Root, root) && !root.IsPointerBlocked(element) && element.Focus(s_focusOptions);

    static string Width(double value) => FormattableString.Invariant($"width:{value}px;");

    protected override void OnUnmount() => _focusBeforeOverlay = null;
}
