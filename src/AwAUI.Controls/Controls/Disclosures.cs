using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>A disclosure with a keyboard accessible header and an inline body.</summary>
/// <remarks>The body children are mounted only while expanded.</remarks>
public sealed partial class Expander : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-disclosure-defaults {
          :where(.aw-expander > [part="header"] > [part="indicator"]) {
            width: 9px;
            height: 6px;
            flex-shrink: 0;
            background-color: currentColor;
            clip-path: polygon(0 0, 50% 70%, 100% 0, 100% 30%, 50% 100%, 0 30%);
            rotate: -90deg;
          }
          :where(.aw-expander > [part="header"][data-state="open"] > [part="indicator"]) {
            rotate: 0deg;
          }
        }
        """;

    readonly ControllableState<bool> _expanded = new();
    readonly Ref<Element> _header = new();
    readonly Ref<Element> _body = new();
    readonly string _bodyId = PrimitiveIds.Next("expander-content");

    public object? Header { get; set; }
    public Func<object, Node>? HeaderTemplate { get; set; }
    public string? HeaderAriaLabel { get; set; }

    /// <summary>Set to control expansion, or leave null to let the expander manage it.</summary>
    public bool? IsExpanded { get; set; }
    public bool DefaultIsExpanded { get; set; }
    public Action<bool>? IsExpandedChanged { get; set; }
    public bool IsEnabled { get; set; } = true;

    protected override Node BuildTemplate()
    {
        bool expanded = _expanded.Sync(IsExpanded, DefaultIsExpanded, IsExpandedChanged);
        string state = PartProps.OpenState(expanded);

        // The body is removed from layout when closed, including any portals its children own.
        // If a binding collapses it while focus is inside, restore focus to the header after removal.
        var bodyElement = _body.Current;
        var root = bodyElement?.Root;
        bool restoreFocus = !expanded && bodyElement is not null && root?.FocusManager.FocusedElement is { } focused
            && FocusIsInside(bodyElement, focused, root);
        UseEffect(() =>
        {
            if (restoreFocus && root is not null)
                root.RequestAfterLayout(() =>
                {
                    if (root.FocusManager.FocusedElement is null && _header.Current is { IsVisible: true } header
                        && ReferenceEquals(header.Root, root) && !root.IsPointerBlocked(header))
                        header.Focus();
                });
            return null;
        }, expanded);

        var indicator = new Div();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        var header = new UIButton
        {
            Class = "aw-button",
            Ref = _header,
            Disabled = !IsEnabled,
            AriaLabel = HeaderAriaLabel,
            Children = [indicator, Header is { } value ? Present(value, HeaderTemplate) : new UIText("Details")],
        };
        header.Attributes["part"] = "header";
        header.Attributes["aria-expanded"] = PartProps.Bool(expanded);
        header.Attributes["aria-controls"] = _bodyId;
        header.Attributes["data-state"] = state;
        ControlParts.OnActivate(header, () => _expanded.Set(!_expanded.Value));

        var body = new Div
        {
            Id = _bodyId,
            Ref = _body,
            Style = expanded ? default : new StyleValue("display: none"),
            Children = expanded ? BuildContent() : [],
        };
        body.Attributes["part"] = "content";
        body.Attributes["data-state"] = state;
        body.Attributes["aria-hidden"] = expanded ? null : "true";
        var box = new Div { Children = [header, body] };
        box.Attributes["data-state"] = state;
        return Apply(box, "aw-expander");
    }

    bool FocusIsInside(Element body, Element focused, UIRoot root)
    {
        if (body.IsInclusiveAncestorOf(focused)) return true;
        // Portal elements live outside the body in the visual tree but still have component owners.
        for (var owner = root.Overlay.LayerOf(focused)?.Owner; owner is not null; owner = owner.ParentComponent)
            if (ReferenceEquals(owner, this)) return true;
        return false;
    }
}

/// <summary>An anchored, nonmodal surface. Escape, outside interaction, or the trigger dismisses it.</summary>
public sealed partial class Flyout : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-disclosure-defaults {
          :where(.aw-flyout) { display: inline-block; }
          :where(.aw-flyout-content) {
            max-width: calc(100vw - 16px);
            max-height: calc(100vh - 16px);
            overflow: auto;
            padding: var(--aw-content-padding, 12px);
            border: 1px solid var(--aw-border, #c8ced6);
            border-radius: var(--aw-radius, 6px);
            color: var(--aw-text, #1f2328);
            background-color: var(--aw-surface, #ffffff);
          }
        }
        """;

    /// <summary>Text or noninteractive content for the built-in trigger button.</summary>
    public object? Trigger { get; set; }
    public string? TriggerAriaLabel { get; set; }
    public string? ContentAriaLabel { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Set to control visibility, or leave null to let the flyout manage it.</summary>
    public bool? IsOpen { get; set; }
    public bool DefaultIsOpen { get; set; }
    public Action<bool>? IsOpenChanged { get; set; }
    public Placement Placement { get; set; } = Placement.BottomStart;
    public float SideOffset { get; set; } = 4;
    public bool ShowCloseButton { get; set; }
    public string CloseButtonText { get; set; } = "Close";

    protected override Node BuildTemplate()
    {
        var trigger = new Popover.Trigger
        {
            Class = "aw-button aw-flyout-trigger",
            Attributes = DisclosureParts.TriggerAttributes(TriggerAriaLabel, IsEnabled),
            Children = [Present(Trigger ?? "Open flyout", null)],
        };
        var children = BuildContent();
        if (ShowCloseButton)
        {
            children = new NodeList(children);
            children.Add(new Popover.Close
            {
                Class = "aw-button aw-flyout-close",
                Attributes = new AttributeDictionary { ["part"] = "close" },
                Children = [CloseButtonText],
            });
        }
        var panel = new Popover.Content
        {
            Class = "aw-flyout-content",
            Attributes = new AttributeDictionary
            {
                ["part"] = "content",
                ["aria-label"] = ContentAriaLabel ?? (Trigger is string { Length: > 0 } label ? label : "Flyout"),
            },
            Side = Placement.Side,
            Align = Placement.Align,
            SideOffset = SideOffset,
            CollisionPadding = 8,
            Children = children,
        };
        return Apply(new Div
        {
            Children = [new Popover.Root
            {
                Open = IsOpen,
                DefaultOpen = DefaultIsOpen,
                OnOpenChange = IsOpenChanged,
                Children = [trigger, new Popover.Portal { Children = [panel] }],
            }],
        }, "aw-flyout");
    }
}

/// <summary>A modal dialog within the current window, with an optional trigger button.</summary>
public sealed partial class DialogHost : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-disclosure-defaults {
          :where(.aw-dialog-host) { display: inline-block; }
          :where(.aw-dialog-overlay) {
            position: fixed;
            inset: 0;
            pointer-events: none;
            background-color: var(--aw-dialog-scrim, #0006);
          }
          :where(.aw-dialog-content) {
            position: fixed;
            top: 50%;
            left: 50%;
            translate: -50% -50%;
            width: 420px;
            max-width: calc(100vw - 32px);
            max-height: calc(100vh - 32px);
            overflow: auto;
            display: flex;
            flex-direction: column;
            align-items: stretch;
            gap: var(--aw-control-gap, 8px);
            padding: var(--aw-content-padding, 12px);
            border: 1px solid var(--aw-border, #c8ced6);
            border-radius: var(--aw-radius, 6px);
            color: var(--aw-text, #1f2328);
            background-color: var(--aw-surface, #ffffff);
          }
          :where(.aw-dialog-title) { font-weight: 600; }
          :where(.aw-dialog-close) { align-self: flex-end; }
        }
        """;

    Element? _focusBeforeOpen;

    /// <summary>Text or noninteractive content for the built-in trigger button. Null omits the trigger.</summary>
    public object? Trigger { get; set; }
    public string? TriggerAriaLabel { get; set; }
    public string Title { get; set; } = "Dialog";
    public string? Description { get; set; }

    /// <summary>Set to control visibility, or leave null to let the dialog manage it.</summary>
    public bool? IsOpen { get; set; }
    public bool DefaultIsOpen { get; set; }
    public Action<bool>? IsOpenChanged { get; set; }

    /// <summary>Whether a pointer press outside the dialog closes it. Defaults to false.</summary>
    public bool IsLightDismissEnabled { get; set; }
    public bool ShowCloseButton { get; set; } = true;
    public string CloseButtonText { get; set; } = "Close";

    protected override Node BuildTemplate()
    {
        var children = new NodeList
        {
            new Dialog.Title
            {
                Class = "aw-dialog-title",
                Attributes = new AttributeDictionary { ["part"] = "title" },
                Children = [Title],
            },
            // Keep the primitive's aria-describedby target valid even when no description is supplied.
            new Dialog.Description
            {
                Class = "aw-dialog-description",
                Style = Description is null ? new StyleValue("display: none") : default,
                Attributes = new AttributeDictionary { ["part"] = "description" },
                Children = Description is { } description ? [description] : [],
            },
        };
        children.AddRange(BuildContent());
        if (ShowCloseButton)
            children.Add(new Dialog.Close
            {
                Class = "aw-button aw-dialog-close",
                Attributes = new AttributeDictionary { ["part"] = "close" },
                Children = [CloseButtonText],
            });

        var panel = new Dialog.Content
        {
            Class = "aw-dialog-content",
            Attributes = new AttributeDictionary { ["part"] = "content", ["aria-label"] = Title },
            OnPointerDownOutside = IsLightDismissEnabled ? null : e => e.PreventDefault(),
            OnOpenAutoFocus = RememberFocus,
            OnCloseAutoFocus = RestoreFocus,
            Children = children,
        };
        var overlay = new Dialog.Overlay
        {
            Class = "aw-dialog-overlay",
            Attributes = new AttributeDictionary { ["part"] = "overlay", ["aria-hidden"] = "true" },
        };
        var parts = new NodeList();
        if (Trigger is { } trigger)
            parts.Add(new Dialog.Trigger
            {
                Class = "aw-button aw-dialog-trigger",
                Attributes = DisclosureParts.TriggerAttributes(TriggerAriaLabel, enabled: true),
                Children = [Present(trigger, null)],
            });
        parts.Add(new Dialog.Portal { Key = "portal", Children = [overlay, panel] });

        return Apply(new Div
        {
            Style = Trigger is null ? new StyleValue("display: contents") : default,
            Children = [new Dialog.Root { Open = IsOpen, DefaultOpen = DefaultIsOpen, OnOpenChange = IsOpenChanged, Children = parts }],
        }, "aw-dialog-host");
    }

    void RememberFocus(FocusScopeAutoFocusEvent e) => _focusBeforeOpen = e.Container.Root?.FocusManager.FocusedElement;

    void RestoreFocus(FocusScopeAutoFocusEvent e)
    {
        // Preserve the primitive's trigger fallback. This also restores focus if a binding opened
        // the dialog without a trigger or while a different control was focused.
        var previous = _focusBeforeOpen;
        _focusBeforeOpen = null;
        if (previous is not { Root: { } root }) return;
        if (root.FocusManager.FocusedElement is not null
            || (previous.IsVisible && !root.IsPointerBlocked(previous) && previous.Focus()))
            e.PreventDefault();
    }
}

internal static class DisclosureParts
{
    internal static AttributeDictionary TriggerAttributes(string? label, bool enabled) => new()
    {
        ["part"] = "trigger",
        ["aria-label"] = label,
        ["disabled"] = !enabled,
    };
}
