using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

/// <summary>
/// 网格属性、<c>order</c>、<c>justify-*</c> / <c>place-*</c>、逻辑属性与 <c>image-rendering</c> 旧别名的解析、计算值与序列化。
/// 序列化形式与 headless Chromium 的 <c>element.style</c> 一致（<c>[a] repeat(2, 10px [b]) 1fr fit-content(20%)</c>、
/// <c>"a a" "b c"</c>、<c>column dense</c>），只是长度是计算值（px）。
/// </summary>
public class GridValueTests
{
    // ================= 轨道列表 =================

    [Theory]
    [InlineData("none", "none")]
    [InlineData("100px 1fr", "100px 1fr")]
    [InlineData("[a] repeat(2, 10px [b]) 1fr fit-content(20%)", "[a] repeat(2, 10px [b]) 1fr fit-content(20%)")]
    [InlineData("minmax(10px, 1fr) auto min-content max-content", "minmax(10px, 1fr) auto min-content max-content")]
    [InlineData("repeat(auto-fill, minmax(100px, 1fr))", "repeat(auto-fill, minmax(100px, 1fr))")]
    [InlineData("[x y] repeat(auto-fit, [s] 50px) [e] 20%", "[x y] repeat(auto-fit, [s] 50px) [e] 20%")]
    [InlineData("2em 0.5FR", "32px 0.5fr")]
    [InlineData("calc(50% - 10px) 0", "calc(50% - 10px) 0px")]
    [InlineData("repeat(100000000, 1px)", "repeat(10000, 1px)")]
    [InlineData("[] 1px", "[] 1px")]
    public void Track_list_computed_text(string value, string expected) =>
        Assert.Equal(expected, Text("grid-template-columns", value));

    [Theory]
    [InlineData("")]
    [InlineData("[a]")]
    [InlineData("[a] [b] 10px")]
    [InlineData("-10px")]
    [InlineData("-1fr")]
    [InlineData("minmax(1fr, 10px)")]
    [InlineData("minmax(10px)")]
    [InlineData("fit-content(1fr)")]
    [InlineData("repeat(0, 10px)")]
    [InlineData("repeat(2.5, 10px)")]
    [InlineData("repeat(2, [a])")]
    [InlineData("repeat(2, repeat(2, 10px))")]
    [InlineData("repeat(auto-fill, 1fr)")]                       // auto-repeat 中必须是 <fixed-size>
    [InlineData("repeat(auto-fill, 10px) 1fr")]                  // 有 auto-repeat 时其余轨道也必须是 <fixed-size>
    [InlineData("repeat(auto-fill, 10px) repeat(auto-fit, 10px)")]
    [InlineData("[span] 10px")]
    [InlineData("[auto] 10px")]
    [InlineData("10px / 20px")]
    [InlineData("none 10px")]
    public void Invalid_track_lists(string value) => Assert.False(IsValid("grid-template-columns", value), value);

    [Fact]
    public void Track_list_structure_and_em_resolution()
    {
        var s = Compute("grid-template-rows", "[a] 2em repeat(3, 1fr [b])", extra: "font-size: 20px;");
        var list = s.GridTemplateRows;
        Assert.Equal(3, list.Components.Count);
        Assert.Equal(CssGridComponentKind.LineNames, list.Components[0].Kind);
        Assert.Equal("a", list.Components[0].Names[0]);
        Assert.Equal(40, list.Components[1].Track.Min.Length.Length);
        var rep = list.Components[2].Repeat!;
        Assert.Equal(3, rep.Count);
        Assert.Equal(CssGridBreadthKind.Flex, rep.Components[0].Track.Max.Kind);
        // 值相等：两次计算得到的实例相等（布局映射依赖这一点避免多余的重排）
        Assert.Equal(list, Compute("grid-template-rows", "[a] 2em repeat(3, 1fr [b])", extra: "font-size: 20px;").GridTemplateRows);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("100px minmax(10px, auto)", "100px minmax(10px, auto)")]
    [InlineData("1fr", "1fr")]
    public void Auto_track_sizes(string value, string expected) => Assert.Equal(expected, Text("grid-auto-rows", value));

    [Theory]
    [InlineData("none")]
    [InlineData("[a] 10px")]
    [InlineData("repeat(2, 10px)")]
    public void Invalid_auto_track_sizes(string value) => Assert.False(IsValid("grid-auto-columns", value), value);

    // ================= grid-template-areas =================

    [Theory]
    [InlineData("'a a' 'b c'", "\"a a\" \"b c\"")]
    [InlineData("\"head head\"\n  \"side  main\"", "\"head head\" \"side main\"")]
    [InlineData("'a ... b' '. . b'", "\"a . b\" \". . b\"")]
    [InlineData("'a.b'", "\"a . b\"")]
    [InlineData("none", "none")]
    public void Areas_text(string value, string expected) => Assert.Equal(expected, Text("grid-template-areas", value));

    [Theory]
    [InlineData("'a a' 'b'")]          // 列数不一致
    [InlineData("'a b a'")]            // 不是矩形
    [InlineData("'a a' 'a b' 'a a'")]  // 不是填满的矩形
    [InlineData("'a #'")]              // 垃圾记号
    [InlineData("''")]
    [InlineData("'a' none")]
    public void Invalid_areas(string value) => Assert.False(IsValid("grid-template-areas", value), value);

    [Fact]
    public void Areas_record_rectangles()
    {
        var a = Compute("grid-template-areas", "'h h h' 's m m' 's m m'").GridTemplateAreas;
        Assert.Equal(3, a.Rows);
        Assert.Equal(3, a.Columns);
        var m = a.Areas.Single(x => x.Name == "m");
        Assert.Equal((1, 3, 1, 3), (m.RowStart, m.RowEnd, m.ColumnStart, m.ColumnEnd));
        var h = a.Areas.Single(x => x.Name == "h");
        Assert.Equal((0, 1, 0, 3), (h.RowStart, h.RowEnd, h.ColumnStart, h.ColumnEnd));
    }

    [Fact]
    public void Huge_areas_string_is_linear()
    {
        // 50 万个单元：解析与校验只做一遍线性扫描
        var row = string.Join(" ", Enumerable.Repeat("x", 500_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(IsValid("grid-template-areas", "'" + row + "'"));
        Assert.True(sw.Elapsed.TotalSeconds < 30, sw.Elapsed.ToString());
    }

    // ================= <grid-line> =================

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("2", "2")]
    [InlineData("-1", "-1")]
    [InlineData("foo", "foo")]
    [InlineData("2 foo", "2 foo")]
    [InlineData("foo 2", "2 foo")]
    [InlineData("span 2", "span 2")]
    [InlineData("span foo", "span foo")]
    [InlineData("span foo 3", "span 3 foo")]
    // span 也可以在末尾（span && [ <integer> || <custom-ident> ]，后一组是整体；headless Chromium 同样接受并序列化为 span 在前）。
    // 评审前这三种被拒绝
    [InlineData("2 span", "span 2")]
    [InlineData("2 foo span", "span 2 foo")]
    [InlineData("foo 2 span", "span 2 foo")]
    [InlineData("100000000", "10000")]
    [InlineData("-100000000", "-10000")]
    [InlineData("Foo", "Foo")]   // custom-ident 区分大小写
    public void Grid_line_text(string value, string expected) => Assert.Equal(expected, Text("grid-row-start", value));

    [Theory]
    [InlineData("0")]
    [InlineData("0 foo")]
    [InlineData("span")]
    [InlineData("span 0")]
    [InlineData("span -1")]
    [InlineData("auto foo")]
    [InlineData("span auto")]
    [InlineData("1 2")]
    [InlineData("foo bar")]
    [InlineData("1.5")]
    [InlineData("2 span 3")]
    [InlineData("foo span 3")]   // span 不能在中间（Chromium 同样拒绝）
    [InlineData("3 span foo")]
    [InlineData("span 2 span")]
    [InlineData("-2 span")]
    [InlineData("inherit 2")]
    public void Invalid_grid_lines(string value) => Assert.False(IsValid("grid-column-end", value), value);

    // ================= 简写 =================

    [Theory]
    [InlineData("1 / 3", "1", "3")]
    [InlineData("2", "2", "auto")]
    [InlineData("foo", "foo", "foo")]       // 只有 custom-ident 时另一侧取同名（§8.4）
    [InlineData("span 2 / -1", "span 2", "-1")]
    [InlineData("auto / span foo", "auto", "span foo")]
    public void Grid_row_shorthand(string value, string start, string end)
    {
        var s = Compute("grid-row", value);
        Assert.Equal(start, s.GetPropertyValueText("grid-row-start"));
        Assert.Equal(end, s.GetPropertyValueText("grid-row-end"));
    }

    [Theory]
    [InlineData("a", "a", "a", "a", "a")]
    [InlineData("1 / 2 / 3 / 4", "1", "2", "3", "4")]
    [InlineData("a / b", "a", "b", "a", "b")]
    [InlineData("2 / b", "2", "b", "auto", "b")]
    [InlineData("a / 2 / c", "a", "2", "c", "auto")]
    public void Grid_area_shorthand(string value, string rs, string cs, string re, string ce)
    {
        var s = Compute("grid-area", value);
        Assert.Equal(rs, s.GetPropertyValueText("grid-row-start"));
        Assert.Equal(cs, s.GetPropertyValueText("grid-column-start"));
        Assert.Equal(re, s.GetPropertyValueText("grid-row-end"));
        Assert.Equal(ce, s.GetPropertyValueText("grid-column-end"));
    }

    [Theory]
    [InlineData("1 / 2 / 3 / 4 / 5")]
    [InlineData("1 /")]
    [InlineData("/ 2")]
    public void Invalid_grid_area(string value) => Assert.False(IsValid("grid-area", value), value);

    [Fact]
    public void Grid_template_rows_and_columns_form()
    {
        var s = Compute("grid-template", "auto 1fr / [a] 100px 200px");
        Assert.Equal("auto 1fr", s.GetPropertyValueText("grid-template-rows"));
        Assert.Equal("[a] 100px 200px", s.GetPropertyValueText("grid-template-columns"));
        Assert.Equal("none", s.GetPropertyValueText("grid-template-areas"));
        Assert.Equal("none", Compute("grid-template", "none", extra: "grid-template-columns: 1px;").GetPropertyValueText("grid-template-columns"));
    }

    [Fact]
    public void Grid_template_areas_form()
    {
        var s = Compute("grid-template", "[top] \"a a\" 40px [mid] [mid2] \"b c\" [bot] / 1fr 2fr");
        Assert.Equal("\"a a\" \"b c\"", s.GetPropertyValueText("grid-template-areas"));
        Assert.Equal("[top] 40px [mid mid2] auto [bot]", s.GetPropertyValueText("grid-template-rows"));
        Assert.Equal("1fr 2fr", s.GetPropertyValueText("grid-template-columns"));
    }

    [Theory]
    [InlineData("\"a\" / repeat(2, 1fr)")]   // 区域形式的列只能是 <explicit-track-list>
    [InlineData("\"a\" \"b c\"")]
    [InlineData("[a] [b] \"a\"")]
    [InlineData("10px")]
    [InlineData("1px / 2px / 3px")]
    public void Invalid_grid_template(string value) => Assert.False(IsValid("grid-template", value), value);

    [Fact]
    public void Grid_shorthand_forms()
    {
        var a = Compute("grid", "auto-flow dense 40px / 100px 100px", extra: "grid-template-areas: 'x x';");
        Assert.Equal("dense", a.GetPropertyValueText("grid-auto-flow"));
        Assert.Equal(CssGridAutoFlow.RowDense, a.GridAutoFlow);
        Assert.Equal("40px", a.GetPropertyValueText("grid-auto-rows"));
        Assert.Equal("100px 100px", a.GetPropertyValueText("grid-template-columns"));
        Assert.Equal("none", a.GetPropertyValueText("grid-template-rows"));
        Assert.Equal("none", a.GetPropertyValueText("grid-template-areas"));

        var b = Compute("grid", "100px / auto-flow 50px");
        Assert.Equal(CssGridAutoFlow.Column, b.GridAutoFlow);
        Assert.Equal("50px", b.GetPropertyValueText("grid-auto-columns"));
        Assert.Equal("auto", b.GetPropertyValueText("grid-auto-rows"));

        var c = Compute("grid", "\"a b\" 10px / 1fr 1fr", extra: "grid-auto-flow: column;");
        Assert.Equal("row", c.GetPropertyValueText("grid-auto-flow"));   // grid-template 形式也重置隐式属性
        Assert.Equal(2, c.GridTemplateAreas.Columns);
        Assert.False(IsValid("grid", "auto-flow / auto-flow"));
        Assert.False(IsValid("grid", "dense / 10px"));
    }

    // ================= grid-auto-flow、order、justify / place =================

    [Theory]
    [InlineData("row", "row")]
    [InlineData("column", "column")]
    [InlineData("dense", "dense")]
    [InlineData("row dense", "dense")]
    [InlineData("dense column", "column dense")]
    public void Auto_flow(string value, string expected) => Assert.Equal(expected, Text("grid-auto-flow", value));

    [Theory]
    [InlineData("row column")]
    [InlineData("dense dense")]
    [InlineData("auto")]
    public void Invalid_auto_flow(string value) => Assert.False(IsValid("grid-auto-flow", value), value);

    [Theory]
    [InlineData("3", 3)]
    [InlineData("-2", -2)]
    [InlineData("calc(1 + 1)", 2)]
    [InlineData("0", 0)]
    public void Order(string value, int expected) => Assert.Equal(expected, Compute("order", value).Order);

    [Theory]
    [InlineData("1.5")]
    [InlineData("auto")]
    [InlineData("1px")]
    public void Invalid_order(string value) => Assert.False(IsValid("order", value), value);

    [Fact]
    public void Justify_items_and_self()
    {
        Assert.Equal(CssJustifyItems.Legacy, ComputedStyle.Initial.JustifyItems);
        Assert.Equal(CssJustifySelf.Auto, ComputedStyle.Initial.JustifySelf);
        Assert.Equal(CssJustifyItems.Center, Compute("justify-items", "center").JustifyItems);
        Assert.Equal(CssJustifySelf.Right, Compute("justify-self", "right").JustifySelf);
        Assert.False(IsValid("justify-self", "legacy"));
        Assert.False(IsValid("justify-items", "auto"));
    }

    [Fact]
    public void Place_shorthands_parse_each_side_with_its_own_keywords()
    {
        var s = Compute("place-items", "center");
        Assert.Equal(CssAlignItems.Center, s.AlignItems);
        Assert.Equal(CssJustifyItems.Center, s.JustifyItems);
        var t = Compute("place-self", "start end");
        Assert.Equal(CssAlignSelf.Start, t.AlignSelf);
        Assert.Equal(CssJustifySelf.End, t.JustifySelf);
        var c = Compute("place-content", "space-between center");
        Assert.Equal(CssAlignContent.SpaceBetween, c.AlignContent);
        Assert.Equal(CssJustifyContent.Center, c.JustifyContent);
        Assert.False(IsValid("place-items", "center center center"));
        Assert.False(IsValid("place-self", "auto legacy"));
    }

    // ================= image-rendering 旧别名 =================

    [Theory]
    [InlineData("optimizeSpeed", CssImageRendering.CrispEdges, "crisp-edges")]
    [InlineData("OPTIMIZESPEED", CssImageRendering.CrispEdges, "crisp-edges")]
    [InlineData("optimizeQuality", CssImageRendering.Smooth, "smooth")]
    public void Image_rendering_legacy_aliases(string value, CssImageRendering expected, string text)
    {
        // CSS Images 3 §5.3：必须接受 optimizeSpeed / optimizeQuality，行为分别同 crisp-edges / smooth（这里作为解析时的别名）
        Assert.True(IsValid("image-rendering", value));
        Assert.Equal(expected, Compute("image-rendering", value).ImageRendering);
        Assert.Equal(text, Text("image-rendering", value));
    }

    // ================= 逻辑属性（横排、从左到右） =================

    [Fact]
    public void Logical_properties_map_to_physical_longhands()
    {
        var s = Compute("margin-inline-start", "5px", extra:
            "margin-inline-end: 6px; margin-block: 1px 2px; padding-inline: 3px; padding-block-end: 4%; inset-inline-start: 7px; inset-block: 8px 9px;" +
            "border-inline-start: 2px solid red; border-block-width: 3px; border-block-style: dashed; border-inline-end-color: blue;");
        Assert.Equal("5px", s.GetPropertyValueText("margin-left"));
        Assert.Equal("6px", s.GetPropertyValueText("margin-right"));
        Assert.Equal("1px", s.GetPropertyValueText("margin-top"));
        Assert.Equal("2px", s.GetPropertyValueText("margin-bottom"));
        Assert.Equal("3px", s.GetPropertyValueText("padding-left"));
        Assert.Equal("3px", s.GetPropertyValueText("padding-right"));
        Assert.Equal("4%", s.GetPropertyValueText("padding-bottom"));
        Assert.Equal("7px", s.GetPropertyValueText("left"));
        Assert.Equal("8px", s.GetPropertyValueText("top"));
        Assert.Equal("9px", s.GetPropertyValueText("bottom"));
        Assert.Equal("2px", s.GetPropertyValueText("border-left-width"));
        Assert.Equal(CssLineStyle.Solid, s.BorderLeftStyle);
        Assert.Equal("3px", s.GetPropertyValueText("border-top-width"));
        Assert.Equal(CssLineStyle.Dashed, s.BorderBottomStyle);
        Assert.Equal(Text("border-right-color", "blue"), s.GetPropertyValueText("border-right-color"));
        var b = Compute("border-inline", "1px dotted");
        Assert.Equal(CssLineStyle.Dotted, b.BorderLeftStyle);
        Assert.Equal(CssLineStyle.Dotted, b.BorderRightStyle);
        Assert.Equal(CssLineStyle.None, b.BorderTopStyle);
    }

    [Fact]
    public void Logical_and_physical_declarations_cascade_in_order()
    {
        Assert.Equal("2px", Compute("margin-inline-start", "2px", extra: "margin-left: 1px;").GetPropertyValueText("margin-left"));
        Assert.Equal("1px", Compute("margin-left", "1px", extra: "margin-inline-start: 2px;").GetPropertyValueText("margin-left"));
        Assert.False(IsValid("margin-inline-start", "1px 2px"));
        Assert.False(IsValid("padding-inline", "-1px"));
    }

    [Fact]
    public void Grid_properties_are_phase_one()
    {
        foreach (var name in new[]
                 {
                     "order", "grid-template-columns", "grid-template-rows", "grid-template-areas", "grid-auto-columns", "grid-auto-rows",
                     "grid-auto-flow", "grid-row-start", "grid-row-end", "grid-column-start", "grid-column-end", "grid-row", "grid-column",
                     "grid-area", "grid-template", "grid", "justify-items", "justify-self", "place-items", "place-self", "place-content",
                     "margin-inline-start", "padding-block", "inset-inline", "border-inline-start", "border-block-color",
                 })
        {
            Assert.True(CssProperties.TryGet(name, out var p), name);
            Assert.Equal(1, p.Phase);
            Assert.False(CssProperties.TryGetPlannedPhase(name, out _), name);
        }
        Assert.Equal(CssPropertyGroup.Flex, CssProperties.Get(CssPropertyId.GridTemplateColumns).Group);
        Assert.Equal(StyleDifference.Layout, CssProperties.Get(CssPropertyId.Order).Affects);
        Assert.Empty(Diagnostics("display: grid; grid-template: 'a' 1fr / auto; grid-area: a; order: 2; place-items: center"));
    }

    [Fact]
    public void Initial_values()
    {
        var i = ComputedStyle.Initial;
        Assert.True(i.GridTemplateColumns.IsNone);
        Assert.True(i.GridTemplateAreas.IsNone);
        Assert.Equal("auto", i.GetPropertyValueText("grid-auto-columns"));
        Assert.Equal(CssGridAutoFlow.Row, i.GridAutoFlow);
        Assert.True(i.GridRowStart.IsAuto);
        Assert.Equal(0, i.Order);
        Assert.Equal("legacy", i.GetPropertyValueText("justify-items"));
    }

    [Fact]
    public void Grid_values_support_var_substitution()
    {
        var s = Compute("grid-template-columns", "var(--cols)", extra: "--cols: repeat(2, 1fr);");
        Assert.Equal("repeat(2, 1fr)", s.GetPropertyValueText("grid-template-columns"));
        var t = Compute("grid-area", "var(--a)", extra: "--a: 1 / 2;");
        Assert.Equal("2", t.GetPropertyValueText("grid-column-start"));
    }
}
