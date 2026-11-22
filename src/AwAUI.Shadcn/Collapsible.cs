using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

public sealed partial class Collapsible : Component
{
    readonly ControllableState<bool> _open = new();
    readonly string _contentId = TabsParts.NextId("collapsible");
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool open = _open.Sync(Open, DefaultOpen, OpenChanged + OnOpenChange);
        var root = TabsParts.Style(new Div { Children = Children ?? [] }, "collapsible", null, Class, Rest);
        root.Attributes["data-state"] = TabsParts.OpenState(open);
        TabsParts.Disabled(root, Disabled);
        return Provide(new Scope(open, Disabled, _contentId, () => _open.Set(!_open.Value)), root);
    }

    internal sealed record Scope(bool Open, bool Disabled, string ContentId, Action Toggle);
}

public sealed partial class CollapsibleTrigger : Component
{
    public bool AsChild { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = Use<Collapsible.Scope>();
        var trigger = TabsParts.Style(new UIButton { Disabled = Disabled || group.Disabled, Children = Children ?? [] },
            "collapsible-trigger", null, Class, Rest);
        trigger.Attributes["aria-controls"] = group.ContentId;
        trigger.Attributes["aria-expanded"] = TabsParts.Bool(group.Open);
        trigger.Attributes["data-state"] = TabsParts.OpenState(group.Open);
        TabsParts.Activate(trigger, group.Toggle);
        return AsChild ? Slot.Merge(trigger, Slot.SingleChild(Children)) : trigger;
    }
}

public sealed partial class CollapsibleContent : Component
{
    public bool ForceMount { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = Use<Collapsible.Scope>();
        var panel = TabsParts.Style(new Div { Id = group.ContentId, Children = Children ?? [] }, "collapsible-content", null, Class, Rest);
        panel.Attributes["data-state"] = TabsParts.OpenState(group.Open);
        TabsParts.Disabled(panel, group.Disabled);
        if (ForceMount && !group.Open) TabsParts.Hide(panel);
        return new PresenceGate { Present = group.Open, ForceMount = ForceMount, Children = [panel] };
    }
}
