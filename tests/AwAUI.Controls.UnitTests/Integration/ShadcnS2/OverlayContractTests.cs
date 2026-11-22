using System;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// Overlay parts that must mirror the new-york-v4 registry: the popover title is a plain element, close
/// affordances keep the physical top-right position and add no label, and alert dialogs still report outside
/// interactions without ever dismissing themselves.
/// </summary>
public class OverlayContractTests
{
    static OverlayContractTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    [Fact]
    public void Popover_title_is_a_plain_element_without_a_heading_role()
    {
        var h = Harness(() => new Popover
        {
            DefaultOpen = true,
            Children =
            [
                new PopoverTrigger { Children = [new UIText("Open")] },
                new PopoverContent
                {
                    Children =
                    [
                        new PopoverHeader
                        {
                            Children =
                            [
                                new PopoverTitle { Children = [new UIText("Dimensions")] },
                                new PopoverDescription { Children = [new UIText("Set the dimensions for the layer.")] },
                            ],
                        },
                    ],
                },
            ],
        });

        var title = SlotOrThrow(h, "popover-title");
        Assert.Null(A(title, "role"));
        Assert.True(title.HasClass("font-medium"));
        Assert.DoesNotContain(h.FindAll(e => e.Role == Role.Heading), e => A(e, "data-slot") == "popover-title");
    }

    [Fact]
    public void Popover_close_renders_no_default_label()
    {
        var h = Harness(() => new Popover
        {
            DefaultOpen = true,
            Children =
            [
                new PopoverTrigger { Children = [new UIText("Open")] },
                new PopoverContent
                {
                    Children = [new PopoverClose { Rest = new AttributeDictionary { ["id"] = "bare-popover-close" } }],
                },
            ],
        });

        Assert.Equal("", h.ById("bare-popover-close").TextContent.Trim());
    }

    [Fact]
    public void Sheet_close_uses_the_registry_position_and_renders_no_default_label()
    {
        var h = Harness(() => new Sheet
        {
            DefaultOpen = true,
            Children =
            [
                new SheetTrigger { Children = [new UIText("Open")] },
                new SheetContent
                {
                    Children = [new SheetTitle { Children = [new UIText("Preferences")] }],
                },
            ],
        });

        var close = SlotOrThrow(h, "sheet-close");
        // The registry hard-codes right-4 (physical) rather than a logical inline edge.
        Assert.Contains("right-4", A(close, "class"));
        Assert.DoesNotContain("end-4", A(close, "class"));
        Assert.Equal("", close.TextContent.Trim());
    }

    [Fact]
    public void AlertDialog_outside_callbacks_run_but_the_dialog_never_dismisses()
    {
        int pointerOutside = 0;
        int interactOutside = 0;
        var h = Harness(() => new Div
        {
            Children =
            [
                new UIButton { Id = "outside", Children = [new UIText("Outside")] },
                new AlertDialog
                {
                    DefaultOpen = true,
                    Children =
                    [
                        new AlertDialogTrigger { Children = [new UIText("Open")] },
                        new AlertDialogContent
                        {
                            OnPointerDownOutside = _ => pointerOutside++,
                            OnInteractOutside = _ => interactOutside++,
                            Children =
                            [
                                new AlertDialogHeader
                                {
                                    Children = [new AlertDialogTitle { Children = [new UIText("Delete project?")] }],
                                },
                                new AlertDialogFooter
                                {
                                    Children = [new AlertDialogCancel(), new AlertDialogAction()],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        h.ClickAt(new Point(4, 4));

        Assert.Equal(1, pointerOutside);
        Assert.Equal(1, interactOutside);
        Assert.NotEmpty(h.FindAll(e => A(e, "role") == "alertdialog"));
    }

    [Theory]
    [InlineData(true)]   // mounted already open
    [InlineData(false)]  // opened by the trigger after mount
    public void Dialog_autofocus_selects_all_text_in_the_first_input(bool defaultOpen)
    {
        var h = Harness(() => new Dialog
        {
            DefaultOpen = defaultOpen,
            Children =
            [
                new DialogTrigger { Children = [new UIText("Rename")] },
                new DialogContent
                {
                    Children =
                    [
                        new DialogHeader
                        {
                            Children = [new DialogTitle { Children = [new UIText("Rename workspace")] }],
                        },
                        new Input { Id = "workspace-name", Value = "Design system" },
                    ],
                },
            ],
        });

        if (!defaultOpen) h.Click(SlotOrThrow(h, "dialog-trigger"));

        var input = Assert.IsType<InputElement>(h.ById("workspace-name"));
        Assert.True(input.IsFocused);
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(input.Value.Length, input.SelectionEnd);
        Assert.Equal("Design system", input.SelectedText);
    }
}
