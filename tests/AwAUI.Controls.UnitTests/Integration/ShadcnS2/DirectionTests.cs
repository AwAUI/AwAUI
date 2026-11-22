using System;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using Direction = AwAUI.Layout.Direction;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 DirectionProvider 验收：<c>dir</c> 属性、<c>display: contents</c> 包装、子内容的方向继承，
/// 以及 <see cref="Direction"/> 为 null 时的惰性直通。
/// </summary>
public class DirectionTests
{
    static DirectionTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SlotOrThrow(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"no element with data-slot=\"{slot}\"");

    static Element Probe(UIHarness h) => h.FindAll(e => e.HasClass("probe")).Single();

    [Fact]
    public void DirectionProvider_rtl_sets_dir_and_children_inherit_it()
    {
        var h = Harness(() => new DirectionProvider
        {
            Dir = Direction.Rtl,
            Children = [new Div { Class = "probe", Children = [new UIText("مرحبا")] }],
        });

        var wrapper = SlotOrThrow(h, "direction-provider");
        Assert.Equal("rtl", A(wrapper, "dir"));
        Assert.True(wrapper.HasClass("contents"));
        Assert.Equal(AwAUI.Css.Values.CssDirection.Rtl, Probe(h).ComputedStyle?.Direction);
    }

    [Fact]
    public void DirectionProvider_ltr_sets_dir_and_children_inherit_it()
    {
        var h = Harness(() => new DirectionProvider
        {
            Dir = Direction.Ltr,
            Children = [new Div { Class = "probe", Children = [new UIText("Hello")] }],
        });

        var wrapper = SlotOrThrow(h, "direction-provider");
        Assert.Equal("ltr", A(wrapper, "dir"));
        Assert.Equal(AwAUI.Css.Values.CssDirection.Ltr, Probe(h).ComputedStyle?.Direction);
    }

    [Fact]
    public void DirectionProvider_without_dir_renders_an_inert_wrapper()
    {
        var h = Harness(() => new DirectionProvider
        {
            Children = [new Div { Class = "probe", Children = [new UIText("Text")] }],
        });

        var wrapper = SlotOrThrow(h, "direction-provider");
        Assert.Null(A(wrapper, "dir"));
        Assert.True(wrapper.HasClass("contents"));
        Assert.Equal(AwAUI.Css.Values.CssDirection.Ltr, Probe(h).ComputedStyle?.Direction);
    }

    [Fact]
    public void DirectionProvider_merges_class_and_rest_attributes()
    {
        var h = Harness(() => new DirectionProvider
        {
            Dir = Direction.Rtl,
            Class = "custom-direction",
            Rest = new AttributeDictionary { ["data-testid"] = "dir" },
            Children = [new UIText("Text")],
        });

        var wrapper = SlotOrThrow(h, "direction-provider");
        Assert.True(wrapper.HasClass("custom-direction"));
        Assert.Equal("dir", A(wrapper, "data-testid"));
    }
}
