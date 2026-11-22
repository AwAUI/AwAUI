using System;
using AwAUI.Icons.Lucide;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>Edge a <see cref="MessageScrollerButton"/> jumps to.</summary>
public enum MessageScrollerDirection { Start, End }

/// <summary>
/// Shared state of one message scroller: the viewport's <see cref="ScrollController"/>, whether it is at either edge, and
/// whether new content should keep the view pinned to the end.
/// </summary>
internal sealed class MessageScrollerScope
{
    internal readonly State<bool> AtEnd = new(true);
    internal readonly State<bool> AtStart = new(true);
    internal readonly ScrollController Controller = new();

    /// <summary>True while the user is (or starts) at the end; new content then follows it.</summary>
    internal bool Follow = true;

    internal bool AutoFollow = true;

    internal void Sync()
    {
        double max = Controller.MaxOffset.Y;
        double offset = Controller.Offset.Y;
        bool atEnd = max - offset <= 1;
        Follow = atEnd;
        AtEnd.Value = atEnd;
        AtStart.Value = offset <= 1;
    }
}

/// <summary>
/// Provides the scroll state shared by <see cref="MessageScrollerViewport"/>, <see cref="MessageScrollerItem"/> and
/// <see cref="MessageScrollerButton"/>. When <see cref="AutoFollow"/> is true, content appended while the viewport is at the
/// end keeps the view pinned to the end.
/// </summary>
public sealed partial class MessageScrollerProvider : Component
{
    readonly MessageScrollerScope _scope = new();

    public bool AutoFollow { get; set; } = true;
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        _scope.AutoFollow = AutoFollow;
        return Provide(_scope, new Fragment(Children ?? new NodeList()));
    }
}

/// <summary>A scroll container that can follow content appended at the end.</summary>
public sealed partial class MessageScroller : Component
{
    MessageScrollerScope? _own;

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var root = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-scroller",
            "group/message-scroller relative flex size-full min-h-0 flex-col overflow-hidden", Class, Rest);
        // The provider is optional: a bare MessageScroller still shares one scope between its parts.
        if (TryUse<MessageScrollerScope>(out _)) return root;
        if (_own is null) _own = new MessageScrollerScope();
        return Provide(_own, root);
    }
}

/// <summary>The scrollable viewport. It owns the scroll controller and tracks whether it is at either edge.</summary>
public sealed partial class MessageScrollerViewport : Component
{
    const string ViewportClass =
        "size-full min-h-0 min-w-0 scroll-fade-b scrollbar-thin scrollbar-gutter-stable overflow-y-auto " +
        "overscroll-contain contain-content data-autoscrolling:scrollbar-none data-pending-scroll:invisible";

    MessageScrollerScope _scope = null!;

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _scope = Use<MessageScrollerScope>();
        var viewport = StaticParts.Style(
            new Div { ScrollController = _scope.Controller, TabIndex = 0, Children = Children ?? new NodeList() },
            "message-scroller-viewport", ViewportClass, Class, Rest);
        // Content growth (or the first layout) is the only reason to follow; a user scroll updates Follow through Scrolled.
        UseEffect(() =>
        {
            Root?.RequestAfterLayout(FollowToEnd);
            return null;
        }, Children, _scope.AutoFollow);
        return viewport;
    }

    protected override void OnMount() => _scope.Controller.Scrolled += OnScrolled;

    protected override void OnUnmount() => _scope.Controller.Scrolled -= OnScrolled;

    void OnScrolled(ScrollEvent e) => _scope.Sync();

    void FollowToEnd()
    {
        var controller = _scope.Controller;
        if (_scope.AutoFollow && _scope.Follow)
        {
            double max = controller.MaxOffset.Y;
            if (max - controller.Offset.Y > 0.5) controller.JumpTo(controller.Offset.X, max);
        }
        _scope.Sync();
    }
}

/// <summary>Vertical stack of <see cref="MessageScrollerItem"/>s.</summary>
public sealed partial class MessageScrollerContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() },
        "message-scroller-content", "flex h-max min-h-full flex-col gap-8", Class, Rest);
}

/// <summary>One message. Enable <see cref="ScrollAnchor"/> to anchor the view to this item.</summary>
public sealed partial class MessageScrollerItem : Component
{
    public bool ScrollAnchor { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        // 官方用 [contain-intrinsic-size:auto_10rem] [content-visibility:auto] 做渲染性能提示；
        // AwAUI 的 CSS 引擎不实现 containment，保留这些声明只会产生 AW2002 且没有视觉效果，故省略。
        var item = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-scroller-item",
            "min-w-0 shrink-0", Class, Rest);
        if (ScrollAnchor) item.Attributes["data-scroll-anchor"] = "";
        return item;
    }
}

/// <summary>Jumps to the start or end of the viewport and is only active while there is somewhere to jump to.</summary>
public sealed partial class MessageScrollerButton : Component
{
    const string DefaultClass =
        "absolute inset-s-1/2 -translate-x-1/2 border-border bg-background text-foreground " +
        "transition-[translate,scale,opacity] duration-200 hover:bg-muted hover:text-foreground " +
        "data-[active=false]:pointer-events-none data-[active=false]:scale-95 data-[active=false]:opacity-0 " +
        "data-[active=false]:duration-400 data-[active=false]:ease-[cubic-bezier(0.7,0,0.84,0)] " +
        "data-[active=true]:translate-y-0 data-[active=true]:scale-100 data-[active=true]:opacity-100 " +
        "data-[active=true]:ease-[cubic-bezier(0.23,1,0.32,1)] " +
        "data-[direction=end]:bottom-4 data-[direction=end]:data-[active=false]:translate-y-full " +
        "data-[direction=start]:top-4 data-[direction=start]:data-[active=false]:-translate-y-full " +
        "rtl:translate-x-1/2 " +
        "data-[direction=start]:[&_svg]:rotate-180";

    MessageScrollerScope _scope = null!;

    public MessageScrollerDirection Direction { get; set; } = MessageScrollerDirection.End;
    public ButtonVariant? Variant { get; set; }
    public ButtonSize? Size { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _scope = Use<MessageScrollerScope>();
        bool active = Direction == MessageScrollerDirection.End ? !_scope.AtEnd.Value : !_scope.AtStart.Value;
        var variant = Variant ?? ButtonVariant.Secondary;
        var size = Size ?? ButtonSize.IconSm;

        var button = new UIButton
        {
            Disabled = false,
            TabIndex = active ? 0 : -1,
            Children = Children ?? DefaultChildren(),
        };
        var classes = DefaultClass + " " + MessageScrollerButtonParts.VariantClasses(variant, size);
        var node = TabsParts.Style(button, "message-scroller-button", classes, Class, Rest);
        node.Attributes["type"] = "button";
        node.Attributes["data-direction"] = Direction == MessageScrollerDirection.Start ? "start" : "end";
        node.Attributes["data-variant"] = MessageScrollerButtonParts.VariantName(variant);
        node.Attributes["data-size"] = MessageScrollerButtonParts.SizeName(size);
        node.Attributes["data-active"] = TabsParts.Bool(active);

        TabsParts.Activate(button, () =>
        {
            var controller = _scope.Controller;
            if (Direction == MessageScrollerDirection.End)
            {
                _scope.Follow = true;
                controller.JumpTo(controller.Offset.X, controller.MaxOffset.Y);
            }
            else
            {
                _scope.Follow = false;
                controller.JumpTo(controller.Offset.X, 0);
            }
        });
        return node;
    }

    NodeList DefaultChildren()
    {
        var glyph = StaticParts.Glyph(LucideIcons.ChevronDown, "size-4");
        glyph.Attributes["data-slot"] = "message-scroller-button-icon";
        var text = new Span(Direction == MessageScrollerDirection.Start ? "Scroll to start" : "Scroll to end")
        {
            Class = "sr-only",
        };
        return new NodeList { glyph, text };
    }
}

internal static class MessageScrollerButtonParts
{
    internal static string VariantName(ButtonVariant variant) => variant switch
    {
        ButtonVariant.Destructive => "destructive",
        ButtonVariant.Outline => "outline",
        ButtonVariant.Secondary => "secondary",
        ButtonVariant.Ghost => "ghost",
        ButtonVariant.Link => "link",
        _ => "default",
    };

    internal static string SizeName(ButtonSize size) => size switch
    {
        ButtonSize.Xs => "xs",
        ButtonSize.Sm => "sm",
        ButtonSize.Lg => "lg",
        ButtonSize.Icon => "icon",
        ButtonSize.IconXs => "icon-xs",
        ButtonSize.IconSm => "icon-sm",
        ButtonSize.IconLg => "icon-lg",
        _ => "default",
    };

    internal static string VariantClasses(ButtonVariant variant, ButtonSize size)
    {
        const string Base =
            "inline-flex shrink-0 items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap " +
            "transition-all outline-none select-none focus-visible:border-ring focus-visible:ring-[3px] " +
            "focus-visible:ring-ring/50 active:translate-y-px disabled:pointer-events-none disabled:opacity-50 " +
            "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4";
        string variantClasses = variant switch
        {
            ButtonVariant.Destructive => "bg-destructive text-white hover:bg-destructive/90",
            ButtonVariant.Outline => "border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50",
            ButtonVariant.Secondary => "bg-secondary text-secondary-foreground hover:bg-secondary/80",
            ButtonVariant.Ghost => "hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50",
            ButtonVariant.Link => "text-primary underline-offset-4 hover:underline",
            _ => "bg-primary text-primary-foreground hover:bg-primary/90",
        };
        string sizeClasses = size switch
        {
            ButtonSize.Xs => "h-6 gap-1 rounded-md px-2 text-xs has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
            ButtonSize.Sm => "h-8 gap-1.5 rounded-md px-3 has-[>svg]:px-2.5",
            ButtonSize.Lg => "h-10 rounded-md px-6 has-[>svg]:px-4",
            ButtonSize.Icon => "size-9",
            ButtonSize.IconXs => "size-6 rounded-md [&_svg:not([class*='size-'])]:size-3",
            ButtonSize.IconSm => "size-8",
            ButtonSize.IconLg => "size-10",
            _ => "h-9 px-4 py-2 has-[>svg]:px-3",
        };
        return TailwindMerge.Cn(Base, variantClasses, sizeClasses);
    }
}
