using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using Side = AwAUI.Controls.Primitives.Side;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>Shadcn S1 浮层组件的键盘与指针验收：打开 / 关闭、Escape、外部指针、焦点与角色。</summary>
public class ShadcnOverlayTests
{
    static ShadcnOverlayTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static Element? Popup(UIHarness h, Role role) => h.FindAll(e => e.Role == role && e.IsVisible).FirstOrDefault();

    static bool IsInside(Element root, Element? element)
    {
        for (var e = element; e is not null; e = e.Parent)
            if (ReferenceEquals(e, root)) return true;
        return false;
    }

    // ------------------------------------------------------------ Select

    [Fact]
    public void Select_opens_with_arrow_keys_and_selects_an_item()
    {
        string? value = null;
        var h = Harness(() => new Select
        {
            ValueChanged = v => value = v,
            Children =
            [
                new SelectTrigger { Children = [new SelectValue { Placeholder = "Choose" }] },
                new SelectContent
                {
                    Children =
                    [
                        new SelectItem { Value = "projects", Children = [new UIText("Projects")] },
                        new SelectItem { Value = "activity", Children = [new UIText("Activity")] },
                    ],
                },
            ],
        });

        var trigger = h.FindAll(e => e.Role == Role.ComboBox).Single();
        Assert.Equal("false", A(trigger, "aria-expanded"));

        h.Tab();
        Assert.True(trigger.IsFocused);
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.Equal("true", A(trigger, "aria-expanded"));

        var list = h.FindAll(e => e.Role == Role.ListBox).Single();
        Assert.False(string.IsNullOrEmpty(A(list, "aria-labelledby")) || A(list, "aria-labelledby")!.Length == 0);
        var options = h.FindAll(e => e.Role == Role.Option);
        Assert.Equal(2, options.Count);

        h.Click(options[1]);
        Assert.Equal("activity", value);
        Assert.Empty(h.FindAll(e => e.Role == Role.ListBox));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void Select_disabled_ignores_keyboard_and_pointer()
    {
        var h = Harness(() => new Select
        {
            Disabled = true,
            Children =
            [
                new SelectTrigger { Children = [new SelectValue { Placeholder = "Choose" }] },
                new SelectContent { Children = [new SelectItem { Value = "one", Children = [new UIText("One")] }] },
            ],
        });

        var trigger = h.FindAll(e => e.Role == Role.ComboBox).Single();
        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        h.Click(trigger);
        Assert.Empty(h.FindAll(e => e.Role == Role.ListBox));
        Assert.Equal("true", A(trigger, "aria-disabled"));
    }

    // ------------------------------------------------------------ DropdownMenu

    [Fact]
    public void DropdownMenu_opens_from_trigger_and_runs_item_on_select()
    {
        bool selected = false;
        var h = Harness(() => new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { Children = [new UIText("Actions")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuItem { OnSelect = _ => selected = true, Children = [new UIText("Rename")] },
                        new DropdownMenuItem { Disabled = true, Children = [new UIText("Archive")] },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "dropdown-menu-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));
        h.Click(trigger);

        var menu = Popup(h, Role.Menu);
        Assert.NotNull(menu);
        Assert.Equal("true", A(trigger, "aria-expanded"));

        var items = h.FindAll(e => e.Role == Role.MenuItem);
        Assert.Equal(2, items.Count);
        h.Click(items[0]);
        Assert.True(selected);
        Assert.Null(Popup(h, Role.Menu));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    [Fact]
    public void DropdownMenu_arrow_down_opens_and_escape_closes()
    {
        var h = Harness(() => new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { Children = [new UIText("Actions")] },
                new DropdownMenuContent
                {
                    Children = [new DropdownMenuItem { Children = [new UIText("Rename")] }],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "dropdown-menu-trigger");
        h.Tab();
        Assert.True(trigger.IsFocused);
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.NotNull(Popup(h, Role.Menu));

        h.Escape();
        Assert.Null(Popup(h, Role.Menu));
        Assert.True(trigger.IsFocused);
    }

    [Fact]
    public void DropdownMenu_checkbox_item_toggles_checked_state()
    {
        bool? checkedState = null;
        var h = Harness(() => new DropdownMenu
        {
            DefaultOpen = true,
            Children =
            [
                new DropdownMenuTrigger { Children = [new UIText("Actions")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuCheckboxItem
                        {
                            DefaultChecked = true,
                            CheckedChanged = v => checkedState = v,
                            Children = [new UIText("Show activity")],
                        },
                    ],
                },
            ],
        });

        var item = h.FindAll(e => A(e, "role") == "menuitemcheckbox" || e.Role == Role.MenuItem)
            .Single(e => A(e, "aria-checked") is not null);
        Assert.Equal("true", A(item, "aria-checked"));
        h.Click(item);
        Assert.False(checkedState);
    }

    // ------------------------------------------------------------ Popover

    [Fact]
    public void Popover_opens_and_closes_with_escape()
    {
        var h = Harness(() => new Popover
        {
            Children =
            [
                new PopoverTrigger { Children = [new UIText("Details")] },
                new PopoverContent { Children = [new UIText("Workspace details")] },
            ],
        });

        var trigger = SlotOrThrow(h, "popover-trigger");
        Assert.Equal("false", A(trigger, "aria-expanded"));
        h.Click(trigger);

        var dialog = Popup(h, Role.Dialog);
        Assert.NotNull(dialog);
        Assert.Equal("true", A(trigger, "aria-expanded"));

        h.Escape();
        Assert.Null(Popup(h, Role.Dialog));
        Assert.Equal("false", A(trigger, "aria-expanded"));
    }

    // ------------------------------------------------------------ Dialog / AlertDialog / Sheet

    [Fact]
    public void Dialog_traps_focus_and_restores_it_after_escape()
    {
        var h = Harness(() => new Dialog
        {
            Children =
            [
                new DialogTrigger { Children = [new UIText("Edit profile")] },
                new DialogContent
                {
                    Children =
                    [
                        new DialogHeader
                        {
                            Children =
                            [
                                new DialogTitle { Children = [new UIText("Edit profile")] },
                                new DialogDescription { Children = [new UIText("Change the name.")] },
                            ],
                        },
                        new Input { Id = "name" },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "dialog-trigger");
        h.Click(trigger);

        var dialog = Popup(h, Role.Dialog);
        Assert.NotNull(dialog);
        Assert.Equal("true", A(dialog, "aria-modal"));
        Assert.False(string.IsNullOrEmpty(A(dialog, "aria-labelledby")));
        Assert.False(string.IsNullOrEmpty(A(dialog, "aria-describedby")));

        // 模态对话框把焦点移入内容
        Assert.True(IsInside(dialog!, h.Focused), $"focused: {h.Focused?.Role}");

        h.Escape();
        Assert.Null(Popup(h, Role.Dialog));
        Assert.True(trigger.IsFocused);
    }

    [Fact]
    public void AlertDialog_uses_alert_dialog_role_and_cancel_closes()
    {
        var h = Harness(() => new AlertDialog
        {
            Children =
            [
                new AlertDialogTrigger { Children = [new UIText("Clear note")] },
                new AlertDialogContent
                {
                    Children =
                    [
                        new AlertDialogHeader
                        {
                            Children = [new AlertDialogTitle { Children = [new UIText("Clear workspace notes?")] }],
                        },
                        new AlertDialogFooter
                        {
                            Children =
                            [
                                new AlertDialogCancel(),
                                new AlertDialogAction(),
                            ],
                        },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "alert-dialog-trigger");
        h.Click(trigger);

        var dialog = h.FindAll(e => A(e, "role") == "alertdialog" && e.IsVisible).FirstOrDefault();
        Assert.NotNull(dialog);
        Assert.True(IsInside(dialog!, h.Focused), $"focused: {h.Focused?.Role}");

        var cancel = h.FindAll(e => A(e, "data-slot") == "alert-dialog-cancel").Single();
        h.Click(cancel);
        Assert.Empty(h.FindAll(e => A(e, "role") == "alertdialog"));
    }

    [Fact]
    public void Sheet_opens_from_trigger_with_side_and_title()
    {
        var h = Harness(() => new Sheet
        {
            Children =
            [
                new SheetTrigger { Children = [new UIText("Preferences")] },
                new SheetContent
                {
                    Side = Side.Left,
                    Children =
                    [
                        new SheetHeader
                        {
                            Children =
                            [
                                new SheetTitle { Children = [new UIText("Preferences")] },
                                new SheetDescription { Children = [new UIText("Workspace settings")] },
                            ],
                        },
                    ],
                },
            ],
        });

        var trigger = SlotOrThrow(h, "sheet-trigger");
        h.Click(trigger);

        var dialog = Popup(h, Role.Dialog);
        Assert.NotNull(dialog);
        Assert.Equal("left", A(dialog, "data-side"));

        h.Escape();
        Assert.Null(Popup(h, Role.Dialog));
    }

    // ------------------------------------------------------------ ToolTip

    [Fact]
    public void Tooltip_shows_on_keyboard_focus_and_hides_on_escape()
    {
        var h = Harness(() => new Tooltip
        {
            Children =
            [
                new TooltipTrigger { Children = [new UIText("Help")] },
                new TooltipContent { Children = [new UIText("Opens the help center")] },
            ],
        });

        var trigger = SlotOrThrow(h, "tooltip-trigger");
        Assert.Equal("closed", A(trigger, "data-state"));

        h.Tab();
        Assert.True(trigger.IsFocused);
        Assert.NotNull(Popup(h, Role.Tooltip));
        Assert.Equal("instant-open", A(trigger, "data-state"));

        h.Escape();
        Assert.Null(Popup(h, Role.Tooltip));
    }
}
