using System;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

public enum TabsActivationMode { Automatic, Manual }
public enum TabsListVariant { Default, Line }

/// <summary>Coordinates selection between tab triggers and their panels. Trigger and panel values must be unique within their parts.</summary>
public sealed partial class Tabs : Component
{
    readonly ControllableState<string?> _value = new(StringComparer.Ordinal);
    readonly string _id = TabsParts.NextId("tabs");

    public string? Value { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsValueControlled { get; set; }
    public Action<string?>? ValueChanged { get; set; }
    public Action<string?>? OnValueChange { get; set; }
    public Orientation Orientation { get; set; }
    public Direction? Dir { get; set; }
    public TabsActivationMode ActivationMode { get; set; }
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var value = _value.Sync(IsValueControlled || Value is not null, Value, DefaultValue, ValueChanged + OnValueChange);
        var node = TabsParts.Style(new Div { Id = Id, Ref = Ref, Children = Children ?? [] }, "tabs",
            "group/tabs flex gap-2 data-[orientation=horizontal]:flex-col", Class, Rest);
        TabsParts.DirectionAttribute(node, Dir);
        node.Attributes["data-orientation"] = TabsParts.Axis(Orientation);
        return Provide(new Scope(node.Id ?? _id, value, Orientation, Dir, ActivationMode, _value), node);
    }

    internal sealed class Scope(string id, string? value, Orientation orientation, Direction? dir,
        TabsActivationMode mode, ControllableState<string?> state)
    {
        internal string? Value { get; } = value;
        internal Orientation Orientation { get; } = orientation;
        internal Direction? Dir { get; } = dir;
        internal TabsActivationMode ActivationMode { get; } = mode;
        internal void Select(string next) => state.Set(next);
        internal string TriggerId(string key) => TabsParts.ValueId(id + "-trigger", key);
        internal string ContentId(string key) => TabsParts.ValueId(id + "-content", key);
    }
}

public sealed partial class TabsList : Component
{
    readonly TabsFocus _focus = new();

    public TabsListVariant Variant { get; set; }
    public bool Loop { get; set; } = true;
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<Tabs.Scope>();
        var node = TabsParts.Style(new Div { Role = Role.TabList, Ref = Ref, Children = Children ?? [] }, "tabs-list",
            "group/tabs-list inline-flex w-fit items-center justify-center rounded-lg p-[3px] text-muted-foreground " +
            "group-data-[orientation=horizontal]/tabs:h-9 group-data-[orientation=vertical]/tabs:h-fit group-data-[orientation=vertical]/tabs:flex-col " +
            (Variant == TabsListVariant.Line ? "gap-1 rounded-none bg-transparent" : "bg-muted"), Class, Rest);
        node.Attributes["data-variant"] = Variant == TabsListVariant.Line ? "line" : "default";
        node.Attributes["data-orientation"] = node.Attributes["aria-orientation"] = TabsParts.Axis(scope.Orientation);
        UseEffect(() => { _focus.Refresh(); return null; }, Children);
        return Provide(new Scope(_focus, Loop), node);
    }

    internal sealed class Scope(TabsFocus focus, bool loop)
    {
        internal TabsFocus Focus { get; } = focus;
        internal bool Loop { get; } = loop;
    }
}

public sealed partial class TabsTrigger : Component
{
    const string DefaultClass =
        "relative inline-flex h-[calc(100%-1px)] flex-1 items-center justify-center gap-1.5 rounded-md border border-transparent px-2 py-1 text-sm font-medium whitespace-nowrap " +
        "text-foreground/60 motion-safe:transition-all group-data-[orientation=vertical]/tabs:w-full group-data-[orientation=vertical]/tabs:justify-start hover:text-foreground " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-1 focus-visible:outline-ring disabled:pointer-events-none disabled:opacity-50 " +
        "group-data-[variant=default]/tabs-list:data-[state=active]:shadow-sm group-data-[variant=line]/tabs-list:data-[state=active]:shadow-none dark:text-muted-foreground dark:hover:text-foreground " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 " +
        "group-data-[variant=line]/tabs-list:data-[state=active]:bg-transparent dark:group-data-[variant=line]/tabs-list:data-[state=active]:border-transparent dark:group-data-[variant=line]/tabs-list:data-[state=active]:bg-transparent " +
        "data-[state=active]:bg-background data-[state=active]:text-foreground dark:data-[state=active]:border-input dark:data-[state=active]:bg-input/30 dark:data-[state=active]:text-foreground " +
        "after:absolute after:bg-foreground after:opacity-0 motion-safe:after:transition-opacity group-data-[orientation=horizontal]/tabs:after:inset-x-0 group-data-[orientation=horizontal]/tabs:after:bottom-[-5px] " +
        "group-data-[orientation=horizontal]/tabs:after:h-0.5 group-data-[orientation=vertical]/tabs:after:inset-y-0 group-data-[orientation=vertical]/tabs:after:-right-1 group-data-[orientation=vertical]/tabs:after:w-0.5 " +
        "group-data-[variant=line]/tabs-list:data-[state=active]:after:opacity-100";

    readonly TabsFocus.Item _item = new();

    public string Value { get; set; } = "";
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var tabs = Use<Tabs.Scope>();
        var list = Use<TabsList.Scope>();
        string value = Value;
        bool selected = tabs.Value == value;
        var button = TabsParts.Style(new UIButton { Id = tabs.TriggerId(value), Role = Role.Tab, Disabled = Disabled,
            Ref = Ref, OnClick = OnClick, Children = Children ?? [] }, "tabs-trigger", DefaultClass, Class, Rest);
        button.Attributes["data-state"] = selected ? "active" : "inactive";
        button.Attributes["aria-selected"] = TabsParts.Bool(selected);
        button.Attributes["aria-controls"] = tabs.ContentId(value);
        bool preferred = selected && !button.Disabled;
        button.TabIndex = list.Focus.GetTabIndex(_item, preferred);
        TabsParts.Activate(button, () => tabs.Select(value));
        if (tabs.ActivationMode == TabsActivationMode.Automatic)
        {
            var onFocus = button.OnFocus;
            button.OnFocus = e =>
            {
                onFocus?.Invoke(e);
                if (!e.DefaultPrevented && e.Origin != FocusOrigin.Pointer && !button.Disabled) tabs.Select(value);
            };
        }
        list.Focus.Wire(button, _item, tabs.Orientation, tabs.Dir, list.Loop);
        UseEffect(() => list.Focus.Register(_item, FirstElement(), preferred), list.Focus, preferred, button.Disabled);
        return button;
    }

    Element? FirstElement() => GetRootElements() is { Count: > 0 } roots ? roots[0] : null;
}

public sealed partial class TabsContent : Component
{
    public string Value { get; set; } = "";
    /// <summary>Keeps children mounted while the panel is hidden.</summary>
    public bool ForceMount { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var tabs = Use<Tabs.Scope>();
        bool selected = tabs.Value == Value;
        var node = TabsParts.Style(new Div { Id = tabs.ContentId(Value), Role = Role.TabPanel, Ref = Ref,
            Children = selected || ForceMount ? Children ?? [] : [] }, "tabs-content", "flex-1 outline-none", Class, Rest);
        node.TabIndex = selected ? node.TabIndex ?? 0 : null;
        node.Attributes["aria-labelledby"] = tabs.TriggerId(Value);
        node.Attributes["data-state"] = selected ? "active" : "inactive";
        node.Attributes["data-orientation"] = TabsParts.Axis(tabs.Orientation);
        if (!selected) TabsParts.Hide(node);
        return node;
    }
}
