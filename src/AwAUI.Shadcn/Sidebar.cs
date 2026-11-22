using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Platform;
using AwAUI.UI;
using PrimSide = AwAUI.Controls.Primitives.Side;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

public enum SidebarSide { Left, Right }

/// <summary>How the sidebar collapses: slide fully away, shrink to icons, or never collapse.</summary>
public enum SidebarCollapsible { Offcanvas, Icon, None }

public enum SidebarVariant { Sidebar, Floating, Inset }

public enum SidebarMenuButtonVariant { Default, Outline }

public enum SidebarMenuButtonSize { Default, Sm, Lg }

/// <summary>
/// Owns the expanded / collapsed state shared by every sidebar part. Ctrl/Cmd+B toggles it, and <see cref="Persist"/>
/// is called with the new value so the application can store it (for example in a cookie named "sidebar_state").
/// The sidebar switches to a mobile sheet when the mounted viewport is narrower than <see cref="MobileBreakpoint"/>
/// (unless <see cref="AutoDetectMobile"/> is off); <see cref="IsMobile"/> forces mobile independently of the viewport.
/// </summary>
public sealed partial class SidebarProvider : Component
{
    readonly ControllableState<bool> _open = new();
    readonly ControllableState<bool> _openMobile = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; } = true;
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }

    public bool? OpenMobile { get; set; }
    public bool DefaultOpenMobile { get; set; }
    public Action<bool>? OpenMobileChanged { get; set; }
    public Action<bool>? OnOpenMobileChange { get; set; }

    /// <summary>Force the mobile sheet regardless of the viewport width (an explicit override).</summary>
    public bool IsMobile { get; set; }

    /// <summary>Use the mounted viewport width to decide whether the sidebar is mobile. Defaults to true.</summary>
    public bool AutoDetectMobile { get; set; } = true;

    /// <summary>Viewport width (logical pixels) below which the sidebar is mobile when <see cref="AutoDetectMobile"/> is on.</summary>
    public double MobileBreakpoint { get; set; } = 768;

    public string Width { get; set; } = "16rem";
    public string IconWidth { get; set; } = "3rem";
    public string MobileWidth { get; set; } = "18rem";

    /// <summary>Persistence hook: called with the new expanded state whenever it changes.</summary>
    public Action<bool>? Persist { get; set; }

    public string? Class { get; set; }
    public string? Style { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    /// <summary>Values shared with the sidebar parts. Equality covers the state, so readers rebuild only on change.</summary>
    public sealed class Context : IEquatable<Context>
    {
        internal Context(bool open, bool openMobile, bool isMobile, string width, string iconWidth, string mobileWidth,
            Action<bool> setOpen, Action<bool> setOpenMobile, Action toggle)
        {
            Open = open;
            OpenMobile = openMobile;
            IsMobile = isMobile;
            Width = width;
            IconWidth = iconWidth;
            MobileWidth = mobileWidth;
            SetOpen = setOpen;
            SetOpenMobile = setOpenMobile;
            Toggle = toggle;
        }

        public bool Open { get; }
        public bool OpenMobile { get; }
        public bool IsMobile { get; }
        public string Width { get; }
        public string IconWidth { get; }
        public string MobileWidth { get; }
        public Action<bool> SetOpen { get; }
        public Action<bool> SetOpenMobile { get; }
        public Action Toggle { get; }

        public bool Equals(Context? other) =>
            other is not null && Open == other.Open && OpenMobile == other.OpenMobile && IsMobile == other.IsMobile
            && string.Equals(Width, other.Width, StringComparison.Ordinal)
            && string.Equals(IconWidth, other.IconWidth, StringComparison.Ordinal)
            && string.Equals(MobileWidth, other.MobileWidth, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as Context);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Open.GetHashCode();
                hash = hash * 31 + OpenMobile.GetHashCode();
                hash = hash * 31 + IsMobile.GetHashCode();
                hash = hash * 31 + (Width?.GetHashCode() ?? 0);
                hash = hash * 31 + (IconWidth?.GetHashCode() ?? 0);
                hash = hash * 31 + (MobileWidth?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }

    protected override Node Build()
    {
        bool open = _open.Sync(Open, DefaultOpen, OpenChanged + OnOpenChange);
        bool openMobile = _openMobile.Sync(OpenMobile, DefaultOpenMobile, OpenMobileChanged + OnOpenMobileChange);
        var context = new Context(open, openMobile, EffectiveMobile, Width, IconWidth, MobileWidth,
            SetOpen, SetOpenMobile, ToggleSidebar);

        string style = "--sidebar-width: " + Width + "; --sidebar-width-icon: " + IconWidth +
            "; --sidebar-width-mobile: " + MobileWidth;
        if (!string.IsNullOrEmpty(Style)) style += "; " + Style;

        var wrapper = new Div { Children = Children ?? new NodeList(), Style = style };
        wrapper.MergeAttributes(VisualPart.Attributes("sidebar-wrapper",
            "group/sidebar-wrapper flex min-h-svh w-full has-data-[variant=inset]:bg-sidebar", Class, Rest));
        return Provide(context, wrapper);
    }

    UIRoot? _root;
    double _viewportWidth;
    bool _hasViewportWidth;

    /// <summary>The explicit override wins; otherwise the current viewport decides (when auto-detection is on).</summary>
    bool EffectiveMobile => IsMobile || (AutoDetectMobile && CurrentViewportWidth < MobileBreakpoint);

    double CurrentViewportWidth =>
        _hasViewportWidth ? _viewportWidth : Root is { } root ? root.ViewportSize.Width : MobileBreakpoint;

    protected override void OnMount()
    {
        _root = Root;
        _root!.Input.PreviewKeyDown += OnPreviewKeyDown;
        _viewportWidth = _root.ViewportSize.Width;
        _hasViewportWidth = true;
        _root.LayoutCompleted += OnLayoutCompleted;
    }

    protected override void OnUnmount()
    {
        if (_root is not null)
        {
            _root.Input.PreviewKeyDown -= OnPreviewKeyDown;
            _root.LayoutCompleted -= OnLayoutCompleted;
        }
        _root = null;
    }

    /// <summary>
    /// Resize signal: the viewport is only known after layout, and rebuilding here (rather than polling every
    /// frame) re-evaluates the breakpoint in the same frame. Only crossing the threshold rebuilds.
    /// </summary>
    void OnLayoutCompleted(UIRoot root)
    {
        double width = root.ViewportSize.Width;
        if (_hasViewportWidth && width == _viewportWidth) return;
        bool wasMobile = EffectiveMobile;
        _viewportWidth = width;
        _hasViewportWidth = true;
        if (EffectiveMobile != wasMobile) InvalidateFromLayout();
    }

    void OnPreviewKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented) return;
        if ((e.Modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if ((e.Modifiers & KeyModifiers.Alt) != 0) return;
        if (!string.Equals(e.Key, "b", StringComparison.OrdinalIgnoreCase)) return;
        e.PreventDefault();
        ToggleSidebar();
    }

    public void ToggleSidebar()
    {
        if (EffectiveMobile) SetOpenMobile(!_openMobile.Value);
        else SetOpen(!_open.Value);
    }

    void SetOpen(bool value)
    {
        _open.Set(value);
        Persist?.Invoke(value);
    }

    void SetOpenMobile(bool value) => _openMobile.Set(value);
}

/// <summary>The sidebar container. Renders a static column, an icon-collapsible rail, or a mobile sheet.</summary>
public sealed partial class Sidebar : Component
{
    public SidebarSide Side { get; set; }
    public SidebarVariant Variant { get; set; }
    public SidebarCollapsible Collapsible { get; set; } = SidebarCollapsible.Offcanvas;
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var context = Use<SidebarProvider.Context>();
        bool left = Side == SidebarSide.Left;

        if (Collapsible == SidebarCollapsible.None)
        {
            var plain = new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() };
            var plainAttributes = VisualPart.Attributes("sidebar",
                "group relative flex h-full w-[var(--sidebar-width)] flex-col bg-sidebar text-sidebar-foreground", Class, Rest);
            plainAttributes["data-side"] = left ? "left" : "right";
            plain.MergeAttributes(plainAttributes);
            return plain;
        }

        if (context.IsMobile)
        {
            var inner = new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() };
            var innerAttributes = VisualPart.Attributes("sidebar", "flex h-full w-full flex-col", Class, Rest);
            innerAttributes["data-sidebar"] = "sidebar";
            innerAttributes["data-mobile"] = "true";
            innerAttributes["data-side"] = left ? "left" : "right";
            innerAttributes["data-state"] = context.OpenMobile ? "expanded" : "collapsed";
            inner.MergeAttributes(innerAttributes);

            var sheetContent = new SheetContent
            {
                Side = left ? PrimSide.Left : PrimSide.Right,
                ShowCloseButton = false,
                Class = "w-[var(--sidebar-width-mobile)] gap-0 bg-sidebar p-0 text-sidebar-foreground",
                Rest = new AttributeDictionary
                {
                    ["style"] = "--sidebar-width-mobile: " + context.MobileWidth,
                    ["data-sidebar"] = "sidebar",
                },
                Children =
                [
                    new SheetHeader
                    {
                        Class = "sr-only",
                        Children =
                        [
                            new SheetTitle { Children = [new UIText("Sidebar")] },
                            new SheetDescription { Children = [new UIText("Displays the mobile sidebar.")] },
                        ],
                    },
                    inner,
                ],
            };
            return new Sheet
            {
                Open = context.OpenMobile,
                OnOpenChange = context.SetOpenMobile,
                Children = [sheetContent],
            };
        }

        bool open = context.Open;
        string state = open ? "expanded" : "collapsed";
        string collapsible = Collapsible == SidebarCollapsible.Icon ? "icon" : "offcanvas";

        string gapClass = "relative w-[var(--sidebar-width)] bg-transparent transition-[width] duration-200 ease-linear " +
            "group-data-[collapsible=offcanvas]:w-0 group-data-[side=right]:rotate-180 " +
            (Variant is SidebarVariant.Floating or SidebarVariant.Inset
                ? "group-data-[collapsible=icon]:w-[calc(var(--sidebar-width-icon)+1rem)]"
                : "group-data-[collapsible=icon]:w-[var(--sidebar-width-icon)]");
        var gap = new Div { Class = gapClass };
        gap.Attributes["data-slot"] = "sidebar-gap";

        string sideClass = left
            ? "left-0 group-data-[collapsible=offcanvas]:left-[calc(var(--sidebar-width)*-1)]"
            : "right-0 group-data-[collapsible=offcanvas]:right-[calc(var(--sidebar-width)*-1)]";
        string containerClass = "fixed inset-y-0 z-10 hidden h-svh w-[var(--sidebar-width)] " +
            "transition-[left,right,width] duration-200 ease-linear md:flex " + sideClass + " " +
            (Variant is SidebarVariant.Floating or SidebarVariant.Inset
                ? "p-2 group-data-[collapsible=icon]:w-[calc(var(--sidebar-width-icon)+1rem+2px)]"
                : "group-data-[collapsible=icon]:w-[var(--sidebar-width-icon)] " +
                  (left ? "group-data-[side=left]:border-r" : "group-data-[side=right]:border-l"));
        var innerSidebar = new Div
        {
            Class = "flex h-full w-full flex-col bg-sidebar group-data-[variant=floating]:rounded-lg " +
                "group-data-[variant=floating]:border group-data-[variant=floating]:border-sidebar-border " +
                "group-data-[variant=floating]:shadow-sm",
            Children = Children ?? new NodeList(),
        };
        innerSidebar.Attributes["data-slot"] = "sidebar-inner";
        innerSidebar.Attributes["data-sidebar"] = "sidebar";
        var container = new Div { Class = containerClass, Children = [innerSidebar] };
        container.Attributes["data-slot"] = "sidebar-container";

        var root = new Div { Id = Id, Ref = Ref, Children = [gap, container] };
        var attributes = VisualPart.Attributes("sidebar",
            "group peer hidden text-sidebar-foreground md:block", Class, Rest);
        attributes["data-state"] = state;
        if (!open) attributes["data-collapsible"] = collapsible;
        attributes["data-variant"] = Variant switch
        {
            SidebarVariant.Floating => "floating",
            SidebarVariant.Inset => "inset",
            _ => "sidebar",
        };
        attributes["data-side"] = left ? "left" : "right";
        attributes["data-sidebar"] = "sidebar";
        root.MergeAttributes(attributes);
        return root;
    }
}

public sealed partial class SidebarTrigger : Component
{
    const string TriggerClass =
        "inline-flex size-7 shrink-0 items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap " +
        "transition-all outline-none hover:bg-accent hover:text-accent-foreground focus-visible:border-ring " +
        "focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-50 " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4";

    public string? Class { get; set; }
    public string Label { get; set; } = "Toggle Sidebar";
    public Action<PointerEvent>? OnClick { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var context = Use<SidebarProvider.Context>();
        var button = new UIButton
        {
            Ref = Ref,
            Role = Role.Button,
            Children = Children ?? new NodeList
            {
                SelectParts.Glyph(LucideIcons.PanelLeft, "sidebar-trigger-icon", "size-4"),
                new Span(Label) { Class = "sr-only" },
            },
        };
        var attributes = VisualPart.Attributes("sidebar-trigger", TriggerClass, Class, Rest);
        attributes["data-sidebar"] = "trigger";
        attributes["type"] = "button";
        attributes["aria-label"] = attributes["aria-label"] ?? Label;
        attributes["aria-expanded"] = TabsParts.Bool(context.IsMobile ? context.OpenMobile : context.Open);
        button.MergeAttributes(attributes);
        button.OnClick = EventComposition.Compose(button.OnClick, OnClick);
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (!e.DefaultPrevented) context.Toggle();
        });
        return button;
    }
}

public sealed partial class SidebarHeader : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() },
            "sidebar-header", "header", "flex flex-col gap-2 p-2", Class, Rest);
}

public sealed partial class SidebarFooter : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() },
            "sidebar-footer", "footer", "flex flex-col gap-2 p-2", Class, Rest);
}

public sealed partial class SidebarContent : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() },
            "sidebar-content", "content",
            "flex min-h-0 flex-1 flex-col gap-2 overflow-auto group-data-[collapsible=icon]:overflow-hidden", Class, Rest);
}

public sealed partial class SidebarGroup : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() },
            "sidebar-group", "group", "relative flex w-full min-w-0 flex-col p-2", Class, Rest);
}

public sealed partial class SidebarGroupLabel : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        const string Defaults = "flex h-8 shrink-0 items-center rounded-md px-2 text-xs font-medium " +
            "text-sidebar-foreground/70 ring-sidebar-ring outline-hidden transition-[margin,opacity] duration-200 " +
            "ease-linear focus-visible:ring-2 group-data-[collapsible=icon]:-mt-8 group-data-[collapsible=icon]:opacity-0 " +
            "[&>svg]:size-4 [&>svg]:shrink-0";
        var carrier = new Div { Ref = Ref, Children = Children ?? new NodeList() };
        carrier.MergeAttributes(VisualPart.Attributes("sidebar-group-label", Defaults, Class, Rest));
        carrier.Attributes["data-sidebar"] = "group-label";
        return AsChild ? Slot.Merge(carrier, Slot.SingleChild(Children)) : carrier;
    }
}

public sealed partial class SidebarGroupContent : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new Div { Id = Id, Ref = Ref, Children = Children ?? new NodeList() },
            "sidebar-group-content", "group-content", "w-full text-sm", Class, Rest);
}

public sealed partial class SidebarMenu : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new StaticParts.TagElement("ul") { Id = Id, Ref = Ref, Role = Role.List, Children = Children ?? new NodeList() },
            "sidebar-menu", "menu", "flex w-full min-w-0 flex-col gap-1", Class, Rest);
}

public sealed partial class SidebarMenuItem : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() =>
        SidebarParts.Box(new StaticParts.TagElement("li") { Id = Id, Ref = Ref, Role = Role.ListItem, Children = Children ?? new NodeList() },
            "sidebar-menu-item", "menu-item", "group/menu-item relative", Class, Rest);
}

/// <summary>A menu row. Set <see cref="IsActive"/> for the current page, <see cref="AsChild"/> to render a link, and
/// <see cref="TooltipText"/> to show a right-side tooltip while the sidebar is collapsed.</summary>
public sealed partial class SidebarMenuButton : Component
{
    const string Defaults =
        "peer/menu-button flex w-full items-center gap-2 overflow-hidden rounded-md p-2 text-left text-sm " +
        "ring-sidebar-ring outline-hidden transition-[width,height,padding] " +
        "group-has-data-[sidebar=menu-action]/menu-item:pr-8 group-data-[collapsible=icon]:size-8! " +
        "group-data-[collapsible=icon]:p-2! hover:bg-sidebar-accent hover:text-sidebar-accent-foreground " +
        "focus-visible:ring-2 active:bg-sidebar-accent active:text-sidebar-accent-foreground " +
        "disabled:pointer-events-none disabled:opacity-50 aria-disabled:pointer-events-none aria-disabled:opacity-50 " +
        "data-[active=true]:bg-sidebar-accent data-[active=true]:font-medium data-[active=true]:text-sidebar-accent-foreground " +
        "data-[state=open]:hover:bg-sidebar-accent data-[state=open]:hover:text-sidebar-accent-foreground " +
        "[&>span:last-child]:truncate [&>svg]:size-4 [&>svg]:shrink-0";

    public SidebarMenuButtonVariant Variant { get; set; }
    public SidebarMenuButtonSize Size { get; set; }
    public bool IsActive { get; set; }
    public bool AsChild { get; set; }
    public bool Disabled { get; set; }
    public bool Open { get; set; }
    public string? TooltipText { get; set; }
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var context = Use<SidebarProvider.Context>();
        string variant = Variant == SidebarMenuButtonVariant.Outline
            ? "bg-background shadow-[0_0_0_1px_var(--sidebar-border)] hover:bg-sidebar-accent " +
              "hover:text-sidebar-accent-foreground hover:shadow-[0_0_0_1px_var(--sidebar-accent)]"
            : "hover:bg-sidebar-accent hover:text-sidebar-accent-foreground";
        string size = Size switch
        {
            SidebarMenuButtonSize.Sm => "h-7 text-xs",
            SidebarMenuButtonSize.Lg => "h-12 text-sm group-data-[collapsible=icon]:p-0!",
            _ => "h-8 text-sm",
        };

        var button = new UIButton
        {
            Id = Id,
            Ref = Ref,
            Role = Role.Button,
            Disabled = Disabled,
            Children = Children ?? new NodeList(),
        };
        var attributes = VisualPart.Attributes("sidebar-menu-button", Defaults + " " + variant + " " + size, Class, Rest);
        attributes["data-sidebar"] = "menu-button";
        attributes["data-size"] = Size switch
        {
            SidebarMenuButtonSize.Sm => "sm",
            SidebarMenuButtonSize.Lg => "lg",
            _ => "default",
        };
        attributes["data-active"] = TabsParts.Bool(IsActive);
        attributes["type"] = "button";
        if (Open) attributes["data-state"] = "open";
        if (Disabled)
        {
            attributes["data-disabled"] = "";
            attributes["aria-disabled"] = "true";
        }
        button.MergeAttributes(attributes);
        button.OnClick = EventComposition.Compose(button.OnClick, OnClick);

        Node node = AsChild ? Slot.Merge(button, Slot.SingleChild(Children)) : button;
        if (!string.IsNullOrEmpty(TooltipText) && !context.IsMobile && !context.Open)
        {
            return new Tooltip
            {
                Children =
                [
                    new TooltipTrigger { AsChild = true, Children = [node] },
                    new TooltipContent { Side = PrimSide.Right, Children = [new UIText(TooltipText!)] },
                ],
            };
        }
        return node;
    }
}

/// <summary>A small action rendered at the trailing edge of a menu item.</summary>
public sealed partial class SidebarMenuAction : Component
{
    const string Defaults =
        "absolute top-1.5 right-1 flex aspect-square w-5 items-center justify-center rounded-md p-0 " +
        "text-sidebar-foreground ring-sidebar-ring outline-hidden transition-transform " +
        "peer-hover/menu-button:text-sidebar-accent-foreground hover:bg-sidebar-accent " +
        "hover:text-sidebar-accent-foreground focus-visible:ring-2 [&>svg]:size-4 [&>svg]:shrink-0 " +
        "after:absolute after:-inset-2 md:after:hidden group-data-[collapsible=icon]:hidden " +
        "peer-data-[size=sm]/menu-button:top-1 peer-data-[size=default]/menu-button:top-1.5 " +
        "peer-data-[size=lg]/menu-button:top-2.5";

    public bool AsChild { get; set; }
    public bool ShowOnHover { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        string hover = ShowOnHover
            ? " group-focus-within/menu-item:opacity-100 group-hover/menu-item:opacity-100 " +
              "peer-data-[active=true]/menu-button:text-sidebar-accent-foreground data-[state=open]:opacity-100 md:opacity-0"
            : "";
        var button = new UIButton
        {
            Ref = Ref,
            Role = Role.Button,
            Disabled = Disabled,
            Children = Children ?? new NodeList(),
        };
        var attributes = VisualPart.Attributes("sidebar-menu-action", Defaults + hover, Class, Rest);
        attributes["data-sidebar"] = "menu-action";
        attributes["type"] = "button";
        button.MergeAttributes(attributes);
        button.OnClick = EventComposition.Compose(button.OnClick, OnClick);
        return AsChild ? Slot.Merge(button, Slot.SingleChild(Children)) : button;
    }
}

/// <summary>The main content region next to the sidebar.</summary>
public sealed partial class SidebarInset : Component
{
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        SidebarParts.Box(new StaticParts.TagElement("main") { Id = Id, Ref = Ref, Children = Children ?? new NodeList() },
            "sidebar-inset", "inset",
            "relative flex w-full flex-1 flex-col bg-background " +
            "md:peer-data-[variant=inset]:m-2 md:peer-data-[variant=inset]:ml-0 " +
            "md:peer-data-[variant=inset]:rounded-xl md:peer-data-[variant=inset]:shadow-sm " +
            "md:peer-data-[variant=inset]:peer-data-[state=collapsed]:ml-2", Class, Rest);
}

/// <summary>The thin draggable-looking strip on the sidebar edge; clicking it toggles the sidebar.</summary>
public sealed partial class SidebarRail : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var context = Use<SidebarProvider.Context>();
        var button = new UIButton { TabIndex = -1, Role = Role.Button };
        var attributes = VisualPart.Attributes("sidebar-rail",
            "absolute inset-y-0 z-20 hidden w-4 -translate-x-1/2 transition-all ease-linear " +
            "group-data-[side=left]:-right-4 group-data-[side=right]:left-0 " +
            "after:absolute after:inset-y-0 after:left-1/2 after:w-[2px] hover:after:bg-sidebar-border sm:flex " +
            "in-data-[side=left]:cursor-w-resize in-data-[side=right]:cursor-e-resize " +
            "[[data-side=left][data-state=collapsed]_&]:cursor-e-resize [[data-side=right][data-state=collapsed]_&]:cursor-w-resize " +
            "group-data-[collapsible=offcanvas]:translate-x-0 group-data-[collapsible=offcanvas]:after:left-full " +
            "hover:group-data-[collapsible=offcanvas]:bg-sidebar " +
            "[[data-side=left][data-collapsible=offcanvas]_&]:-right-2 [[data-side=right][data-collapsible=offcanvas]_&]:-left-2", Class, Rest);
        attributes["data-sidebar"] = "rail";
        attributes["title"] = "Toggle Sidebar";
        attributes["aria-label"] = "Toggle Sidebar";
        attributes["type"] = "button";
        button.MergeAttributes(attributes);
        button.AriaLabel = "Toggle Sidebar";
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (!e.DefaultPrevented) context.Toggle();
        });
        return button;
    }
}

/// <summary>An input styled to match the sidebar surface.</summary>
public sealed partial class SidebarInput : Component
{
    const string InputClass =
        "h-9 w-full min-w-0 rounded-md border border-input bg-transparent px-3 py-1 text-base shadow-xs " +
        "transition-[color,box-shadow] outline-none selection:bg-primary/25 placeholder:text-muted-foreground " +
        "disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm dark:bg-input/30 " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40";

    public string? Value { get; set; }
    public Action<string>? ValueChanged { get; set; }
    public string? Id { get; set; }
    public string? InputType { get; set; }
    public string? Placeholder { get; set; }
    public bool Disabled { get; set; }
    public bool ReadOnly { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<InputEvent>? OnInput { get; set; }
    public Action<InputEvent>? OnChange { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var input = new UI.Input
        {
            Id = Id,
            InputType = InputType,
            Value = Value,
            Placeholder = Placeholder,
            Disabled = Disabled,
            ReadOnly = ReadOnly,
            Ref = Ref,
            OnInput = OnInput,
            OnChange = OnChange,
        };
        input.Attributes["data-slot"] = "sidebar-input";
        input.Attributes["data-sidebar"] = "input";
        input.MergeAttributes(Rest);
        input.Class = AwAUI.Tailwind.TailwindMerge.Cn(InputClass, input.Class.Text, Class,
            "h-8 w-full bg-background shadow-none");
        if (ValueChanged is { } changed)
        {
            var handler = input.OnInput;
            input.OnInput = e =>
            {
                changed(e.Value);
                handler?.Invoke(e);
            };
        }
        return input;
    }
}

/// <summary>A divider between sidebar sections.</summary>
public sealed partial class SidebarSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var separator = new Div { Role = Role.Presentation };
        separator.Attributes["data-slot"] = "sidebar-separator";
        separator.Attributes["data-sidebar"] = "separator";
        separator.Attributes["data-orientation"] = "horizontal";
        separator.MergeAttributes(Rest);
        separator.Class = AwAUI.Tailwind.TailwindMerge.Cn(
            "shrink-0 bg-border data-[orientation=horizontal]:h-px data-[orientation=horizontal]:w-full",
            separator.Class.Text, "mx-2 w-auto bg-sidebar-border", Class);
        return separator;
    }
}

/// <summary>A small action rendered at the trailing edge of a group header.</summary>
public sealed partial class SidebarGroupAction : Component
{
    const string Defaults =
        "absolute top-3.5 right-3 flex aspect-square w-5 items-center justify-center rounded-md p-0 " +
        "text-sidebar-foreground ring-sidebar-ring outline-hidden transition-transform " +
        "hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 " +
        "[&>svg]:size-4 [&>svg]:shrink-0 after:absolute after:-inset-2 md:after:hidden " +
        "group-data-[collapsible=icon]:hidden";

    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var button = new UIButton { Ref = Ref, Role = Role.Button, Children = Children ?? new NodeList() };
        button.MergeAttributes(VisualPart.Attributes("sidebar-group-action", Defaults, Class, Rest));
        button.Attributes["data-sidebar"] = "group-action";
        button.Attributes["type"] = "button";
        button.OnClick = EventComposition.Compose(button.OnClick, OnClick);
        return AsChild ? Slot.Merge(button, Slot.SingleChild(Children)) : button;
    }
}

/// <summary>A small badge rendered at the trailing edge of a menu row.</summary>
public sealed partial class SidebarMenuBadge : Component
{
    const string Defaults =
        "pointer-events-none absolute right-1 flex h-5 min-w-5 items-center justify-center rounded-md px-1 " +
        "text-xs font-medium text-sidebar-foreground tabular-nums select-none " +
        "peer-hover/menu-button:text-sidebar-accent-foreground peer-data-[active=true]/menu-button:text-sidebar-accent-foreground " +
        "peer-data-[size=sm]/menu-button:top-1 peer-data-[size=default]/menu-button:top-1.5 " +
        "peer-data-[size=lg]/menu-button:top-2.5 group-data-[collapsible=icon]:hidden";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        SidebarParts.Box(new Div { Children = Children ?? new NodeList() }, "sidebar-menu-badge", "menu-badge", Defaults, Class, Rest);
}

/// <summary>A placeholder row shown while menu data loads.</summary>
public sealed partial class SidebarMenuSkeleton : Component
{
    const string Defaults = "flex h-8 items-center gap-2 rounded-md px-2";
    static readonly Random WidthRandom = new();
    readonly string _width = (WidthRandom.Next(40) + 50).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

    public bool ShowIcon { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var children = new NodeList();
        if (ShowIcon)
            children.Add(new Skeleton
            {
                Class = "size-4 rounded-md",
                Rest = new AttributeDictionary { ["data-sidebar"] = "menu-skeleton-icon" },
            });
        children.Add(new Skeleton
        {
            Class = "h-4 max-w-[var(--skeleton-width)] flex-1",
            Style = "--skeleton-width: " + _width,
            Rest = new AttributeDictionary { ["data-sidebar"] = "menu-skeleton-text" },
        });
        return SidebarParts.Box(new Div { Children = children }, "sidebar-menu-skeleton", "menu-skeleton", Defaults, Class, Rest);
    }
}

/// <summary>A nested list under a menu row.</summary>
public sealed partial class SidebarMenuSub : Component
{
    const string Defaults =
        "mx-3.5 flex min-w-0 translate-x-px flex-col gap-1 border-l border-sidebar-border " +
        "px-2.5 py-0.5 group-data-[collapsible=icon]:hidden";

    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        SidebarParts.Box(new StaticParts.TagElement("ul") { Ref = Ref, Role = Role.List, Children = Children ?? new NodeList() },
            "sidebar-menu-sub", "menu-sub", Defaults, Class, Rest);
}

/// <summary>A nested item under a menu row.</summary>
public sealed partial class SidebarMenuSubItem : Component
{
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        SidebarParts.Box(new StaticParts.TagElement("li") { Ref = Ref, Role = Role.ListItem, Children = Children ?? new NodeList() },
            "sidebar-menu-sub-item", "menu-sub-item", "group/menu-sub-item relative", Class, Rest);
}

/// <summary>A link in a nested sidebar list.</summary>
public sealed partial class SidebarMenuSubButton : Component
{
    const string Defaults =
        "flex h-7 min-w-0 -translate-x-px items-center gap-2 overflow-hidden rounded-md px-2 text-sidebar-foreground " +
        "ring-sidebar-ring outline-hidden hover:bg-sidebar-accent hover:text-sidebar-accent-foreground " +
        "focus-visible:ring-2 active:bg-sidebar-accent active:text-sidebar-accent-foreground " +
        "disabled:pointer-events-none disabled:opacity-50 aria-disabled:pointer-events-none aria-disabled:opacity-50 " +
        "[&>span:last-child]:truncate [&>svg]:size-4 [&>svg]:shrink-0 [&>svg]:text-sidebar-accent-foreground " +
        "data-[active=true]:bg-sidebar-accent data-[active=true]:text-sidebar-accent-foreground " +
        "group-data-[collapsible=icon]:hidden";

    public bool AsChild { get; set; }
    public bool IsActive { get; set; }
    public bool Disabled { get; set; }
    public bool Small { get; set; }
    public string? Href { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var link = StaticParts.Link("sidebar-menu-sub-button", Defaults, Href, Disabled,
            AwAUI.Tailwind.TailwindMerge.Cn(Small ? "text-xs" : "text-sm", Class), Rest, Children, OnClick, Ref);
        link.Attributes["data-sidebar"] = "menu-sub-button";
        link.Attributes["data-size"] = Small ? "sm" : "md";
        link.Attributes["data-active"] = TabsParts.Bool(IsActive);
        return AsChild ? Slot.Merge(link, Slot.SingleChild(Children)) : link;
    }
}

internal static class SidebarParts
{
    internal static T Box<T>(T node, string slot, string sidebarName, string defaults, string? classes,
        AttributeDictionary? rest) where T : ElementNode
    {
        var attributes = VisualPart.Attributes(slot, defaults, classes, rest);
        attributes["data-sidebar"] = sidebarName;
        node.MergeAttributes(attributes);
        return node;
    }
}
