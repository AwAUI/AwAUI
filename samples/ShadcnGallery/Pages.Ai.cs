using System;
using System.Collections.Generic;
using AwAUI.Icons.Lucide;
using AwAUI.Shadcn;
using AwAUI.UI;
using SvgDocument = AwAUI.Svg.SvgDocument;
using Text = AwAUI.UI.Text;

namespace ShadcnGallery;

/// <summary>AI building blocks: attachments, conversational bubbles, messages, markers, trace and code samples.</summary>
public static partial class AiPages
{
    public static void Register()
    {
        DocsCatalog.Add("attachment", "Attachment",
            DocsExample.Of("States", AttachmentStates),
            DocsExample.Of("Sizes", AttachmentSizes),
            DocsExample.Of("Vertical", AttachmentVertical));

        DocsCatalog.Add("bubble", "Bubble",
            DocsExample.Of("Variants", BubbleVariants),
            DocsExample.Of("Alignment", BubbleAlignment),
            DocsExample.Of("Grouping", BubbleGrouping));

        DocsCatalog.Add("marker", "Marker",
            DocsExample.Of("Variants", MarkerVariants),
            DocsExample.Of("Conversation", MarkerConversation));

        DocsCatalog.Add("message", "Message",
            DocsExample.Of("Conversation", MessageConversation),
            DocsExample.Of("Full conversation", () => new AiConversationDemo()),
            DocsExample.Of("Bubble content", MessageWithBubble),
            DocsExample.Of("Grouped turns", MessageGroupedTurns));

        DocsCatalog.Add("message-scroller", "Message Scroller",
            DocsExample.Of("Basic", ScrollerBasic),
            DocsExample.Of("Transcript", ScrollerTranscript),
            DocsExample.Of("Scroll to latest", () => new AiScrollerLatestDemo()));

        DocsCatalog.Add("questionnaire", "Questionnaire",
            DocsExample.Of("Basic", () => new AiQuestionnaireDemo()),
            DocsExample.Of("Skippable", QuestionnaireSkippable),
            DocsExample.Of("Validation and skip", () => new AiQuestionnaireValidationDemo()));

        DocsCatalog.Add("trace", "Trace",
            DocsExample.Of("Reasoning", TraceReasoning),
            DocsExample.Of("Tool calls", TraceToolCalls),
            DocsExample.Of("Agent run", TraceAgentRun));

        DocsCatalog.Add("code-block", "Code Block",
            DocsExample.Of("With filename", CodeWithFilename),
            DocsExample.Of("Terminal output", CodeTerminal),
            DocsExample.Of("Wrapping", CodeWrapping));
    }

    // ---------------------------------------------------------------- shared helpers

    static AwAUI.UI.Svg AiIcon(SvgDocument document, int size = 16)
    {
        var svg = new AwAUI.UI.Svg(document) { Style = "width:" + size + "px;height:" + size + "px" };
        svg.Attributes["aria-hidden"] = "true";
        return svg;
    }

    // ---------------------------------------------------------------- attachment

    static Node AttachmentStates() => DocsUi.Stack(
        AiAttachmentRow("Idle", AttachmentState.Idle, "brand-guide.pdf", "Drop to upload", LucideIcons.Circle),
        AiAttachmentRow("Uploading", AttachmentState.Uploading, "brand-guide.pdf", "Uploading 64%", LucideIcons.LoaderCircle),
        AiAttachmentRow("Processing", AttachmentState.Processing, "brand-guide.pdf", "Reading 24 pages", LucideIcons.LoaderCircle),
        AiAttachmentRow("Error", AttachmentState.Error, "brand-guide.pdf", "Upload failed, try again", LucideIcons.X),
        AiAttachmentRow("Done", AttachmentState.Done, "brand-guide.pdf", "2.4 MB, PDF", LucideIcons.Check));

    static Div AiAttachmentRow(string label, AttachmentState state, string title, string description, SvgDocument icon) =>
        DocsUi.Column(DocsUi.Label(label), AiAttachment(state, title, description, icon));

    static Attachment AiAttachment(AttachmentState state, string title, string description, SvgDocument icon,
        AttachmentSize size = AttachmentSize.Default) => new()
    {
        State = state,
        Size = size,
        Children =
        [
            new AttachmentMedia { Variant = AttachmentMediaVariant.Icon, Children = [AiIcon(icon)] },
            new AttachmentContent
            {
                Children =
                [
                    new AttachmentTitle { Children = [new Text { Content = title }] },
                    new AttachmentDescription { Children = [new Text { Content = description }] },
                ],
            },
            new AttachmentActions { Children = [new AttachmentAction { Children = [AiIcon(LucideIcons.X, 14)] }] },
        ],
    };

    static Node AttachmentSizes() => DocsUi.Stack(
        DocsUi.Column(DocsUi.Label("Default"), AiAttachment(AttachmentState.Done, "spec.pdf", "1.8 MB, PDF", LucideIcons.Check)),
        DocsUi.Column(DocsUi.Label("Small"), AiAttachment(AttachmentState.Done, "spec.pdf", "1.8 MB, PDF", LucideIcons.Check, AttachmentSize.Sm)),
        DocsUi.Column(DocsUi.Label("Extra small"), AiAttachment(AttachmentState.Done, "spec.pdf", "1.8 MB, PDF", LucideIcons.Check, AttachmentSize.Xs)));

    static Node AttachmentVertical() => DocsUi.Stack(
        DocsUi.Row(
            AiVerticalAttachment("cover.png", "1.2 MB", LucideIcons.X),
            AiVerticalAttachment("chart.png", "840 KB", LucideIcons.Check),
            AiVerticalAttachment("notes.png", "310 KB", LucideIcons.Plus)),
        DocsUi.Note("Vertical attachments hold their media above the title, so they work in compact grids and composer trays."));

    static Attachment AiVerticalAttachment(string title, string description, SvgDocument icon) => new()
    {
        Orientation = AttachmentOrientation.Vertical,
        State = AttachmentState.Done,
        Children =
        [
            new AttachmentMedia
            {
                Variant = AttachmentMediaVariant.Image,
                Children =
                [
                    new Div { Style = "width:100%;height:100%;background:linear-gradient(135deg,var(--chart-1),var(--chart-3))" },
                ],
            },
            new AttachmentContent
            {
                Children =
                [
                    new AttachmentTitle { Children = [new Text { Content = title }] },
                    new AttachmentDescription { Children = [new Text { Content = description }] },
                ],
            },
            new AttachmentActions { Children = [new AttachmentAction { Children = [AiIcon(icon, 14)] }] },
        ],
    };

    // ---------------------------------------------------------------- bubble

    static Node BubbleVariants() => DocsUi.Stack(
        DocsUi.Row(DocsUi.Label("Default"), AiBubble("Primary surface", BubbleVariant.Default)),
        DocsUi.Row(DocsUi.Label("Secondary"), AiBubble("Secondary surface", BubbleVariant.Secondary)),
        DocsUi.Row(DocsUi.Label("Muted"), AiBubble("Muted surface", BubbleVariant.Muted)),
        DocsUi.Row(DocsUi.Label("Tinted"), AiBubble("Tinted surface", BubbleVariant.Tinted)),
        DocsUi.Row(DocsUi.Label("Outline"), AiBubble("Outline surface", BubbleVariant.Outline)),
        DocsUi.Row(DocsUi.Label("Ghost"), AiBubble("Ghost surface", BubbleVariant.Ghost)),
        DocsUi.Row(DocsUi.Label("Destructive"), AiBubble("Destructive surface", BubbleVariant.Destructive)));

    static Bubble AiBubble(string text, BubbleVariant variant = BubbleVariant.Default, BubbleAlign align = BubbleAlign.Start) => new()
    {
        Variant = variant,
        Align = align,
        Children = [new BubbleContent { Children = [new Text { Content = text }] }],
    };

    static Node BubbleAlignment() => DocsUi.Stack(
        DocsUi.Column(
            DocsUi.Label("Start"),
            new BubbleGroup { Children = [AiBubble("Can we ship the new transcript view?", BubbleVariant.Secondary)] }),
        DocsUi.Column(
            DocsUi.Label("End"),
            new BubbleGroup { Children = [AiBubble("Yes, right after the design review.", BubbleVariant.Default, BubbleAlign.End)] }),
        DocsUi.Note("Bubbles align to the start or end of their BubbleGroup column."));

    static Node BubbleGrouping()
    {
        var reacted = new Bubble
        {
            Variant = BubbleVariant.Default,
            Align = BubbleAlign.End,
            Children =
            [
                new BubbleContent { Children = [new Text { Content = "Perfect, tagging the release now." }] },
                new BubbleReactions
                {
                    Side = BubbleSide.Bottom,
                    Align = BubbleAlign.End,
                    Children = [new Text { Content = "👍 2" }],
                },
            ],
        };

        return DocsUi.Stack(
            new BubbleGroup
            {
                Children =
                [
                    AiBubble("The build is green.", BubbleVariant.Secondary),
                    AiBubble("Both renderers passed the snapshot suite.", BubbleVariant.Secondary),
                    reacted,
                ],
            },
            DocsUi.Muted("Consecutive bubbles share one column, and a reaction badge anchors to a corner."));
    }

    // ---------------------------------------------------------------- marker

    static Marker AiMarker(MarkerVariant variant, SvgDocument? icon, string text)
    {
        var children = new NodeList();
        if (icon is not null) children.Add(new MarkerIcon { Children = [AiIcon(icon)] });
        children.Add(new MarkerContent { Children = [new Text { Content = text }] });
        return new Marker { Variant = variant, Children = children };
    }

    static Node MarkerVariants() => DocsUi.Stack(
        AiMarker(MarkerVariant.Default, LucideIcons.Circle, "Model switched to Claude"),
        AiMarker(MarkerVariant.Separator, null, "Today"),
        AiMarker(MarkerVariant.Border, LucideIcons.Check, "Conversation saved"));

    static Node MarkerConversation() => DocsUi.Panel(
        AiMarker(MarkerVariant.Separator, null, "Earlier"),
        AiBubble("Summarize the design review.", BubbleVariant.Secondary),
        AiBubble("Three decisions and one open question.", BubbleVariant.Default, BubbleAlign.End),
        AiMarker(MarkerVariant.Border, LucideIcons.Search, "Searching 4 documents"));

    // ---------------------------------------------------------------- message

    static Avatar AiAvatar(string initials, string name) => new()
    {
        Rest = new AttributeDictionary { ["aria-label"] = name },
        Children = [new AvatarFallback { Children = [new Text { Content = initials }] }],
    };

    static Message AiMessage(MessageAlign align, string initials, string name, string body, string? footer = null)
    {
        var content = new NodeList
        {
            new MessageHeader { Children = [new Text { Content = name }] },
            new Bubble
            {
                Align = align == MessageAlign.End ? BubbleAlign.End : BubbleAlign.Start,
                Variant = align == MessageAlign.End ? BubbleVariant.Default : BubbleVariant.Secondary,
                Children = [new BubbleContent { Children = [new Text { Content = body }] }],
            },
        };
        if (footer is not null) content.Add(new MessageFooter { Children = [new Text { Content = footer }] });

        return new Message
        {
            Align = align,
            Children =
            [
                new MessageAvatar { Children = [AiAvatar(initials, name)] },
                new MessageContent { Children = content },
            ],
        };
    }

    static Node MessageConversation() => DocsUi.Stack(
        new MessageGroup
        {
            Children =
            [
                AiMessage(MessageAlign.Start, "AR", "Alex Rivera", "The scroller now pins to the end while tokens stream.", "10:24"),
                AiMessage(MessageAlign.End, "YU", "You", "Does the jump button hide when I am already at the bottom?", "10:25"),
                AiMessage(MessageAlign.Start, "AR", "Alex Rivera", "Yes, it fades out and leaves the transcript clean.", "10:26"),
            ],
        });

    static Node MessageWithBubble() => DocsUi.Stack(
        new MessageGroup
        {
            Children =
            [
                new Message
                {
                    Align = MessageAlign.Start,
                    Children =
                    [
                        new MessageAvatar { Children = [AiAvatar("AR", "Alex Rivera")] },
                        new MessageContent
                        {
                            Children =
                            [
                                new MessageHeader { Children = [new Text { Content = "Alex Rivera" }] },
                                new Bubble
                                {
                                    Variant = BubbleVariant.Secondary,
                                    Children =
                                    [
                                        new BubbleContent { Children = [new Text { Content = "The transcript is attached. Jump to any turn to replay it." }] },
                                        new BubbleReactions
                                        {
                                            Side = BubbleSide.Bottom,
                                            Align = BubbleAlign.Start,
                                            Children = [new Text { Content = "👍 3" }],
                                        },
                                    ],
                                },
                                new MessageFooter { Children = [new Text { Content = "10:31, delivered" }] },
                            ],
                        },
                    ],
                },
            ],
        },
        DocsUi.Note("MessageContent stacks a header, a Bubble with reactions, and a footer; alignment is set on the Message."));

    static Node MessageGroupedTurns() => DocsUi.Stack(
        new MessageGroup
        {
            Children =
            [
                AiMessage(MessageAlign.End, "YU", "You", "Can you summarize the review?", "10:40"),
                AiMessage(MessageAlign.Start, "AR", "Alex Rivera", "Three decisions and one open question.", "10:41"),
                AiMessage(MessageAlign.Start, "AR", "Alex Rivera", "The open question is the release date.", "10:41"),
            ],
        });

    static MessageHeader AiMessageHeader(string name, string time) => new()
    {
        Class = "gap-2",
        Children =
        [
            new Text { Content = name },
            new Text { Class = "text-muted-foreground/70 tabular-nums", Content = time },
        ],
    };

    static AwAUI.Shadcn.Button AiGhostAction(string label, SvgDocument? icon, Action<PointerEvent>? onClick)
    {
        var children = new NodeList();
        if (icon is not null) children.Add(AiIcon(icon, 14));
        children.Add(new TextNode(label));
        return new AwAUI.Shadcn.Button
        {
            Variant = ButtonVariant.Ghost,
            Size = ButtonSize.Sm,
            OnClick = onClick,
            Children = children,
        };
    }

    /// <summary>A complete exchange: attachment, both speakers, actions, reactions and timestamps.</summary>
    sealed partial class AiConversationDemo : Component
    {
        readonly State<string> _status = new("The action row is wired to this example.");

        protected override Node Build() => DocsUi.Stack(
            new MessageGroup
            {
                Children =
                [
                    new Marker
                    {
                        Variant = MarkerVariant.Separator,
                        Children = [new MarkerContent { Children = [new Text { Content = "Today" }] }],
                    },
                    new Message
                    {
                        Align = MessageAlign.Start,
                        Children =
                        [
                            new MessageAvatar { Children = [AiAvatar("AR", "Alex Rivera")] },
                            new MessageContent
                            {
                                Children =
                                [
                                    AiMessageHeader("Alex Rivera", "10:24"),
                                    new AttachmentGroup
                                    {
                                        Children =
                                        [
                                            AiAttachment(AttachmentState.Done, "launch-brief.pdf", "2.4 MB, PDF", LucideIcons.Check),
                                        ],
                                    },
                                    new Bubble
                                    {
                                        Variant = BubbleVariant.Secondary,
                                        Children =
                                        [
                                            new BubbleContent
                                            {
                                                Children = [new Text { Content = "Here is the launch brief. The two metrics on page 3 are the ones to review." }],
                                            },
                                        ],
                                    },
                                    new MessageFooter
                                    {
                                        Class = "gap-3",
                                        Children =
                                        [
                                            new Div
                                            {
                                                Class = "flex items-center gap-1",
                                                Children =
                                                [
                                                    AiGhostAction("Copy", LucideIcons.Copy,
                                                        _ => _status.Value = "Copy pressed: the reply is on the clipboard."),
                                                    AiGhostAction("Regenerate", LucideIcons.RefreshCw,
                                                        _ => _status.Value = "Regenerate pressed: generating a new reply from the same attachment."),
                                                    AiGhostAction("Helpful", LucideIcons.ThumbsUp,
                                                        _ => _status.Value = "Helpful pressed: positive feedback recorded for this reply."),
                                                    AiGhostAction("Not helpful", LucideIcons.ThumbsDown,
                                                        _ => _status.Value = "Not helpful pressed: negative feedback recorded for this reply."),
                                                ],
                                            },
                                            new Text { Class = "tabular-nums", Content = "10:24" },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                    new Message
                    {
                        Align = MessageAlign.End,
                        Children =
                        [
                            new MessageAvatar { Children = [AiAvatar("YU", "You")] },
                            new MessageContent
                            {
                                Children =
                                [
                                    AiMessageHeader("You", "10:25"),
                                    new Bubble
                                    {
                                        Variant = BubbleVariant.Default,
                                        Align = BubbleAlign.End,
                                        Children =
                                        [
                                            new BubbleContent
                                            {
                                                Children = [new Text { Content = "Thanks. I will check the numbers after standup and reply here." }],
                                            },
                                        ],
                                    },
                                    new MessageFooter
                                    {
                                        Children = [new Text { Class = "tabular-nums", Content = "Delivered 10:25" }],
                                    },
                                ],
                            },
                        ],
                    },
                    new Message
                    {
                        Align = MessageAlign.Start,
                        Children =
                        [
                            new MessageAvatar { Children = [AiAvatar("AR", "Alex Rivera")] },
                            new MessageContent
                            {
                                Children =
                                [
                                    AiMessageHeader("Alex Rivera", "10:26"),
                                    new Bubble
                                    {
                                        Variant = BubbleVariant.Secondary,
                                        Children =
                                        [
                                            new BubbleContent
                                            {
                                                Children = [new Text { Content = "Perfect. I highlighted the retention and activation lines so they are easy to find." }],
                                            },
                                        ],
                                    },
                                    new MessageFooter
                                    {
                                        Class = "gap-3",
                                        Children =
                                        [
                                            new Div
                                            {
                                                Class = "flex items-center gap-1",
                                                Children =
                                                [
                                                    AiGhostAction("Copy", LucideIcons.Copy,
                                                        _ => _status.Value = "Copy pressed: the last reply is on the clipboard."),
                                                    AiGhostAction("Regenerate", LucideIcons.RefreshCw,
                                                        _ => _status.Value = "Regenerate pressed: generating a new version of the last reply."),
                                                    AiGhostAction("Helpful", LucideIcons.ThumbsUp,
                                                        _ => _status.Value = "Helpful pressed: positive feedback recorded for the last reply."),
                                                    AiGhostAction("Not helpful", LucideIcons.ThumbsDown,
                                                        _ => _status.Value = "Not helpful pressed: negative feedback recorded for the last reply."),
                                                    AiGhostAction("More", LucideIcons.MoreHorizontal,
                                                        _ => _status.Value = "More pressed: pin, quote, or report this reply."),
                                                ],
                                            },
                                            new Text { Class = "tabular-nums", Content = "10:26" },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
            DocsUi.Muted(_status.Value));
    }

    // ---------------------------------------------------------------- message scroller

    static MessageScrollerItem AiScrollerItem(MessageAlign align, string initials, string name, string body, bool anchor = false) => new()
    {
        ScrollAnchor = anchor,
        Children = [AiMessage(align, initials, name, body)],
    };

    static Node ScrollerBasic()
    {
        var items = new NodeList
        {
            AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera", "Opening the saved transcript.", anchor: true),
            AiScrollerItem(MessageAlign.End, "YU", "You", "Scroll up and the jump button appears."),
            AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera", "New tokens keep the view pinned while you are at the end."),
            AiScrollerItem(MessageAlign.End, "YU", "You", "The fade at the top makes long threads easier to scan."),
            AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera", "Anchored turns survive history loads without a jump."),
            AiScrollerItem(MessageAlign.End, "YU", "You", "Nice, ship it."),
        };

        return new MessageScroller
        {
            Class = "demo-panel",
            Rest = new AttributeDictionary { ["style"] = "height:280px;padding:0" },
            Children =
            [
                new MessageScrollerViewport
                {
                    Children = [new MessageScrollerContent { Children = items }],
                },
                new MessageScrollerButton { Direction = MessageScrollerDirection.End },
            ],
        };
    }

    static Node ScrollerTranscript() => DocsUi.Stack(
        new MessageScrollerProvider
        {
            AutoFollow = true,
            Children =
            [
                new MessageScroller
                {
                    Class = "demo-panel",
                    Rest = new AttributeDictionary { ["style"] = "height:280px;padding:0" },
                    Children =
                    [
                        new MessageScrollerViewport
                        {
                            Children =
                            [
                                new MessageScrollerContent
                                {
                                    Children =
                                    [
                                        new MessageScrollerItem
                                        {
                                            Children = [AiMarker(MarkerVariant.Separator, null, "Tuesday")],
                                        },
                                        AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera", "Imported the older thread."),
                                        AiScrollerItem(MessageAlign.End, "YU", "You", "Great, scroll to the top to check the history."),
                                        new MessageScrollerItem
                                        {
                                            Children = [AiMarker(MarkerVariant.Separator, null, "Today")],
                                        },
                                        AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera", "The latest answer is anchored at the bottom.", anchor: true),
                                    ],
                                },
                            ],
                        },
                        new MessageScrollerButton { Direction = MessageScrollerDirection.Start },
                        new MessageScrollerButton { Direction = MessageScrollerDirection.End },
                    ],
                },
            ],
        },
        DocsUi.Muted("Start and end buttons share the scroller's scroll state and disable themselves at each edge."));

    /// <summary>Appends turns and relies on AutoFollow to keep the viewport pinned to the latest message.</summary>
    sealed partial class AiScrollerLatestDemo : Component
    {
        readonly State<int> _replies = new(1);

        protected override Node Build()
        {
            int replies = _replies.Value;
            var items = new NodeList
            {
                AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera",
                    "Here is the launch checklist. I will post an update after each step.", anchor: true),
                AiScrollerItem(MessageAlign.End, "YU", "You",
                    "Great, keep them coming. I am watching this thread."),
            };
            for (int i = 1; i <= replies; i++)
            {
                items.Add(AiScrollerItem(MessageAlign.Start, "AR", "Alex Rivera",
                    "Update " + i + ": the build is still green and the checklist is on track."));
            }

            return DocsUi.Stack(
                DocsUi.Row(
                    DocsUi.Action("Send a reply", _ => _replies.Value = _replies.Peek() + 1),
                    DocsUi.Muted((replies == 1 ? "1 reply" : replies + " replies") + " appended")),
                new MessageScrollerProvider
                {
                    AutoFollow = true,
                    Children =
                    [
                        new MessageScroller
                        {
                            Class = "demo-panel",
                            Rest = new AttributeDictionary { ["style"] = "height:300px;padding:0" },
                            Children =
                            [
                                new MessageScrollerViewport
                                {
                                    Children = [new MessageScrollerContent { Children = items }],
                                },
                                new MessageScrollerButton { Direction = MessageScrollerDirection.Start },
                                new MessageScrollerButton { Direction = MessageScrollerDirection.End },
                            ],
                        },
                    ],
                },
                DocsUi.Note("Send appends a turn. AutoFollow keeps the viewport pinned to the end while it is already at the latest message; scrolling up stops the follow, leaves the new turn unread and reveals the Scroll to end button."));
        }
    }

    // ---------------------------------------------------------------- questionnaire

    static IReadOnlyList<QuestionnaireQuestion> AiQuestions(bool skippable = false, bool requiredFreeform = false) => new List<QuestionnaireQuestion>
    {
        new()
        {
            Id = "goal",
            Prompt = "What are you building?",
            Description = "Choose a direction, or answer the next questions to refine it.",
            Kind = QuestionnaireQuestionKind.SingleChoice,
            Options =
            [
                new QuestionnaireOption { Value = "assistant", Label = "A chat assistant" },
                new QuestionnaireOption { Value = "copilot", Label = "An in-app copilot" },
                new QuestionnaireOption { Value = "agent", Label = "A background agent" },
            ],
        },
        new()
        {
            Id = "surfaces",
            Prompt = "Where should messages appear?",
            Description = "Select every surface that should render the conversation.",
            Kind = QuestionnaireQuestionKind.MultipleChoice,
            Skippable = skippable,
            Options =
            [
                new QuestionnaireOption { Value = "sidebar", Label = "Sidebar panel" },
                new QuestionnaireOption { Value = "inline", Label = "Inline in the page" },
                new QuestionnaireOption { Value = "dialog", Label = "Dialog or sheet" },
            ],
        },
        new()
        {
            Id = "notes",
            Prompt = "Anything else we should know?",
            Description = "Share constraints, tone, or examples.",
            Kind = QuestionnaireQuestionKind.Freeform,
            Skippable = skippable && !requiredFreeform,
            Placeholder = "Share constraints, tone, or examples…",
        },
    };

    static Node QuestionnaireSkippable() => DocsUi.Stack(
        new Questionnaire { Questions = AiQuestions(skippable: true) },
        DocsUi.Note("Optional questions show a Skip action; the first question still needs an answer before Next."));

    sealed partial class AiQuestionnaireDemo : Component
    {
        readonly State<string> _status = new("No answers submitted yet.");

        protected override Node Build() => DocsUi.Stack(
            new Questionnaire
            {
                Questions = AiQuestions(),
                Submitted = OnSubmitted,
            },
            DocsUi.Panel(
                DocsUi.Label("Submitted answers"),
                DocsUi.Muted(_status.Value)));

        void OnSubmitted(IReadOnlyDictionary<string, string> answers)
        {
            if (answers.Count == 0)
            {
                _status.Value = "Submitted with no answers.";
                return;
            }

            var parts = new List<string>(answers.Count);
            foreach (var pair in answers) parts.Add(pair.Key + " = " + pair.Value);
            _status.Value = string.Join(" · ", parts);
        }
    }

    /// <summary>Required and optional questions side by side: required ones validate, optional ones offer Skip.</summary>
    sealed partial class AiQuestionnaireValidationDemo : Component
    {
        readonly State<string> _status = new("Nothing submitted yet.");

        protected override Node Build() => DocsUi.Stack(
            new Questionnaire
            {
                Questions = AiQuestions(skippable: true, requiredFreeform: true),
                Submitted = OnSubmitted,
            },
            DocsUi.Panel(
                DocsUi.Label("Submitted answers"),
                DocsUi.Muted(_status.Value)),
            DocsUi.Note("The first and last questions are required: Next and Submit stay disabled, and the field exposes data-slot=questionnaire-error until it has an answer. The middle question is optional, so it shows Skip instead of blocking the flow."));

        void OnSubmitted(IReadOnlyDictionary<string, string> answers)
        {
            if (answers.Count == 0)
            {
                _status.Value = "Submitted with no answers.";
                return;
            }

            var parts = new List<string>(answers.Count);
            foreach (var pair in answers) parts.Add(pair.Key + " = " + pair.Value);
            _status.Value = string.Join(" · ", parts);
        }
    }

    // ---------------------------------------------------------------- trace

    static TraceCollapsible AiTraceItem(string label, TraceStatusKind status, string duration, SvgDocument icon,
        string body, bool open = false, string? value = null, bool disabled = false) => new()
    {
        DefaultOpen = open,
        Value = value,
        Disabled = disabled,
        Children =
        [
            new TraceCollapsibleTrigger
            {
                Children =
                [
                    new TraceLine
                    {
                        Status = status,
                        Children =
                        [
                            new TraceIcon { Children = [AiIcon(icon)] },
                            new TraceTitle { Children = [new Text { Content = label }] },
                            new TraceDescription { Children = [new Text { Content = duration }] },
                        ],
                    },
                ],
            },
            new TraceCollapsibleContent { Children = [new Text { Content = body }] },
        ],
    };

    static Node TraceReasoning() => DocsUi.Stack(
        new Trace
        {
            Children =
            [
                AiTraceItem("Reasoning", TraceStatusKind.Complete, "1.8s", LucideIcons.Check,
                    "Compared the two API shapes and kept the uncontrolled default.", open: true),
                AiTraceItem("Drafting the answer", TraceStatusKind.Running, "0.6s", LucideIcons.LoaderCircle,
                    "Streaming the summary into the bubble.", open: true),
                new TraceCollapsible
                {
                    Disabled = true,
                    Value = "verify-citations",
                    Children =
                    [
                        new TraceCollapsibleTrigger
                        {
                            Children =
                            [
                                new TraceLine
                                {
                                    Status = TraceStatusKind.Pending,
                                    Children =
                                    [
                                        new TraceIcon { Children = [AiIcon(LucideIcons.Circle)] },
                                        new TraceTitle { Children = [new Text { Content = "Verify citations" }] },
                                        new TraceDescription { Children = [new Text { Content = "-" }] },
                                    ],
                                },
                            ],
                        },
                        new TraceCollapsibleContent { Children = [new Text { Content = "Queued behind the current step." }] },
                    ],
                },
            ],
        });

    static Node TraceToolCalls() => DocsUi.Stack(
        new Trace
        {
            Children =
            [
                AiTraceItem("search_docs", TraceStatusKind.Complete, "0.9s", LucideIcons.Search,
                    "Found 4 matching sections in the design docs.", open: true, value: "search_docs"),
                AiTraceItem("run_tests", TraceStatusKind.Error, "2.1s", LucideIcons.X,
                    "Two tests failed in AwAUI.Css. The failing assertion is listed in the log.", open: true, value: "run_tests"),
                AiTraceItem("write_file", TraceStatusKind.Complete, "0.2s", LucideIcons.Check,
                    "Updated MessageScroller.cs.", value: "write_file"),
            ],
        },
        DocsUi.Note("TraceLine exposes its status as data-status, and running titles shimmer. Expand a line to read its detail."));

    /// <summary>One agent turn: reasoning first, then the tool calls it made, with running and queued work.</summary>
    static Node TraceAgentRun() => DocsUi.Stack(
        DocsUi.Column(
            DocsUi.Label("Reasoning"),
            new Trace
            {
                Children =
                [
                    AiTraceItem("Read the request", TraceStatusKind.Complete, "0.3s", LucideIcons.CircleCheck,
                        "The user wants a summary of the launch brief and the two riskiest numbers.", open: true),
                    AiTraceItem("Plan the work", TraceStatusKind.Complete, "0.2s", LucideIcons.Info,
                        "Search the brief, read the metric table, then check the numbers before summarizing."),
                ],
            }),
        DocsUi.Column(
            DocsUi.Label("Tool calls"),
            new Trace
            {
                Children =
                [
                    AiTraceItem("search_docs(\"launch brief\")", TraceStatusKind.Complete, "0.9s", LucideIcons.Search,
                        "Matched 3 sections: overview, retention and activation.", open: true, value: "search"),
                    AiTraceItem("read_file(\"launch-brief.pdf\")", TraceStatusKind.Complete, "0.4s", LucideIcons.CircleCheck,
                        "Read 12 pages and extracted the metric table.", value: "read"),
                    AiTraceItem("web_fetch(\"benchmarks\")", TraceStatusKind.Error, "2.1s", LucideIcons.TriangleAlert,
                        "The host timed out, so the step fell back to the cached copy.", value: "fetch"),
                    AiTraceItem("run_checks()", TraceStatusKind.Running, "1.2s", LucideIcons.LoaderCircle,
                        "Recalculating retention and activation from the raw rows.", open: true, value: "checks"),
                    AiTraceItem("summarize()", TraceStatusKind.Pending, "-", LucideIcons.Circle,
                        "Queued behind run_checks.", value: "summary", disabled: true),
                ],
            }),
        DocsUi.Note("Complete rows keep a quiet check, the running row shimmers with a spinner, the errored row turns destructive, and the queued row stays disabled. Expand any row to read what that step did."));

    // ---------------------------------------------------------------- code block

    static Node CodeWithFilename() => DocsUi.Stack(
        new CodeBlock
        {
            FileName = "message.awaui",
            Language = "awaui",
            ShowLineNumbers = true,
            Code = """
                <MessageGroup>
                  <Message Align="MessageAlign.Start">
                    <MessageAvatar>
                      <Avatar><AvatarFallback>AR</AvatarFallback></Avatar>
                    </MessageAvatar>
                    <MessageContent>
                      <MessageHeader>Alex Rivera</MessageHeader>
                      <Bubble Variant="BubbleVariant.Secondary">
                        <BubbleContent>The transcript is ready.</BubbleContent>
                      </Bubble>
                      <MessageFooter>10:31, delivered</MessageFooter>
                    </MessageContent>
                  </Message>
                </MessageGroup>
                """,
        });

    static Node CodeTerminal() => DocsUi.Stack(
        new CodeBlock
        {
            Language = "bash",
            ShowLineNumbers = false,
            Code = """
                $ dotnet run --project samples/ShadcnGallery
                Building ShadcnGallery for macOS...
                Loaded 8 AI pages
                Gallery ready on http://localhost:5173
                """,
        },
        DocsUi.Muted("Without a file name the header shows only the language and the copy action."));

    static Node CodeWrapping() => DocsUi.Stack(
        new CodeBlock
        {
            FileName = "TranscriptBuilder.cs",
            Language = "csharp",
            ShowLineNumbers = true,
            Wrap = true,
            Code = """
                public static Node BuildTranscript(IReadOnlyList<TranscriptTurn> turns, bool followLatest)
                {
                    var items = new NodeList();
                    foreach (var turn in turns)
                        items.Add(new MessageScrollerItem { ScrollAnchor = turn.IsLatest, Children = [BuildMessage(turn)] });
                    var scroller = new MessageScroller { Children = [new MessageScrollerViewport { Children = [new MessageScrollerContent { Children = items }] }] };
                    return new MessageScrollerProvider { AutoFollow = followLatest, Children = [scroller] };
                }
                """,
        },
        DocsUi.Muted("Wrap keeps a long signature readable inside a 720px page instead of scrolling horizontally."));
}
