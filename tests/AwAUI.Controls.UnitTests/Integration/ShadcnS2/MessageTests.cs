using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Message：对齐（end → data-align=end 与 flex-row-reverse）、头像 / 内容 / 页眉 / 页脚的槽位与树序，
/// 以及页脚在 end 对齐时的右对齐类。
/// </summary>
public class MessageTests
{
    static MessageTests() => ShadcnTheme.Register();

    static UIHarness Harness(System.Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    [Fact]
    public void Message_end_alignment_reverses_the_row()
    {
        var h = Harness(() => new Message
        {
            Align = MessageAlign.End,
            Children =
            [
                new MessageAvatar { Children = [new UIText("A")] },
                new MessageContent { Children = [new Bubble { Children = [new BubbleContent { Children = [new UIText("Hi")] }] }] },
            ],
        });

        var message = SlotOrThrow(h, "message");
        Assert.Equal("end", A(message, "data-align"));
        Assert.Contains("flex-row-reverse", A(message, "class")!);
        Assert.Contains("group/message", A(message, "class")!);
    }

    [Fact]
    public void Message_start_alignment_keeps_the_normal_row()
    {
        var h = Harness(() => new Message
        {
            Children = [new MessageContent { Children = [new Bubble { Children = [new BubbleContent()] }] }],
        });

        var message = SlotOrThrow(h, "message");
        Assert.Equal("start", A(message, "data-align"));
        Assert.Contains("group/message", A(message, "class")!);
    }

    [Fact]
    public void Message_header_footer_and_avatar_slots_keep_the_documented_order()
    {
        var h = Harness(() => new Message
        {
            Children =
            [
                new MessageAvatar { Children = [new UIText("A")] },
                new MessageContent
                {
                    Children =
                    [
                        new MessageHeader { Children = [new UIText("Ada")] },
                        new Bubble { Children = [new BubbleContent { Children = [new UIText("Hello")] }] },
                        new MessageFooter { Children = [new UIText("12:30")] },
                    ],
                },
            ],
        });

        var avatar = SlotOrThrow(h, "message-avatar");
        var content = SlotOrThrow(h, "message-content");
        var header = SlotOrThrow(h, "message-header");
        var footer = SlotOrThrow(h, "message-footer");

        Assert.Same(content, header.Parent);
        Assert.Same(content, footer.Parent);
        Assert.Same(avatar.Parent, content.Parent);

        var slots = h.FindAll(e => A(e, "data-slot") is "message-header" or "message-footer" or "message-content" or "message-avatar")
            .Select(e => A(e, "data-slot"))
            .ToList();
        Assert.Equal(new[] { "message-avatar", "message-content", "message-header", "message-footer" }, slots);

        Assert.Contains("group-data-[align=end]/message:justify-end", A(footer, "class")!);
        Assert.Contains("text-muted-foreground", A(header, "class")!);
    }

    [Fact]
    public void Message_group_stacks_messages()
    {
        var h = Harness(() => new MessageGroup
        {
            Children =
            [
                new Message { Children = [new MessageContent { Children = [] }] },
                new Message { Align = MessageAlign.End, Children = [new MessageContent { Children = [] }] },
            ],
        });

        var group = SlotOrThrow(h, "message-group");
        Assert.Contains("flex-col", A(group, "class")!);
        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "message").Count);
    }
}
