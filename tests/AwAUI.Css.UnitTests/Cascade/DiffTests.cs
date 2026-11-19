using AwAUI.Css.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

public class DiffTests
{
    static StyleDifference Diff(string before, string after)
    {
        var e1 = new StyleEngine();
        e1.AddStyleSheet(StyleSheet.Parse("div { " + before + " }"));
        var e2 = new StyleEngine();
        e2.AddStyleSheet(StyleSheet.Parse("div { " + after + " }"));
        return ComputedStyle.Diff(e1.Resolve(new El("div"), null), e2.Resolve(new El("div"), null));
    }

    [Theory]
    [InlineData("color: red", "color: red", StyleDifference.None)]
    [InlineData("color: red", "color: blue", StyleDifference.Paint)]
    [InlineData("width: 1px", "width: 2px", StyleDifference.Layout)]
    [InlineData("opacity: 1", "opacity: 0.5", StyleDifference.Composite)]
    [InlineData("display: block", "display: flex", StyleDifference.Structure)]
    [InlineData("position: static", "position: absolute", StyleDifference.Structure)]
    [InlineData("z-index: auto", "z-index: 1", StyleDifference.Structure)]
    [InlineData("cursor: auto", "cursor: pointer", StyleDifference.Behavior)]
    [InlineData("border-top-color: red", "border-top-color: blue", StyleDifference.Paint)]
    [InlineData("border-top-style: solid", "border-top-style: solid; border-top-width: 1px", StyleDifference.Layout)]
    [InlineData("background-image: none", "background-image: linear-gradient(red, blue)", StyleDifference.Paint)]
    [InlineData("box-shadow: 1px 1px red", "box-shadow: 1px 1px red", StyleDifference.None)]
    [InlineData("font-family: a, serif", "font-family: a, serif", StyleDifference.None)]
    [InlineData("color: red; opacity: 0.5", "color: blue; opacity: 0.4", StyleDifference.Paint | StyleDifference.Composite)]
    [InlineData("--x: 1", "--x: 2", StyleDifference.CustomProperties)]
    public void Diff_flags(string before, string after, StyleDifference expected) => Assert.Equal(expected, Diff(before, after));

    [Fact]
    public void Same_reference_and_null()
    {
        var s = ComputedStyle.Initial;
        Assert.Equal(StyleDifference.None, ComputedStyle.Diff(s, s));
        Assert.Equal(StyleDifference.Structure, ComputedStyle.Diff(null, s));
        Assert.Equal(StyleDifference.None, ComputedStyle.Diff(null, null));
    }

    [Fact]
    public void Re_resolving_an_unchanged_element_shares_groups()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div { color: red } span { width: 1px }"));
        var span = new El("span");
        var div = new El("div").Add(span);
        var p1 = e.Resolve(div, null);
        var c1 = e.Resolve(span, p1);
        var c2 = e.Resolve(span, p1);
        Assert.NotSame(c1, c2);
        Assert.Same(c1.TextGroup, c2.TextGroup);
        Assert.Same(c1.BorderGroup, c2.BorderGroup);
        Assert.Equal(c1.BoxGroup, c2.BoxGroup);
        Assert.Equal(StyleDifference.None, ComputedStyle.Diff(c1, c2));
    }

    [Fact]
    public void Group_equality_and_hash()
    {
        var a = ComputedStyle.Initial.BoxGroup;
        var b = a.Clone();
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        b.Width = SizeValue.FromPx(3);
        Assert.NotEqual(a, b);
    }
}
