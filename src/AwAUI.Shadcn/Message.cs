using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Side of the conversation a message sits on.</summary>
public enum MessageAlign { Start, End }

/// <summary>A column of conversational messages.</summary>
public sealed partial class MessageGroup : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-group",
        "flex min-w-0 flex-col gap-2", Class, Rest);
}

/// <summary>A message row: avatar, content, and optional header and footer. Align it to the start or end of the transcript.</summary>
public sealed partial class Message : Component
{
    public MessageAlign Align { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var message = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message",
            "group/message relative flex w-full min-w-0 gap-2 text-sm data-[align=end]:flex-row-reverse", Class, Rest);
        message.Attributes["data-align"] ??= Align == MessageAlign.End ? "end" : "start";
        return message;
    }
}

/// <summary>The sender's avatar.</summary>
public sealed partial class MessageAvatar : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-avatar",
        "flex w-fit min-w-8 shrink-0 items-center justify-center self-end overflow-hidden rounded-full bg-muted " +
        "group-has-data-[slot=message-footer]/message:-translate-y-8", Class, Rest);
}

/// <summary>The message's content column.</summary>
public sealed partial class MessageContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-content",
        "flex w-full min-w-0 flex-col gap-2.5 wrap-break-word " +
        "group-data-[align=end]/message:*:data-slot:self-end", Class, Rest);
}

/// <summary>The sender name and timestamp above a message.</summary>
public sealed partial class MessageHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-header",
        "flex max-w-full min-w-0 items-center px-3 text-xs font-medium text-muted-foreground " +
        "group-has-data-[variant=ghost]/message:px-0", Class, Rest);
}

/// <summary>The actions and metadata below a message.</summary>
public sealed partial class MessageFooter : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "message-footer",
        "flex max-w-full min-w-0 items-center px-3 text-xs font-medium text-muted-foreground " +
        "group-has-data-[variant=ghost]/message:px-0 group-data-[align=end]/message:justify-end", Class, Rest);
}
