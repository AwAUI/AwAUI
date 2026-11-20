using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A set of disclosure sections with shared selection and keyboard navigation.</summary>
public sealed partial class Accordion : Component
{
    readonly ControllableState<string> _single = new(StringComparer.Ordinal);
    readonly ControllableState<IReadOnlyList<string>> _multiple = new();
    readonly TabsFocus _focus = new();

    public string? Value { get; set; }
    public string DefaultValue { get; set; } = "";
    public Action<string>? ValueChanged { get; set; }
    public bool Multiple { get; set; }
    public IReadOnlyList<string>? Values { get; set; }
    public IReadOnlyList<string> DefaultValues { get; set; } = Array.Empty<string>();
    public Action<IReadOnlyList<string>>? ValuesChanged { get; set; }
    public bool Collapsible { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var selected = _single.Sync(Value, DefaultValue, ValueChanged);
        var expanded = _multiple.Sync(Values, DefaultValues, ValuesChanged);
        var root = TabsParts.Style(new Div { Children = Children ?? [] }, "accordion", "w-full", Class, Rest);
        return Provide(new Scope(Multiple, Collapsible, Disabled, selected, expanded, _focus, Toggle), root);
    }

    void Toggle(string value)
    {
        if (Disabled) return;
        if (Multiple)
        {
            var values = _multiple.Value;
            _multiple.Set(values.Contains(value, StringComparer.Ordinal)
                ? values.Where(item => item != value).ToArray()
                : values.Concat(new[] { value }).ToArray());
        }
        else if (_single.Value != value) _single.Set(value);
        else if (Collapsible) _single.Set("");
    }

    internal sealed record Scope(bool Multiple, bool Collapsible, bool Disabled, string Value,
        IReadOnlyList<string> Values, TabsFocus Focus, Action<string> Toggle)
    {
        internal bool IsOpen(string value) => Multiple ? Values.Contains(value, StringComparer.Ordinal) : Value == value;
    }
}

public sealed partial class AccordionItem : Component
{
    readonly string _id = TabsParts.NextId("accordion-item");
    readonly Ref<Element> _trigger = new();
    public string Value { get; set; } = "";
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        if (Value.Length == 0) throw new InvalidOperationException("Each accordion item needs a nonempty value.");
        var group = Use<Accordion.Scope>();
        bool open = group.IsOpen(Value);
        var root = TabsParts.Style(new Div { Children = Children ?? [] }, "accordion-item", "border-b last:border-b-0", Class, Rest);
        root.Attributes["data-state"] = TabsParts.OpenState(open);
        TabsParts.Disabled(root, Disabled || group.Disabled);
        return Provide(new Scope(group, Value, _id, open, Disabled || group.Disabled, _trigger), root);
    }

    internal sealed record Scope(Accordion.Scope Group, string Value, string Id, bool Open, bool Disabled, Ref<Element> Trigger);
}

public sealed partial class AccordionTrigger : Component
{
    readonly TabsFocus.Item _item = new();

    protected override string GlobalStyleSheet => """
        @layer components {
          :where([data-slot="accordion-trigger"] > [data-slot="accordion-indicator"]) {
            transform: rotate(0deg);
            transition: transform 200ms ease-out;
          }
          :where([data-slot="accordion-trigger"][data-state="open"] > [data-slot="accordion-indicator"]) {
            transform: rotate(180deg);
          }
          @media (prefers-reduced-motion: reduce) {
            :where([data-slot="accordion-trigger"] > [data-slot="accordion-indicator"]) {
              transition: none;
            }
          }
        }
        """;

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var item = Use<AccordionItem.Scope>();
        var button = TabsParts.Style(new UIButton { Id = item.Id + "-trigger", Disabled = item.Disabled,
            Children = new NodeList(Children) }, "accordion-trigger",
            "flex w-full items-center justify-between gap-4 rounded-md border-0 bg-transparent px-0 py-4 text-left text-sm font-medium outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-50",
            Class, Rest);
        button.Ref = item.Trigger;
        button.Attributes["aria-expanded"] = TabsParts.Bool(item.Open);
        button.Attributes["aria-controls"] = item.Id + "-panel";
        button.Attributes["aria-disabled"] = item.Disabled || (item.Open && !item.Group.Multiple && !item.Group.Collapsible) ? "true" : null;
        button.Attributes["data-state"] = TabsParts.OpenState(item.Open);
        button.Children.Add(SelectParts.Glyph(SelectParts.ChevronDown, "accordion-indicator", "size-4 shrink-0 text-muted-foreground"));
        TabsParts.Activate(button, () => item.Group.Toggle(item.Value));
        item.Group.Focus.Wire(button, _item, Orientation.Vertical, null, loop: true, roving: false);
        UseEffect(() => item.Group.Focus.Register(_item, item.Trigger.Current, item.Open), item.Group.Focus, item.Trigger, item.Open, button.Disabled);
        return new Div { Role = Role.Heading, Children = [button] };
    }
}

public sealed partial class AccordionContent : Component
{
    readonly Ref<Element> _panel = new();
    readonly Ref<Element> _body = new();
    readonly DisclosureMotion _motion = new();

    protected override string GlobalStyleSheet => DisclosureMotion.StyleSheet;

    public bool ForceMount { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var item = Use<AccordionItem.Scope>();
        // Keep the padding inside the clipping box so the panel can collapse all the way to zero.
        var body = new Div { Ref = _body, Class = "shrink-0 pb-4", Children = Children ?? [] };
        var panel = TabsParts.Style(new Div { Id = item.Id + "-panel", Role = Role.Region, Children = [body] },
            "accordion-content", "overflow-hidden text-sm", Class, Rest);
        panel.Ref = _panel;
        panel.Attributes["aria-labelledby"] = item.Id + "-trigger";
        panel.Attributes["data-state"] = TabsParts.OpenState(item.Open);
        if (!item.Open)
        {
            panel.Attributes["aria-hidden"] = "true";
            var onFocusIn = panel.OnFocusIn;
            panel.OnFocusIn = e =>
            {
                DisclosureMotion.RestoreFocus(e.CurrentTarget, item.Trigger.Current);
                onFocusIn?.Invoke(e);
            };
        }
        UseEffect(() => _motion.Update(Root, _panel.Current, _body.Current, item.Trigger.Current, item.Open, ForceMount),
            item.Open, ForceMount);
        return new PresenceGate { Present = item.Open, ForceMount = ForceMount, Children = [panel] };
    }
}
