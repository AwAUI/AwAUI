using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Cascade.CascadeTestHelper;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary><c>outline</c> 及其长属性（第 1 期提前加入：焦点环需要它）。</summary>
public class OutlineTests
{
    [Fact]
    public void Initial_values()
    {
        var s = ComputedStyle.Initial;
        Assert.Equal(CssLineStyle.None, s.OutlineStyle);
        Assert.Equal(3f, s.OutlineWidth); // medium
        Assert.True(s.OutlineColor.IsCurrentColor);
        Assert.Equal(0f, s.OutlineOffset.Resolve(0));
    }

    [Fact]
    public void Shorthand_and_longhands()
    {
        var el = new El("button", classes: "b");
        var s = One(".b { outline: 2px solid #ff0000; outline-offset: -1px; }", el);
        Assert.Equal(CssLineStyle.Solid, s.OutlineStyle);
        Assert.Equal(2f, s.OutlineWidth);
        Assert.Equal(new Color(1, 0, 0), s.ResolveColor(s.OutlineColor));
        Assert.Equal(-1f, s.OutlineOffset.Resolve(0));
        Assert.Equal("2px", s.Text("outline-width"));

        var t = One(".b { outline: dashed; outline-width: thin; }", el);
        Assert.Equal(CssLineStyle.Dashed, t.OutlineStyle);
        Assert.Equal(1f, t.OutlineWidth);
    }

    [Fact]
    public void Outline_change_is_paint_only()
    {
        var engine = Engine(".a { outline: 1px solid red; } .b { outline: 3px dotted blue; }");
        var a = engine.Resolve(new El("div", classes: "a"), null);
        var b = engine.Resolve(new El("div", classes: "b"), null);
        Assert.Equal(StyleDifference.Paint, ComputedStyle.Diff(a, b));
    }
}
