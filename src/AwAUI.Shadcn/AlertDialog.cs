using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using DialogPrimitive = AwAUI.Controls.Primitives.Dialog;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A modal confirmation dialog. Use an <see cref="AlertDialogCancel"/> for the safe default action.</summary>
public sealed partial class AlertDialog : Component
{
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new DialogPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        Modal = true,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    };
}

public sealed partial class AlertDialogTrigger : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Trigger
    {
        AsChild = AsChild,
        Children = Children,
        Attributes = DialogAttributes.ForPart("alert-dialog-trigger", Rest, Class),
    };
}

public sealed partial class AlertDialogPortal : Component
{
    public int ZOrder { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new DialogPortal { ZOrder = ZOrder, Children = Children };
}

public sealed partial class AlertDialogOverlay : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Overlay
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("alert-dialog-overlay", Rest, Class, SheetOverlay.OverlayClass),
    };
}

public enum AlertDialogSize { Default, Sm }

public sealed partial class AlertDialogContent : Component
{
    const string ContentClass = "group/alert-dialog-content fixed top-[50%] left-[50%] z-50 grid w-full max-w-[calc(100%-2rem)] translate-x-[-50%] translate-y-[-50%] gap-4 rounded-lg border bg-background p-6 shadow-lg animation-duration-200 outline-none data-[size=sm]:max-w-xs data-[state=closed]:animate-aw-dialog-panel-out data-[state=open]:animate-aw-dialog-panel-in data-[size=default]:sm:max-w-lg motion-reduce:animate-none!";
    internal const string CancelMarker = "data-aw-alert-dialog-cancel";

    public AlertDialogSize Size { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var context = Use<DialogPrimitive.Context>();
        var attributes = DialogAttributes.ForPart("alert-dialog-content", Rest, Class, ContentClass);
        attributes["data-size"] = Size == AlertDialogSize.Sm ? "sm" : "default";
        var panel = new AlertDialogPanel
        {
            Children =
            [
                new DialogPrimitive.Content
                {
                    Attributes = attributes,
                    Children = Children,
                    OnOpenAutoFocus = FocusCancel,
                    OnCloseAutoFocus = OnCloseAutoFocus,
                    OnEscapeKeyDown = OnEscapeKeyDown,
                    OnInteractOutside = static e => e.PreventDefault(),
                },
            ],
        };
        if (TryUse<DialogPortal.Placement>(out var placement) && ReferenceEquals(placement.Owner, context.Shared))
            return panel;
        return new DialogPrimitive.Portal { Children = [new AlertDialogOverlay(), panel] };
    }

    void FocusCancel(FocusScopeAutoFocusEvent e)
    {
        OnOpenAutoFocus?.Invoke(e);
        if (e.DefaultPrevented) return;
        e.PreventDefault();
        if (e.Container.Root is { } root)
            foreach (var candidate in root.FocusManager.GetTabOrder(e.Container))
                if (candidate.GetAttribute(CancelMarker) is not null && candidate.Focus()) return;
        e.Container.Focus(new FocusOptions { PreventScroll = true });
    }
}

// Dialog.Content owns the live panel and its focus behavior. It currently fixes Element.Role to
// Dialog, so publish the more specific role through the live attribute override API.
internal sealed partial class AlertDialogPanel : Component
{
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        bool open = Use<DialogPrimitive.Context>().Open;
        UseEffect(() =>
        {
            foreach (var element in GetRootElements())
                if (!element.IsTextNode) element.SetAttributeOverride("role", "alertdialog");
            return null;
        }, open);
        return new Fragment(Children ?? new NodeList());
    }
}

public sealed partial class AlertDialogHeader : Component
{
    const string HeaderClass = "grid grid-rows-[auto_1fr] place-items-center gap-1.5 text-center has-data-[slot=alert-dialog-media]:grid-rows-[auto_auto_1fr] has-data-[slot=alert-dialog-media]:gap-x-6 sm:group-data-[size=default]/alert-dialog-content:place-items-start sm:group-data-[size=default]/alert-dialog-content:text-left sm:group-data-[size=default]/alert-dialog-content:has-data-[slot=alert-dialog-media]:grid-rows-[auto_1fr]";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("alert-dialog-header", Rest, Class, HeaderClass));
        return node;
    }
}

public sealed partial class AlertDialogFooter : Component
{
    const string FooterClass = "flex flex-col-reverse gap-2 group-data-[size=sm]/alert-dialog-content:grid group-data-[size=sm]/alert-dialog-content:grid-cols-2 sm:flex-row sm:justify-end";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("alert-dialog-footer", Rest, Class, FooterClass));
        return node;
    }
}

public sealed partial class AlertDialogTitle : Component
{
    const string TitleClass = "text-lg font-semibold sm:group-data-[size=default]/alert-dialog-content:group-has-data-[slot=alert-dialog-media]/alert-dialog-content:col-start-2";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Title
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("alert-dialog-title", Rest, Class, TitleClass),
    };
}

public sealed partial class AlertDialogDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Description
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("alert-dialog-description", Rest, Class, "text-sm text-muted-foreground"),
    };
}

public sealed partial class AlertDialogMedia : Component
{
    const string MediaClass = "mb-2 inline-flex size-16 items-center justify-center rounded-md bg-muted sm:group-data-[size=default]/alert-dialog-content:row-span-2 *:[svg:not([class*='size-'])]:size-8";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("alert-dialog-media", Rest, Class, MediaClass));
        return node;
    }
}

public sealed partial class AlertDialogAction : Component
{
    public ButtonVariant Variant { get; set; }
    public ButtonSize Size { get; set; }
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => AlertDialogButton.Build("alert-dialog-action", "Continue", false,
        Variant, Size, Disabled, OnClick, Class, Rest, Children);
}

public sealed partial class AlertDialogCancel : Component
{
    public ButtonVariant Variant { get; set; } = ButtonVariant.Outline;
    public ButtonSize Size { get; set; }
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => AlertDialogButton.Build("alert-dialog-cancel", "Cancel", true,
        Variant, Size, Disabled, OnClick, Class, Rest, Children);
}

internal static class AlertDialogButton
{
    internal static Node Build(string slot, string label, bool cancel, ButtonVariant variant, ButtonSize size,
        bool disabled, Action<PointerEvent>? onClick, string? classes, AttributeDictionary? rest, NodeList? children)
    {
        var attributes = DialogAttributes.ForPart(slot, rest, classes);
        if (cancel) attributes[AlertDialogContent.CancelMarker] = "";
        var button = new UIButton { Disabled = disabled, OnClick = onClick, Children = children ?? new NodeList { label } };
        button.MergeAttributes(attributes);

        // The inner button keeps the alert slot. Its click handler runs before the primitive closes.
        return new DialogPrimitive.Close
        {
            AsChild = true,
            Children = [new Button { Variant = variant, Size = size, AsChild = true, Children = [button] }],
        };
    }
}
