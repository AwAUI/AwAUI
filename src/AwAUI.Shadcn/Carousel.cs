using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A horizontal carousel with transform-based paging, previous/next controls and dot indicators.</summary>
public sealed partial class Carousel : Component
{

    // Created on the first Build so DefaultIndex seeds the uncontrolled index (React's useState initial state).
    CarouselScope? _scope;

    public Orientation Orientation { get; set; }
    public Direction? Dir { get; set; }
    public bool Loop { get; set; }

    /// <summary>Controlled slide index. When null the carousel keeps its own index.</summary>
    public int? Index { get; set; }
    public int DefaultIndex { get; set; }
    public Action<int>? IndexChanged { get; set; }
    public Action<int>? OnIndexChange { get; set; }

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var items = CarouselTree.CollectItems(Children);
        // DefaultIndex only seeds the uncontrolled state, and only once (like React's useState initial value).
        var scope = _scope ??= new CarouselScope(Index is null ? DefaultIndex : 0);
        scope.Count = items.Count;
        scope.Loop = Loop;
        scope.Orientation = Orientation;
        scope.Dir = Dir;
        scope.ControlledIndex = Index;
        scope.SetIndex = SetIndex;
        for (int i = 0; i < items.Count; i++)
        {
            items[i].SlideIndex = i;
            items[i].SlideCount = items.Count;
        }

        // A controlled index is mirrored into the state parts subscribe to.
        UseEffect(() =>
        {
            if (Index is not int controlled) return null;
            int count = scope.Count;
            int normalized = count <= 0 ? 0 : ShadcnMath.Clamp(controlled, 0, count - 1);
            if (scope.Index.Value != normalized) scope.Index.Value = normalized;
            return null;
        }, Index);

        var node = TabsParts.Style(new Div
        {
            Role = Role.Region,
            Children = Children ?? new NodeList(),
        }, "carousel", "relative", Class, Rest);
        node.Attributes["aria-roledescription"] = "carousel";
        node.Attributes["data-orientation"] = TabsParts.Axis(Orientation);
        node.AriaLabel ??= "Carousel";
        if (Dir is { } dir) node.Attributes["dir"] = dir == Direction.Rtl ? "rtl" : "ltr";
        node.OnKeyDown = EventComposition.Chain(node.OnKeyDown, HandleKey);
        return Context.Provide(scope, node);
    }

    void SetIndex(int next)
    {
        if (_scope is not { } scope) return;
        int count = scope.Count;
        if (count <= 0) return;
        int normalized = ShadcnMath.Clamp(next, 0, count - 1);
        if (normalized == scope.CurrentIndex) return;
        if (Index is null) scope.Index.Value = normalized;
        (IndexChanged + OnIndexChange)?.Invoke(normalized);
    }

    void HandleKey(KeyEvent e)
    {
        if (e.DefaultPrevented || _scope is not { } scope) return;
        int direction = e.Key switch
        {
            "ArrowRight" => 1,
            "ArrowLeft" => -1,
            _ => 0,
        };
        if (direction == 0) return;
        if (TabsParts.ResolveDirection(e.CurrentTarget, Dir) == Direction.Rtl) direction = -direction;
        e.PreventDefault();
        scope.Move(direction);
    }
}

public sealed partial class CarouselContent : Component
{
    public string? Class { get; set; }
    public string? OuterClass { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CarouselScope>();
        int index = scope.CurrentIndex;
        bool vertical = scope.Orientation == Orientation.Vertical;

        var inner = new Div
        {
            Class = Tailwind.TailwindMerge.Cn("flex motion-safe:transition-transform motion-safe:duration-300",
                vertical ? "-mt-4 flex-col" : "-ml-4", Class),
            Children = Children ?? new NodeList(),
        };
        inner.MergeAttributes(Rest);
        inner.Class = Tailwind.TailwindMerge.Cn(inner.Class.Text, "motion-reduce:transition-none");
        inner.Style = new StyleValue((vertical ? "transform:translateY(-" : "transform:translateX(-")
            + (index * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%);");

        return TabsParts.Style(new Div { Ref = Ref, Children = [inner] },
            "carousel-content", "overflow-hidden", OuterClass, null);
    }
}

public sealed partial class CarouselItem : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal int SlideIndex { get; set; } = -1;
    internal int SlideCount { get; set; }

    protected override Node Build()
    {
        var scope = Use<CarouselScope>();
        _ = scope.CurrentIndex;
        bool vertical = scope.Orientation == Orientation.Vertical;
        var node = TabsParts.Style(new Div
        {
            Role = Role.Group,
            Children = Children ?? new NodeList(),
        }, "carousel-item",
            "min-w-0 shrink-0 grow-0 basis-full " + (vertical ? "pt-4" : "pl-4"), Class, Rest);
        node.Attributes["aria-roledescription"] = "slide";
        if (SlideCount > 0 && SlideIndex >= 0)
        {
            node.AriaLabel ??= (SlideIndex + 1).ToString(CultureInfo.InvariantCulture) + " of "
                + SlideCount.ToString(CultureInfo.InvariantCulture);
            node.Attributes["data-index"] = SlideIndex.ToString(CultureInfo.InvariantCulture);
        }
        return node;
    }
}

public sealed partial class CarouselPrevious : Component
{
    internal const string BaseClass =
        "inline-flex shrink-0 items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap " +
        "transition-all outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0";
    const string OutlineClass =
        "border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:border-input " +
        "dark:bg-input/30 dark:hover:bg-input/50";
    const string HorizontalClass = "absolute size-8 rounded-full top-1/2 -left-12 -translate-y-1/2";
    const string VerticalClass = "absolute size-8 rounded-full -top-12 left-1/2 -translate-x-1/2 rotate-90";

    public ButtonVariant Variant { get; set; } = ButtonVariant.Outline;
    public ButtonSize Size { get; set; } = ButtonSize.Icon;
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CarouselScope>();
        bool canScroll = scope.CanPrevious;
        bool vertical = scope.Orientation == Orientation.Vertical;

        var button = new UIButton { Disabled = !canScroll };
        button.Attributes["data-slot"] = "carousel-previous";
        button.MergeAttributes(Rest);
        button.Class = Tailwind.TailwindMerge.Cn(BaseClass,
            Variant == ButtonVariant.Outline ? OutlineClass : VariantClass(Variant),
            "size-9", vertical ? VerticalClass : HorizontalClass, button.Class.Text, Class);
        button.Attributes["data-slot"] = "carousel-previous";
        button.OnClick = EventComposition.Chain(button.OnClick, e =>
        {
            if (!e.DefaultPrevented && canScroll) scope.Move(-1);
        });
        button.Children.Add(SelectParts.Glyph(LucideIcons.ChevronLeft, "carousel-previous-icon", "size-4"));
        button.Children.Add(HiddenLabel("Previous slide"));
        return button;
    }

    internal static string VariantClass(ButtonVariant variant) => variant switch
    {
        ButtonVariant.Default => "bg-primary text-primary-foreground hover:bg-primary/90",
        ButtonVariant.Destructive => "bg-destructive text-white hover:bg-destructive/90",
        ButtonVariant.Secondary => "bg-secondary text-secondary-foreground hover:bg-secondary/80",
        ButtonVariant.Ghost => "hover:bg-accent hover:text-accent-foreground",
        ButtonVariant.Link => "text-primary underline-offset-4 hover:underline",
        _ => OutlineClass,
    };

    internal static Span HiddenLabel(string text)
    {
        var span = new Span(text) { Class = "sr-only" };
        span.Attributes["data-slot"] = "carousel-sr-only";
        return span;
    }
}

public sealed partial class CarouselNext : Component
{
    const string HorizontalClass = "absolute size-8 rounded-full top-1/2 -right-12 -translate-y-1/2";
    const string VerticalClass = "absolute size-8 rounded-full -bottom-12 left-1/2 -translate-x-1/2 rotate-90";

    public ButtonVariant Variant { get; set; } = ButtonVariant.Outline;
    public ButtonSize Size { get; set; } = ButtonSize.Icon;
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CarouselScope>();
        bool canScroll = scope.CanNext;
        bool vertical = scope.Orientation == Orientation.Vertical;

        var button = new UIButton { Disabled = !canScroll };
        button.Attributes["data-slot"] = "carousel-next";
        button.MergeAttributes(Rest);
        button.Class = Tailwind.TailwindMerge.Cn(CarouselPrevious.BaseClass,
            CarouselPrevious.VariantClass(Variant),
            "size-9", vertical ? VerticalClass : HorizontalClass, button.Class.Text, Class);
        button.Attributes["data-slot"] = "carousel-next";
        button.OnClick = EventComposition.Chain(button.OnClick, e =>
        {
            if (!e.DefaultPrevented && canScroll) scope.Move(1);
        });
        button.Children.Add(SelectParts.Glyph(LucideIcons.ChevronRight, "carousel-next-icon", "size-4"));
        button.Children.Add(CarouselPrevious.HiddenLabel("Next slide"));
        return button;
    }
}

/// <summary>Optional dot indicators. Clicking a dot pages directly to that slide.</summary>
public sealed partial class CarouselDots : Component
{
    const string DotsClass = "absolute inset-x-0 bottom-2 flex items-center justify-center gap-1";

    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CarouselScope>();
        int count = scope.Count;
        if (count <= 1) return new Fragment();
        int current = scope.CurrentIndex;

        var dots = TabsParts.Style(new Div(), "carousel-dots", DotsClass, Class, Rest);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var dot = new UIButton { TabIndex = i == current ? 0 : -1 };
            dot.Class = Tailwind.TailwindMerge.Cn("size-2 rounded-full border-0 p-0",
                i == current ? "bg-primary" : "bg-muted-foreground/30 hover:bg-muted-foreground/60");
            dot.Attributes["data-slot"] = "carousel-dot";
            dot.Attributes["data-index"] = i.ToString(CultureInfo.InvariantCulture);
            dot.Attributes["aria-current"] = i == current ? "true" : null;
            dot.AriaLabel = "Go to slide " + (i + 1).ToString(CultureInfo.InvariantCulture);
            dot.OnClick = EventComposition.Chain(dot.OnClick, e =>
            {
                if (!e.DefaultPrevented) scope.GoTo(index);
            });
            dots.Children.Add(dot);
        }
        return dots;
    }
}

internal sealed class CarouselScope
{
    internal CarouselScope(int initialIndex = 0) => Index = new(initialIndex);

    internal readonly State<int> Index;
    internal int Count;
    internal bool Loop;
    internal Orientation Orientation;
    internal Direction? Dir;
    internal int? ControlledIndex;
    internal Action<int>? SetIndex;

    internal int CurrentIndex
    {
        get
        {
            int value = Index.Value;
            if (ControlledIndex is { } controlled) value = controlled;
            return Count <= 0 ? 0 : ShadcnMath.Clamp(value, 0, Count - 1);
        }
    }

    internal bool CanPrevious => Count > 0 && (Loop || CurrentIndex > 0);
    internal bool CanNext => Count > 0 && (Loop || CurrentIndex < Count - 1);

    internal void Move(int delta)
    {
        if (Count <= 0) return;
        int current = CurrentIndex;
        int next;
        if (Loop) next = ((current + delta) % Count + Count) % Count;
        else next = ShadcnMath.Clamp(current + delta, 0, Count - 1);
        SetIndex?.Invoke(next);
    }

    internal void GoTo(int index) => SetIndex?.Invoke(index);
}

internal static class CarouselTree
{
    internal static List<CarouselItem> CollectItems(NodeList? children)
    {
        var items = new List<CarouselItem>();
        Visit(children, items);
        return items;
    }

    static void Visit(NodeList? children, List<CarouselItem> items)
    {
        if (children is null) return;
        foreach (var child in children) Visit(child, items);
    }

    static void Visit(Node? node, List<CarouselItem> items)
    {
        switch (node)
        {
            case null:
                return;
            case CarouselItem item:
                items.Add(item);
                return;
            case CarouselContent content:
                Visit(content.Children, items);
                return;
            case Fragment fragment:
                Visit(fragment.Children, items);
                return;
            case ElementNode element:
                Visit(element.Children, items);
                return;
        }
    }
}
