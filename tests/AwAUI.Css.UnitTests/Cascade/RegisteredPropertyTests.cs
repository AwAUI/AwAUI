using AwAUI.Css.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary><c>@property</c>：注册、初始值、继承性、类型化计算与计算值时无效。</summary>
public class RegisteredPropertyTests
{
    [Theory]
    [InlineData("*", true)]
    [InlineData("<length>", true)]
    [InlineData("<length> | auto", true)]
    [InlineData("<color>#", true)]
    [InlineData("<length-percentage>+", true)]
    [InlineData("<transform-list>", true)]
    [InlineData("<transform-list>+", false)]
    [InlineData("<foo>", false)]
    [InlineData("", false)]
    [InlineData("<length> |", false)]
    [InlineData("inherit", false)]
    public void Syntax_strings(string syntax, bool valid) => Assert.Equal(valid, CssPropertySyntax.Parse(syntax) is not null);

    [Theory]
    [InlineData("@property --x { syntax: '<length>'; inherits: false; initial-value: 0px; }", true)]
    [InlineData("@property --x { syntax: '*'; inherits: true; }", true)]
    [InlineData("@property --x { syntax: '<length>'; inherits: false; }", false)]
    [InlineData("@property --x { syntax: '<length>'; initial-value: 0px; }", false)]
    [InlineData("@property --x { syntax: '<length>'; inherits: false; initial-value: 1em; }", false)]
    [InlineData("@property --x { syntax: '<length>'; inherits: false; initial-value: red; }", false)]
    [InlineData("@property --x { syntax: '<color>'; inherits: maybe; initial-value: red; }", false)]
    [InlineData("@property x { syntax: '*'; inherits: true; }", false)]
    [InlineData("@property --x --y { syntax: '*'; inherits: true; }", false)]
    public void Property_rule_validity(string css, bool valid)
    {
        var s = StyleSheet.Parse(css);
        Assert.Equal(valid, s.Rules.Count == 1 && s.Rules[0] is CssPropertyRule);
        if (!valid) Assert.Contains(s.Diagnostics, d => d.Code == CssDiagnosticCodes.InvalidAtRule);
    }

    const string Registrations =
        "@property --tw-translate-x { syntax: \"*\"; inherits: false; initial-value: 0; }"
        + " @property --tw-ring-offset-width { syntax: \"<length>\"; inherits: false; initial-value: 0px; }"
        + " @property --brand { syntax: \"<color>\"; inherits: true; initial-value: red; }"
        + " @property --gap { syntax: \"<length>\"; inherits: true; initial-value: 4px; }";

    [Fact]
    public void Initial_values_are_available_everywhere()
    {
        var engine = CascadeTestHelper.Engine(Registrations + " p { translate: var(--tw-translate-x) 0; color: var(--brand); margin-left: var(--gap) }");
        var root = new El("html").Add(new El("p"));
        var map = CascadeTestHelper.ResolveTree(engine, root);
        var p = map[(El)root[0]];
        Assert.Equal("0px", p.GetPropertyValueText("translate"));
        Assert.Equal("rgb(255, 0, 0)", p.GetPropertyValueText("color"));
        Assert.Equal("4px", p.GetPropertyValueText("margin-left"));
        Assert.Equal("0", map[root].GetCustomPropertyText("--tw-translate-x"));
        Assert.Equal(4, engine.Registry.Count);
        Assert.True(engine.Registry.TryGet("--brand", out var brand) && brand.Inherits);
    }

    [Fact]
    public void Non_inherited_registered_properties_reset_on_children()
    {
        var engine = CascadeTestHelper.Engine(Registrations + " .t { --tw-translate-x: 10px; --brand: blue } * { translate: var(--tw-translate-x) 0; color: var(--brand) }");
        var grand = new El("b");
        var child = new El("span").Add(grand);
        var t = new El("div", null, "t").Add(child);
        var root = new El("html").Add(t);
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("10px", map[t].GetPropertyValueText("translate"));
        Assert.Equal("0px", map[child].GetPropertyValueText("translate")); // 不继承：回到初始值
        Assert.Equal("0px", map[grand].GetPropertyValueText("translate"));
        Assert.Equal("rgb(0, 0, 255)", map[child].GetPropertyValueText("color")); // 继承
        Assert.Equal("0", map[child].GetCustomPropertyText("--tw-translate-x"));
    }

    [Fact]
    public void Typed_registration_computes_lengths_at_the_declaring_element()
    {
        // 未注册的 --u 按记号继承（子元素再按自身 em 解释），注册为 <length> 的 --gap 在声明处换算为 px 后继承
        var engine = CascadeTestHelper.Engine(Registrations + " .p { font-size: 10px; --gap: 2em; --u: 2em } .c { font-size: 20px; width: var(--gap); height: var(--u) }");
        var c = new El("div", null, "c");
        var p = new El("div", null, "p").Add(c);
        var map = CascadeTestHelper.ResolveTree(engine, new El("html").Add(p));
        Assert.Equal("20px", map[p].GetCustomPropertyText("--gap"));
        Assert.Equal("20px", map[c].GetPropertyValueText("width"));
        Assert.Equal("40px", map[c].GetPropertyValueText("height"));
    }

    [Fact]
    public void Invalid_typed_values_fall_back_to_initial_or_inherited()
    {
        var engine = CascadeTestHelper.Engine(Registrations + " .p { --gap: 8px } .c { --gap: red; --tw-ring-offset-width: blue; --brand: 12px }"
            + " .c { width: var(--gap); height: var(--tw-ring-offset-width); color: var(--brand) }");
        var c = new El("div", null, "c");
        var map = CascadeTestHelper.ResolveTree(engine, new El("html").Add(new El("div", null, "p").Add(c)));
        Assert.Equal("8px", map[c].GetPropertyValueText("width")); // 继承的：取父值
        Assert.Equal("0px", map[c].GetPropertyValueText("height")); // 不继承的：初始值
        Assert.Equal("rgb(255, 0, 0)", map[c].GetPropertyValueText("color")); // 继承的颜色：父值即初始值 red
    }

    [Fact]
    public void Css_wide_keywords_on_registered_properties()
    {
        var engine = CascadeTestHelper.Engine(Registrations + " .p { --brand: blue; --tw-translate-x: 5px } .c { --brand: initial; --tw-translate-x: inherit; color: var(--brand); translate: var(--tw-translate-x) }");
        var c = new El("div", null, "c");
        var map = CascadeTestHelper.ResolveTree(engine, new El("html").Add(new El("div", null, "p").Add(c)));
        Assert.Equal("rgb(255, 0, 0)", map[c].GetPropertyValueText("color"));
        Assert.Equal("5px", map[c].GetPropertyValueText("translate"));
    }

    [Fact]
    public void Last_registration_wins_and_tailwind_registrations_parse()
    {
        var engine = CascadeTestHelper.Engine("@property --x { syntax: '*'; inherits: false; initial-value: 1; } @layer properties { @property --x { syntax: '<number>'; inherits: true; initial-value: 2; } }");
        Assert.True(engine.Registry.TryGet("--x", out var x));
        Assert.True(x.Inherits);
        Assert.Equal("2", x.InitialValue!.ToString());
        var tw = StyleSheet.Parse("@property --tw-gradient-from-position { syntax: \"<length-percentage>\"; inherits: false; initial-value: 0%; }"
            + " @property --tw-gradient-from { syntax: \"<color>\"; inherits: false; initial-value: #0000; }"
            + " @property --tw-rotate-x { syntax: \"*\"; inherits: false; }"
            + " @property --tw-ring-inset { syntax: \"*\"; inherits: false; }"
            + " @property --tw-gradient-angle { syntax: \"<angle>\"; inherits: false; initial-value: 0deg; }"
            + " @property --tw-scale { syntax: \"<percentage>\"; inherits: false; initial-value: 100%; }");
        Assert.Empty(tw.Diagnostics);
        Assert.Equal(6, tw.Rules.Count);
    }
}
