using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;

namespace AwAUI.Controls.UnitTests.Focus;

public class ItemCollectionTests
{
    [Fact]
    public void Register_keeps_order_and_updates_in_place()
    {
        var c = new ItemCollection<string>();
        c.Register("a", "Apple");
        c.Register("b", "Banana", disabled: true);
        c.Register("c", "Cherry");
        int v = c.Version;
        c.Register("b", "Banana", disabled: false);
        Assert.Equal(new[] { "a", "b", "c" }, c.Select(i => i.Key));
        Assert.False(c[1].Disabled);
        Assert.True(c.Version > v);
        v = c.Version;
        c.Register("b", "Banana", disabled: false);
        Assert.Equal(v, c.Version);   // 没有变化
    }

    [Fact]
    public void Unregister_reorder_and_sort()
    {
        var c = new ItemCollection<int>();
        foreach (var i in new[] { 5, 3, 9, 1 }) c.Register(i, i.ToString());
        Assert.True(c.Unregister(3));
        Assert.False(c.Unregister(3));
        Assert.Equal(2, c.IndexOf(1));
        c.Reorder(new[] { 1, 42, 5 });
        Assert.Equal(new[] { 1, 5, 9 }, c.Select(i => i.Key));
        c.Sort((a, b) => b.Key.CompareTo(a.Key));
        Assert.Equal(new[] { 9, 5, 1 }, c.Select(i => i.Key));
        Assert.Equal(0, c.IndexOf(9));
        Assert.Equal(new[] { 9, 5, 1 }, c.EnabledItems.Select(i => i.Key));
    }
}

public class TypeAheadTests
{
    static readonly string[] Fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Éclair", "avocado" };

    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Accumulates_and_matches_prefix_case_insensitively()
    {
        var t = new TypeAhead();
        Assert.Equal(2, t.HandleKey("b", Ms(0), Fruits, -1));
        Assert.Equal(3, t.HandleKey("l", Ms(200), Fruits, 2));
        Assert.Equal("bl", t.Search);
    }

    [Fact]
    public void Repeated_character_cycles_through_matches()
    {
        var t = new TypeAhead();
        int cur = -1;
        var seen = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            cur = t.HandleKey("a", Ms(i * 100), Fruits, cur);
            seen.Add(cur);
        }
        Assert.Equal(new[] { 0, 1, 6, 0 }, seen);
    }

    [Fact]
    public void Single_char_starts_after_current_and_skips_disabled()
    {
        var t = new TypeAhead();
        Assert.Equal(6, t.HandleKey("a", Ms(0), Fruits, 1, i => i != 0));
        t.Reset();
        Assert.Equal(1, t.HandleKey("a", Ms(0), Fruits, 6, i => i != 0));
    }

    [Fact]
    public void Multi_char_search_can_stay_on_current()
    {
        var t = new TypeAhead();
        Assert.Equal(0, t.HandleKey("a", Ms(0), Fruits, -1));
        // "ap" 仍匹配当前项 Apple：不移动（返回 -1）
        Assert.Equal(-1, t.HandleKey("p", Ms(10), Fruits, 0));
        Assert.Equal(1, t.HandleKey("r", Ms(20), Fruits, 0));
    }

    [Fact]
    public void Buffer_resets_after_one_second()
    {
        var t = new TypeAhead();
        t.HandleKey("b", Ms(0), Fruits, -1);
        Assert.True(t.IsTypingAhead(Ms(999)));
        Assert.False(t.IsTypingAhead(Ms(1000)));
        Assert.Equal(4, t.HandleKey("c", Ms(1500), Fruits, 2));
        Assert.Equal("c", t.Search);
    }

    [Fact]
    public void Diacritic_and_width_insensitive()
    {
        var t = new TypeAhead();
        Assert.Equal(5, t.HandleKey("e", Ms(0), Fruits, -1));
        Assert.Equal(5, TypeAhead.GetNextMatch(Fruits, "ÉC", -1));
        Assert.Equal(0, TypeAhead.GetNextMatch(new[] { "Ｆｕｌｌ" }, "full", -1));
        Assert.Equal("cafe", TypeAhead.Normalize("CAFÉ"));
    }

    [Fact]
    public void Duplicate_texts_are_reachable_by_index()
    {
        var items = new[] { "Apple", "Apple", "Banana" };
        Assert.Equal(1, TypeAhead.GetNextMatch(items, "a", 0));
        Assert.Equal(0, TypeAhead.GetNextMatch(items, "a", 1));
    }

    [Fact]
    public void Key_filter_and_untrusted_input()
    {
        Assert.True(TypeAhead.IsTypeAheadKey("a", KeyModifiers.Shift));
        Assert.True(TypeAhead.IsTypeAheadKey("😀", KeyModifiers.None));
        Assert.False(TypeAhead.IsTypeAheadKey("a", KeyModifiers.Control));
        Assert.False(TypeAhead.IsTypeAheadKey("ArrowDown", KeyModifiers.None));
        Assert.False(TypeAhead.IsTypeAheadKey("\t", KeyModifiers.None));
        Assert.False(TypeAhead.IsTypeAheadKey("\ud800", KeyModifiers.None));
        // 孤立代理项不会让规范化抛出
        Assert.Equal("\ud800x", TypeAhead.Normalize("\ud800X"));
        var t = new TypeAhead();
        for (int i = 0; i < 10_000; i++) t.Append("z", Ms(i));
        Assert.Equal(TypeAhead.MaxSearchLength, t.Search.Length);
    }

    [Fact]
    public void Works_on_item_collection()
    {
        var c = new ItemCollection<string>();
        c.Register("one", "One");
        c.Register("two", "Two", disabled: true);
        c.Register("three", "Three");
        c.Register("sep", null);
        var t = new TypeAhead();
        Assert.Equal("three", t.HandleKey("t", Ms(0), c, "one")!.Key);
        Assert.Null(t.HandleKey("x", Ms(10), c, "three"));
    }
}

public class RovingFocusTests
{
    [Theory]
    [InlineData("ArrowDown", null, Direction.Ltr, FocusIntent.Next)]
    [InlineData("ArrowRight", null, Direction.Rtl, FocusIntent.Previous)]
    [InlineData("ArrowLeft", null, Direction.Rtl, FocusIntent.Next)]
    [InlineData("ArrowLeft", Orientation.Vertical, Direction.Ltr, FocusIntent.None)]
    [InlineData("ArrowUp", Orientation.Horizontal, Direction.Ltr, FocusIntent.None)]
    [InlineData("Home", Orientation.Horizontal, Direction.Ltr, FocusIntent.First)]
    [InlineData("PageDown", null, Direction.Ltr, FocusIntent.Last)]
    [InlineData("Enter", null, Direction.Ltr, FocusIntent.None)]
    public void Key_to_intent(string key, Orientation? orientation, Direction dir, FocusIntent expected) =>
        Assert.Equal(expected, RovingFocus.GetFocusIntent(key, orientation, dir));

    [Fact]
    public void Navigate_skips_disabled_and_respects_loop()
    {
        Func<int, bool> f = i => i != 3 && i != 4;
        Assert.Equal(5, RovingFocus.Navigate(FocusIntent.Next, 2, 6, f, loop: false));
        Assert.Equal(-1, RovingFocus.Navigate(FocusIntent.Next, 5, 6, f, loop: false));
        Assert.Equal(0, RovingFocus.Navigate(FocusIntent.Next, 5, 6, f, loop: true));
        Assert.Equal(5, RovingFocus.Navigate(FocusIntent.Previous, 0, 6, f, loop: true));
        Assert.Equal(0, RovingFocus.Navigate(FocusIntent.Next, -1, 6, f, loop: false));
        Assert.Equal(5, RovingFocus.Navigate(FocusIntent.Previous, -1, 6, f, loop: false));
        Assert.Equal(-1, RovingFocus.Navigate(FocusIntent.Next, 0, 6, _ => false, loop: true));
        Assert.Equal(-1, RovingFocus.Navigate(FocusIntent.First, 0, 0, null, loop: true));
    }

    [Fact]
    public void Page_navigation()
    {
        Assert.Equal(10, RovingFocus.Navigate(FocusIntent.PageForward, 0, 100, null, false, pageSize: 10));
        Assert.Equal(99, RovingFocus.Navigate(FocusIntent.PageForward, 95, 100, null, false, pageSize: 10));
        Assert.Equal(0, RovingFocus.Navigate(FocusIntent.PageBackward, 5, 100, null, false, pageSize: 10));
        // 目标禁用：沿方向继续，再回头
        Assert.Equal(12, RovingFocus.Navigate(FocusIntent.PageForward, 0, 100, i => i < 10 || i > 11, false, pageSize: 10));
        Assert.Equal(9, RovingFocus.Navigate(FocusIntent.PageForward, 0, 12, i => i < 10, false, pageSize: 10));
    }

    [Fact]
    public void HandleKeyDown_ignores_modifiers_and_reports_handled_without_move()
    {
        Assert.Equal(-1, RovingFocus.HandleKeyDown("ArrowDown", KeyModifiers.Shift, 0, 5, null));
        Assert.Equal(4, RovingFocus.HandleKeyDown("ArrowDown", KeyModifiers.None, 4, 5, null));   // 末尾不回绕：已处理，不动
        Assert.Equal(-1, RovingFocus.HandleKeyDown("Tab", KeyModifiers.None, 0, 5, null));
    }

    [Fact]
    public void Group_tracks_tab_stop()
    {
        var items = new ItemCollection<string>();
        items.Register("bold");
        items.Register("italic");
        items.Register("underline", disabled: true);
        items.Register("strike");
        var g = new RovingFocusGroup<string>(items, new RovingFocusOptions { Orientation = Orientation.Horizontal, Loop = true });
        Assert.Equal(0, g.GetTabIndex("bold"));
        Assert.Equal(-1, g.GetTabIndex("italic"));
        Assert.True(g.HandleKeyDown("ArrowRight", KeyModifiers.None, "italic", out var next));
        Assert.Equal("strike", next);
        Assert.Equal(0, g.GetTabIndex("strike"));
        Assert.Equal(-1, g.GetTabIndex("bold"));
        Assert.True(g.HandleKeyDown("ArrowRight", KeyModifiers.None, "strike", out next));
        Assert.Equal("bold", next);
        Assert.False(g.HandleKeyDown("ArrowDown", KeyModifiers.None, "bold", out _));
        // 当前 Tab 位被注销：退回第一个可聚焦项
        items.Unregister("bold");
        Assert.Equal(0, g.GetTabIndex("italic"));
        Assert.Equal(-1, g.GetTabIndex("missing"));
    }
}

public class SubmenuGraceAreaTests
{
    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Point_in_polygon()
    {
        var square = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10) };
        Assert.True(SubmenuGraceArea.IsPointInPolygon(new Point(5, 5), square));
        Assert.False(SubmenuGraceArea.IsPointInPolygon(new Point(15, 5), square));
        Assert.False(SubmenuGraceArea.IsPointInPolygon(new Point(5, -1), square));
        Assert.False(SubmenuGraceArea.IsPointInPolygon(new Point(1, 1), Array.Empty<Point>()));
    }

    [Fact]
    public void Pointer_moving_diagonally_towards_right_submenu_is_in_grace()
    {
        var g = new SubmenuGraceArea();
        var submenu = new Rect(200, 50, 150, 300);
        g.TrackPointerMove(new Point(150, 120));
        g.TrackPointerMove(new Point(160, 120));
        g.Begin(new Point(160, 120), submenu, Side.Right, Ms(0));
        Assert.Equal(5, g.Polygon.Count);
        Assert.Equal(155, g.Polygon[0].X);   // 离开点向外侧放宽 5px
        g.TrackPointerMove(new Point(175, 140));
        Assert.True(g.IsPointerMovingToSubmenu(new Point(175, 140), Ms(50)));
        // 三角形之外（向下偏得太多）
        Assert.False(g.IsPointerMovingToSubmenu(new Point(175, 400), Ms(60)));
        // 向左移动：不是移往子菜单
        g.TrackPointerMove(new Point(170, 140));
        Assert.False(g.IsPointerMovingToSubmenu(new Point(170, 140), Ms(70)));
    }

    [Fact]
    public void Grace_expires_after_300ms_and_supports_left_side()
    {
        var g = new SubmenuGraceArea();
        g.TrackPointerMove(new Point(300, 100));
        g.TrackPointerMove(new Point(290, 100));
        g.Begin(new Point(290, 100), new Rect(50, 0, 200, 300), Side.Left, Ms(1000));
        Assert.Equal(Side.Left, g.PointerDirection);
        Assert.True(g.IsPointerMovingToSubmenu(new Point(270, 110), Ms(1299)));
        Assert.False(g.IsPointerMovingToSubmenu(new Point(270, 110), Ms(1300)));
        g.Begin(new Point(290, 100), new Rect(50, 0, 200, 300), Side.Left, Ms(2000));
        g.Clear();
        Assert.False(g.IsActive(Ms(2001)));
    }
}
