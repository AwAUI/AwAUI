using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.UI;
using DialogPrimitive = AwAUI.Controls.Primitives.Dialog;

namespace AwAUI.Shadcn;

/// <summary>
/// The dialog panel. It supplies a portal and modal overlay unless it is already placed inside
/// a <see cref="DialogPortal"/> for the same dialog.
/// </summary>
public sealed partial class DialogContent : Component
{
    // Dialog motion uses only its authored opacity and scale tracks. The generic Tailwind keyframes
    // also set filter: blur(0), which still allocates an offscreen filter layer.
    const string ContentClass = "fixed top-[50%] left-[50%] z-50 grid w-full max-w-[calc(100%-2rem)] translate-x-[-50%] translate-y-[-50%] gap-4 rounded-lg border bg-background p-6 shadow-lg animation-duration-200 outline-none data-[state=closed]:animate-aw-dialog-panel-out data-[state=open]:animate-aw-dialog-panel-in sm:max-w-lg";
    const string CloseClass = "absolute top-4 right-4 rounded-xs opacity-70 ring-offset-background transition-opacity hover:opacity-100 focus:ring-2 focus:ring-ring focus:ring-offset-2 focus:outline-hidden disabled:pointer-events-none data-[state=open]:bg-accent data-[state=open]:text-muted-foreground";

    public bool ShowCloseButton { get; set; } = true;

    public string? Class { get; set; }

    public NodeList? Children { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Rest { get; set; }

    public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }

    public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }

    public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }

    protected override Node Build()
    {
        var children = Children;
        if (ShowCloseButton)
        {
            children = new NodeList(Children);
            children.Add(CreateCloseButton());
        }

        var content = new DialogPrimitive.Content
        {
            Attributes = DialogAttributes.ForPart("dialog-content", Rest, Class, ContentClass),
            Children = children,
            OnOpenAutoFocus = OnOpenAutoFocus,
            OnCloseAutoFocus = OnCloseAutoFocus,
            OnEscapeKeyDown = OnEscapeKeyDown,
            OnPointerDownOutside = OnPointerDownOutside,
            OnFocusOutside = OnFocusOutside,
            OnInteractOutside = OnInteractOutside,
        };

        if (TryUse<DialogPortal.Placement>(out var placement)
            && ReferenceEquals(placement.Owner, Use<DialogPrimitive.Context>().Shared))
            return content;

        return new DialogPrimitive.Portal { Children = [new DialogOverlay(), content] };
    }

    static DialogClose CreateCloseButton()
    {
        var glyph = SelectParts.Glyph(LucideIcons.X, "dialog-close-icon", "pointer-events-none size-4");
        return new DialogClose
        {
            Class = CloseClass,
            Rest = new AttributeDictionary { ["aria-label"] = "Close" },
            Children = [glyph],
        };
    }
}
