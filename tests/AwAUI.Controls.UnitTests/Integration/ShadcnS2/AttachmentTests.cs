using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Attachment：状态 / 尺寸 / 方向属性、各部分 slot、媒体变体、附件组的横向滚动布局，
/// 以及可点击的 AttachmentAction（复用 Button，默认 ghost / icon-xs）。
/// </summary>
public class AttachmentTests
{
    static AttachmentTests() => ShadcnTheme.Register();

    static UIHarness Harness(System.Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    [Fact]
    public void Attachment_renders_state_size_orientation_and_all_parts()
    {
        var h = Harness(() => new Attachment
        {
            State = AttachmentState.Processing,
            Size = AttachmentSize.Sm,
            Orientation = AttachmentOrientation.Vertical,
            Children =
            [
                new AttachmentMedia { Children = [new UIText("PDF")] },
                new AttachmentContent
                {
                    Children =
                    [
                        new AttachmentTitle { Children = [new UIText("report.pdf")] },
                        new AttachmentDescription { Children = [new UIText("2.4 MB")] },
                    ],
                },
                new AttachmentActions
                {
                    Children = [new AttachmentAction { Children = [new UIText("Remove")] }],
                },
            ],
        });

        var attachment = SlotOrThrow(h, "attachment");
        Assert.Equal("processing", A(attachment, "data-state"));
        Assert.Equal("sm", A(attachment, "data-size"));
        Assert.Equal("vertical", A(attachment, "data-orientation"));
        Assert.Contains("group/attachment", A(attachment, "class")!);
        Assert.Contains("flex-col", A(attachment, "class")!);

        var media = SlotOrThrow(h, "attachment-media");
        Assert.Equal("icon", A(media, "data-variant"));

        Assert.Equal("report.pdf", SlotOrThrow(h, "attachment-title").TextContent.Trim());
        Assert.Equal("2.4 MB", SlotOrThrow(h, "attachment-description").TextContent.Trim());
        Assert.NotNull(SlotOrThrow(h, "attachment-content"));
        Assert.NotNull(SlotOrThrow(h, "attachment-actions"));
        Assert.NotNull(SlotOrThrow(h, "attachment-action"));
    }

    [Fact]
    public void Attachment_idle_state_uses_dashed_border_and_image_media_is_marked()
    {
        var h = Harness(() => new Attachment
        {
            State = AttachmentState.Idle,
            Size = AttachmentSize.Xs,
            Children =
            [
                new AttachmentMedia { Variant = AttachmentMediaVariant.Image, Children = [new UIText("img")] },
                new AttachmentContent { Children = [new AttachmentTitle { Children = [new UIText("photo.png")] }] },
            ],
        });

        var attachment = SlotOrThrow(h, "attachment");
        Assert.Equal("idle", A(attachment, "data-state"));
        Assert.Equal("xs", A(attachment, "data-size"));
        Assert.Equal("horizontal", A(attachment, "data-orientation"));
        Assert.Contains("data-[state=idle]:border-dashed", A(attachment, "class")!);

        var media = SlotOrThrow(h, "attachment-media");
        Assert.Equal("image", A(media, "data-variant"));
        Assert.Contains("opacity-60", A(media, "class")!);
    }

    [Fact]
    public void Attachment_action_is_a_ghost_icon_button_that_clicks()
    {
        bool clicked = false;
        var h = Harness(() => new Attachment
        {
            Children =
            [
                new AttachmentContent { Children = [new AttachmentTitle { Children = [new UIText("notes.txt")] }] },
                new AttachmentActions
                {
                    Children =
                    [
                        new AttachmentAction { OnClick = _ => clicked = true, Children = [new UIText("x")] },
                    ],
                },
            ],
        });

        var action = SlotOrThrow(h, "attachment-action");
        Assert.IsType<ButtonElement>(action);
        Assert.True(action.IsFocusable);
        Assert.Equal("ghost", A(action, "data-variant"));
        Assert.Equal("icon-xs", A(action, "data-size"));

        h.Click(action);

        Assert.True(clicked);
    }

    [Fact]
    public void Attachment_trigger_is_a_button_unless_rendered_as_child()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Attachment
                {
                    Children = [new AttachmentTrigger { Children = [new UIText("Open")] }],
                },
                new Attachment
                {
                    Children =
                    [
                        new AttachmentTrigger
                        {
                            AsChild = true,
                            Children = [new Div { Children = [new UIText("Child")] }],
                        },
                    ],
                },
            ],
        });

        var triggers = h.FindAll(e => A(e, "data-slot") == "attachment-trigger");
        Assert.Equal(2, triggers.Count);
        Assert.Equal("button", A(triggers[0], "type"));
        Assert.Null(A(triggers[1], "type"));
    }

    [Fact]
    public void Attachment_group_scrolls_horizontally_and_marks_its_children()
    {
        var h = Harness(() => new AttachmentGroup
        {
            Children =
            [
                new Attachment { Children = [new AttachmentTitle { Children = [new UIText("a.png")] }] },
                new Attachment { Children = [new AttachmentTitle { Children = [new UIText("b.png")] }] },
            ],
        });

        var group = SlotOrThrow(h, "attachment-group");
        Assert.Contains("overflow-x-auto", A(group, "class")!);
        Assert.Contains("snap-x", A(group, "class")!);
        Assert.Equal(2, h.FindAll(e => A(e, "data-slot") == "attachment").Count);
    }
}
