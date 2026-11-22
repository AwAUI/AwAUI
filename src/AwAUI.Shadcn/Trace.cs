using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

/// <summary>Status of a <see cref="TraceLine"/> (rendered as <c>data-status</c>).</summary>
public enum TraceStatusKind { Pending, Running, Complete, Error }

/// <summary>Container for reasoning / tool-call trace lines. Matches the registry's <c>trace</c> slot.</summary>
public sealed partial class Trace : Component
{
    const string RootClass =
        "flex w-full min-w-0 flex-col gap-2 " +
        "*:has-[>[data-slot=trace-line]]:flex *:has-[>[data-slot=trace-line]]:w-fit " +
        "*:has-[>[data-slot=trace-line]]:max-w-full *:has-[>[data-slot=trace-line]]:min-w-0 " +
        "*:has-[>[data-slot=trace-line]]:rounded-lg *:has-[>[data-slot=trace-line]]:text-left " +
        "*:has-[>[data-slot=trace-line]]:outline-none *:has-[>[data-slot=trace-line]]:focus-visible:ring-3 " +
        "*:has-[>[data-slot=trace-line]]:focus-visible:ring-ring/30";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() },
        "trace", RootClass, Class, Rest);
}

/// <summary>
/// One trace line: an icon, a title and an optional description. A line on its own is plain text; wrap it in a
/// <see cref="TraceCollapsibleTrigger"/> (or a link) to make it interactive.
/// </summary>
public sealed partial class TraceLine : Component
{
    const string LineClass =
        "flex w-fit max-w-full min-w-0 items-center gap-2 text-sm text-muted-foreground transition-colors " +
        "in-[a,button]:cursor-pointer in-[a:hover,button:hover]:text-foreground " +
        "data-[status=error]:text-destructive data-[status=error]:in-[a:hover,button:hover]:text-destructive " +
        "data-[status=pending]:opacity-60 data-[status=running]:*:data-[slot=trace-title]:shimmer";

    /// <summary>The state of the work the line reports. <see cref="TraceStatusKind.Complete"/> renders no status.</summary>
    public TraceStatusKind? Status { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        // AwAUI 的 Span 是行内文本片段，flex/gap 不生效；官方用 span，这里用等价的块容器承载行布局。
        var line = StaticParts.Style(new Div { Children = Children ?? new NodeList() },
            "trace-line", LineClass, Class, Rest);
        line.Attributes["data-status"] = StatusName(Status);
        return line;
    }

    internal static string? StatusName(TraceStatusKind? status) => status switch
    {
        TraceStatusKind.Pending => "pending",
        TraceStatusKind.Running => "running",
        TraceStatusKind.Error => "error",
        _ => null,
    };
}

/// <summary>Decorative glyph in a trace line (hidden from assistive technology).</summary>
public sealed partial class TraceIcon : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var icon = StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "trace-icon",
            "flex size-4 shrink-0 items-center justify-center [&_svg]:pointer-events-none " +
            "[&_svg:not([class*='size-'])]:size-4 [[data-slot=trace-title]~&]:-ml-1", Class, Rest);
        icon.Attributes["aria-hidden"] ??= "true";
        return icon;
    }
}

/// <summary>One sentence on one line. It truncates and shimmers while the line is running.</summary>
public partial class TraceTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Span { Children = Children ?? new NodeList() },
        "trace-title", "min-w-0 truncate motion-reduce:shimmer-none", Class, Rest);
}

/// <summary>Secondary text after the title, such as a count or a duration. It does not truncate.</summary>
public partial class TraceDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Span { Children = Children ?? new NodeList() },
        "trace-description", "shrink-0 text-muted-foreground/70 tabular-nums font-mono", Class, Rest);
}

/// <summary>Legacy name for <see cref="TraceTitle"/>.</summary>
public sealed partial class TraceLabel : TraceTitle
{
}

/// <summary>Elapsed time of a trace line, such as <c>1.2s</c>. Rendered as a <see cref="TraceDescription"/>.</summary>
public sealed partial class TraceDuration : TraceDescription
{
    public string? Duration { get; set; }

    protected override Node Build() => StaticParts.Style(
        new Span { Children = Children ?? new NodeList { new UIText(Duration ?? "") } },
        "trace-description", "shrink-0 text-muted-foreground/70 tabular-nums font-mono", Class, Rest);
}

/// <summary>Status shared with the parts of one <see cref="TraceCollapsible"/>.</summary>
internal sealed record TraceItemScope(TraceStatusKind? Status, bool Open, bool Disabled, string ContentId, Action Toggle);

/// <summary>A collapsible trace row. Equivalent to the registry's <c>TraceCollapsible</c>.</summary>
public partial class TraceCollapsible : Component
{
    readonly ControllableState<bool> _open = new();
    readonly string _contentId = TabsParts.NextId("trace-collapsible");

    public string? Value { get; set; }
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public bool Disabled { get; set; }
    public TraceStatusKind? Status { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool open = _open.Sync(Open, DefaultOpen, OpenChanged + OnOpenChange);
        var root = StaticParts.Style(new Div { Children = Children ?? new NodeList() },
            "trace-collapsible", "flex w-full min-w-0 flex-col", Class, Rest);
        root.Attributes["data-state"] = TabsParts.OpenState(open);
        if (Value is not null) root.Attributes["data-value"] = Value;
        TabsParts.Disabled(root, Disabled);
        return Provide(new TraceItemScope(Status, open, Disabled, _contentId, () => _open.Set(!_open.Value)), root);
    }
}

/// <summary>Legacy name for <see cref="TraceCollapsible"/>.</summary>
public sealed partial class TraceItem : TraceCollapsible
{
}

/// <summary>Status text of a trace entry. Defaults to the containing <see cref="TraceItem"/>'s status.</summary>
public sealed partial class TraceStatus : Component
{
    public TraceStatusKind? Status { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        TraceStatusKind? status = Status;
        if (status is null && TryUse<TraceItemScope>(out var scope)) status = scope.Status;
        string? name = TraceLine.StatusName(status);
        var children = Children ?? new NodeList { new UIText(name ?? "") };
        var node = StaticParts.Style(new Span { Children = children },
            "trace-status", "text-xs font-medium text-muted-foreground", Class, Rest);
        node.Attributes["data-status"] = name;
        return node;
    }
}

/// <summary>The button that opens and closes the row. Put a <see cref="TraceLine"/> inside it.</summary>
public partial class TraceCollapsibleTrigger : Component
{
    const string TriggerClass =
        "group/trace-collapsible-trigger flex w-fit max-w-full min-w-0 cursor-pointer items-center gap-1 " +
        "rounded-lg text-left text-muted-foreground transition-colors outline-none hover:text-foreground " +
        "focus-visible:ring-3 focus-visible:ring-ring/30 has-[[data-status=error]]:text-destructive " +
        "disabled:cursor-not-allowed disabled:opacity-50";

    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<TraceItemScope>();
        bool disabled = Disabled || scope.Disabled;
        string state = TabsParts.OpenState(scope.Open);

        var children = new NodeList();
        if (Children is not null) children.AddRange(Children);
        children.Add(new TraceIcon
        {
            Class = "transition-transform group-aria-expanded/trace-collapsible-trigger:rotate-90",
            Children = [StaticParts.Glyph(LucideIcons.ChevronRight)],
        });

        var trigger = TabsParts.Style(new UIButton { Disabled = disabled, Children = children },
            "trace-collapsible-trigger", TriggerClass, Class, Rest);
        trigger.Attributes["aria-controls"] = scope.ContentId;
        trigger.Attributes["aria-expanded"] = TabsParts.Bool(scope.Open);
        trigger.Attributes["data-state"] = state;
        TabsParts.Disabled(trigger, disabled);
        TabsParts.Activate(trigger, scope.Toggle);
        return trigger;
    }
}

/// <summary>Legacy name for <see cref="TraceCollapsibleTrigger"/>.</summary>
public sealed partial class TraceTrigger : TraceCollapsibleTrigger
{
}

/// <summary>Body of a trace row; only mounted while the row is open unless <see cref="KeepMounted"/> is set.</summary>
public partial class TraceCollapsibleContent : Component
{
    public bool KeepMounted { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = Use<TraceItemScope>();
        var inner = new Div
        {
            Children = Children ?? new NodeList(),
            Class = TailwindMerge.Cn(
                "mt-2 mb-1 ml-[calc(--spacing(2)-1px)] min-w-0 border-l pr-1 pl-4 leading-relaxed " +
                "wrap-break-word text-muted-foreground",
                Class),
        };
        inner.Attributes["data-slot"] = "trace-collapsible-content-inner";

        var panel = new Div { Id = group.ContentId, Children = [inner] };
        panel.Class = "min-w-0 overflow-hidden transition-[height] duration-200";
        panel.MergeAttributes(Rest);
        panel.Attributes["data-slot"] = "trace-collapsible-content";
        panel.Attributes["data-state"] = TabsParts.OpenState(group.Open);
        TabsParts.Disabled(panel, group.Disabled);
        if (KeepMounted && !group.Open) TabsParts.Hide(panel);
        return new PresenceGate { Present = group.Open, ForceMount = KeepMounted, Children = [panel] };
    }
}

/// <summary>Legacy name for <see cref="TraceCollapsibleContent"/>.</summary>
public sealed partial class TraceContent : TraceCollapsibleContent
{
    /// <summary>Legacy alias of <see cref="TraceCollapsibleContent.KeepMounted"/>.</summary>
    public bool ForceMount { get => KeepMounted; set => KeepMounted = value; }
}
