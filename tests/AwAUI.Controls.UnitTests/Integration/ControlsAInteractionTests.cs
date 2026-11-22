using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Keyboard and pointer coverage for the 9c Controls A components.</summary>
public class ControlsAInteractionTests
{
    static MenuItem Item(string header, params MenuItem[] children) => new()
    {
        Header = header,
        Children = children.Length == 0 ? null : new NodeList(children),
    };

    static List<Element> Popups(UIHarness h) =>
        h.FindAll(e => e.Role == Role.Menu && e.IsVisible);

    [Fact]
    public void MenuBar_opens_with_keyboard_and_restores_focus_on_escape()
    {
        var h = new UIHarness(() => new Menu
        {
            Children =
            [
                Item("File", Item("New"), Item("Open")),
                Item("Edit", Item("Undo")),
            ],
        });

        h.Tab();
        var firstTrigger = h.Focused;
        Assert.NotNull(firstTrigger);
        Assert.Equal(Role.MenuItem, firstTrigger!.Role);

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Single(Popups(h));

        h.Escape();
        Assert.Empty(Popups(h));
        Assert.True(firstTrigger.IsFocused);
    }

    [Fact]
    public void MenuBar_right_arrow_moves_between_top_level_items()
    {
        var h = new UIHarness(() => new Menu
        {
            Children = [Item("File", Item("New")), Item("Edit", Item("Undo"))],
        });

        h.Tab();
        var file = h.Focused;
        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.NotSame(file, h.Focused);
        Assert.Equal(Role.MenuItem, h.Focused!.Role);
    }

    [Fact]
    public void Menu_submenu_opens_on_the_inline_end_side_in_rtl()
    {
        var h = new UIHarness(() => new Menu
        {
            Children = [Item("File", Item("New"), Item("Open Recent", Item("Doc1")))],
        });
        // 弹出层挂在根元素之下，文档方向从根元素继承
        h.Root.RootElement.SetAttributeOverride("dir", "rtl");
        h.Frame();

        h.Tab();
        h.Key(PhysicalKey.Enter, "Enter");
        h.Frame();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        var subTrigger = h.Focused;
        Assert.NotNull(subTrigger);
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        h.Frame();

        var menus = Popups(h);
        Assert.Equal(2, menus.Count);
        var sub = menus[1].GetBoundingClientRect();
        Assert.True(sub.Right <= subTrigger!.GetBoundingClientRect().Left + 1,
            $"RTL: the submenu should open on the inline-end (left) side, sub={sub} trigger={subTrigger.GetBoundingClientRect()}");
    }

    [Fact]
    public void ContextMenu_opens_on_right_click_and_closes_on_escape()
    {
        var h = new UIHarness(() => new ContextMenu
        {
            Children = [new UIButton { Id = "target", Children = [new UIText("Open")] }],
            Items = [Item("Copy"), Item("Paste")],
        });

        h.RightClick(h.ById("target"));
        Assert.Single(Popups(h));

        h.Escape();
        Assert.Empty(Popups(h));
    }

    [Fact]
    public void ContextMenu_opens_with_the_context_menu_key()
    {
        var h = new UIHarness(() => new ContextMenu
        {
            Children = [new UIButton { Id = "target", Children = [new UIText("Open")] }],
            Items = [Item("Copy")],
        });

        h.ById("target").Focus();
        h.Key(PhysicalKey.ContextMenu, "ContextMenu");
        Assert.Single(Popups(h));
    }

    [Fact]
    public void SplitButton_runs_primary_click_and_opens_the_dropdown()
    {
        int clicks = 0;
        var h = new UIHarness(() => new SplitButton
        {
            Children = [new UIText("Save")],
            Items = [Item("Save as")],
            OnClick = _ => clicks++,
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "primary").Single());
        Assert.Equal(1, clicks);

        h.Click(h.FindAll(e => e.GetAttribute("part") == "toggle").Single());
        Assert.Single(Popups(h));
    }

    [Fact]
    public void RelativePanel_positions_children_relative_to_each_other()
    {
        var first = new Div { Id = "first", Style = "width:50px;height:20px;" };
        var second = new Div { Id = "second", Style = "width:30px;height:10px;" };
        RelativePanel.SetRightOf(second, first);
        RelativePanel.SetBelow(second, first);
        var h = new UIHarness(() => new RelativePanel
        {
            Children = [first, second],
            Style = "width:300px;height:200px;",
        });

        var a = h.Box("first");
        var b = h.Box("second");
        Assert.Equal(a.Right, b.X, 1);
        Assert.Equal(a.Bottom, b.Y, 1);
    }

    [Fact]
    public void RelativePanel_aligns_to_panel_edges()
    {
        var bottomRight = new Div { Id = "corner", Style = "width:20px;height:10px;" };
        RelativePanel.SetAlignRightWithPanel(bottomRight);
        RelativePanel.SetAlignBottomWithPanel(bottomRight);
        var h = new UIHarness(() => new RelativePanel
        {
            Children = [bottomRight],
            Style = "width:300px;height:120px;",
        });

        var panel = h.FindAll(e => e.GetAttribute("part") == "panel").Single().BoundingRect;
        var box = h.Box("corner");
        Assert.Equal(panel.Right, box.Right, 1);
        Assert.Equal(panel.Bottom, box.Bottom, 1);
    }

    [Fact]
    public void GridSplitter_arrow_key_moves_the_split()
    {
        double? position = null;
        var h = new UIHarness(() => new GridSplitter
        {
            SplitPositionChanged = v => position = v,
            Children = [new Div { Id = "left" }, new Div { Id = "right" }],
            Style = "width:400px;height:200px;",
        });

        var bar = h.FindAll(e => e.Role == Role.Separator).Single();
        bar.Focus();
        h.Key(PhysicalKey.ArrowRight, "ArrowRight", KeyModifiers.Shift);
        Assert.NotNull(position);
        Assert.Equal(0.6, position!.Value, 3);
    }

    [Fact]
    public void Carousel_next_button_advances_the_selected_slide()
    {
        var h = new UIHarness(() => new Carousel
        {
            Children = [new Div { Id = "one" }, new Div { Id = "two" }, new Div { Id = "three" }],
            Style = "width:300px;height:200px;",
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "next").Single());
        var selected = h.FindAll(e => e.GetAttribute("part") == "indicator" && e.GetAttribute("aria-selected") == "true");
        Assert.Single(selected);
        Assert.Equal("Go to slide 2", selected[0].GetAttribute("aria-label"));
    }

    [Fact]
    public void NumericUpDown_spinner_increments_and_clamps()
    {
        var h = new UIHarness(() => new NumericUpDown
        {
            DefaultValue = 5,
            Minimum = 0,
            Maximum = 10,
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "up").Single());
        var input = Assert.IsType<InputElement>(h.FindAll(e => e.GetAttribute("part") == "input").Single());
        Assert.Equal("6", input.Value);

        h.Click(h.FindAll(e => e.GetAttribute("part") == "down").Single());
        h.Click(h.FindAll(e => e.GetAttribute("part") == "down").Single());
        Assert.Equal("4", input.Value);
    }

    [Fact]
    public void AutoCompleteBox_filters_and_accepts_with_the_keyboard()
    {
        var h = new UIHarness(() => new AutoCompleteBox
        {
            ItemsSource = new[] { "apple", "apricot", "banana" },
        });

        var input = Assert.IsType<InputElement>(h.FindAll(e => e.GetAttribute("part") == "input").Single());
        input.Focus();
        h.TypeText("ap");

        var options = h.FindAll(e => e.Role == Role.Option && e.IsVisible);
        Assert.Equal(2, options.Count);

        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("apricot", input.Value);
        Assert.Empty(h.FindAll(e => e.Role == Role.Option && e.IsVisible));
    }

    [Fact]
    public void RelativePanel_attribute_relations_resolve_by_id()
    {
        var first = new Div { Id = "first", Style = "width:50px;height:20px;" };
        var second = new Div { Id = "second", Style = "width:30px;height:10px;" };
        second.Attributes["data-relative-right-of"] = "first";
        second.Attributes["data-relative-align-top-with"] = "first";
        var third = new Div { Id = "third", Style = "width:30px;height:10px;" };
        third.Attributes["data-relative-below"] = "first";
        third.Attributes["data-relative-align-left-with"] = "first";
        var h = new UIHarness(() => new RelativePanel
        {
            Children = [first, second, third],
            Style = "width:300px;height:200px;",
        });
        var a = h.Box("first");
        var b = h.Box("second");
        var c = h.Box("third");
        Assert.Equal(a.Right, b.X, 1);
        Assert.Equal(a.Y, b.Y, 1);
        Assert.Equal(a.X, c.X, 1);
        Assert.Equal(a.Bottom, c.Y, 1);
    }

    [Fact]
    public void RelativePanel_natural_width_anchor()
    {
        var first = new Div { Id = "first" };
        first.Children.Add("Anchor");
        var second = new Div { Id = "second" };
        second.Children.Add("Right of");
        second.Attributes["data-relative-right-of"] = "first";
        second.Attributes["data-relative-align-top-with"] = "first";
        var h = new UIHarness(() => new RelativePanel
        {
            Children = [first, second],
            Style = "width:300px;height:200px;",
        });
        var a = h.Box("first");
        var b = h.Box("second");
        Assert.True(a.Width > 0, $"anchor width {a.Width}");
        Assert.Equal(a.Right, b.X, 1);
    }
}
