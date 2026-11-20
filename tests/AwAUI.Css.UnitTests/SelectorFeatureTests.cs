using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.UnitTests.Cascade;

namespace AwAUI.Css.UnitTests;

/// <summary>第九批的选择器：<c>:host</c>、表单 / 文档状态伪类、<c>:dir() :lang()</c>、新伪元素与伪元素之后的状态伪类。</summary>
public class SelectorFeatureTests
{
    static bool M(string selector, StyleElement e)
    {
        var list = SelectorParser.Parse(selector);
        Assert.NotNull(list);
        return SelectorMatcher.Matches(list!, e);
    }

    [Fact]
    public void Host_never_matches_but_keeps_the_list_valid()
    {
        var root = new El("html");
        var list = SelectorParser.Parse(":root, :host");
        Assert.NotNull(list);
        Assert.True(SelectorMatcher.Matches(list!, root));
        Assert.False(M(":host", root));
        Assert.False(M(":host(.a)", root));
        Assert.False(M(":host-context(.a)", root));
        Assert.Null(SelectorParser.Parse(":host(.a .b)"));
        // Tailwind v4 的主题规则 :root, :host 现在保留，令牌可用
        var engine = CascadeTestHelper.Engine(":root, :host { --primary: oklch(0.205 0 0) } div { color: var(--primary) }");
        var map = CascadeTestHelper.ResolveTree(engine, new El("html").Add(new El("div")));
        Assert.Equal("rgb(23, 23, 23)", map.Values.Last().GetPropertyValueText("color"));
    }

    [Theory]
    [InlineData(PseudoClass.Required, ":required")]
    [InlineData(PseudoClass.Optional, ":optional")]
    [InlineData(PseudoClass.Valid, ":valid")]
    [InlineData(PseudoClass.Invalid, ":invalid")]
    [InlineData(PseudoClass.UserValid, ":user-valid")]
    [InlineData(PseudoClass.UserInvalid, ":user-invalid")]
    [InlineData(PseudoClass.ReadWrite, ":read-write")]
    [InlineData(PseudoClass.Indeterminate, ":indeterminate")]
    [InlineData(PseudoClass.Default, ":default")]
    [InlineData(PseudoClass.InRange, ":in-range")]
    [InlineData(PseudoClass.OutOfRange, ":out-of-range")]
    [InlineData(PseudoClass.Autofill, ":autofill")]
    [InlineData(PseudoClass.Autofill, ":-webkit-autofill")]
    [InlineData(PseudoClass.Open, ":open")]
    [InlineData(PseudoClass.PopoverOpen, ":popover-open")]
    [InlineData(PseudoClass.Modal, ":modal")]
    [InlineData(PseudoClass.Target, ":target")]
    [InlineData(PseudoClass.Visited, ":visited")]
    [InlineData(PseudoClass.Link, ":link")]
    [InlineData(PseudoClass.AnyLink, ":any-link")]
    public void State_pseudo_classes_use_the_element_hook(PseudoClass pc, string selector)
    {
        var e = new El("input");
        Assert.False(M(selector, e));
        e.StatePseudoClasses.Add(pc);
        Assert.True(M(selector, e));
        Assert.Equal(new Specificity(0, 1, 0), SelectorParser.Parse(selector)![0].Specificity);
    }

    [Fact]
    public void Default_states_follow_the_spec()
    {
        var e = new El("div");
        Assert.True(M(":read-only", e));
        Assert.False(M(":read-write", e));
        Assert.True(M(":defined", e));
        // 基类默认实现：没有重写钩子的适配器
        Assert.True(new BareElement().MatchesStatePseudoClass(PseudoClass.ReadOnly));
        Assert.False(new BareElement().MatchesStatePseudoClass(PseudoClass.Invalid));
    }

    sealed class BareElement : StyleElement
    {
        public override string Tag => "x";
        public override string? Id => null;
        public override IReadOnlyList<string> Classes => Array.Empty<string>();
        public override string? GetAttribute(string name) => null;
        public override StyleElement? Parent => null;
        public override StyleElement? PreviousSibling => null;
        public override StyleElement? NextSibling => null;
        public override PseudoStates States => PseudoStates.None;
        public override bool IsEmpty => true;
    }

    [Fact]
    public void Dir_and_lang()
    {
        var root = new El("html") { Lang = "zh-Hans-CN" };
        var child = new El("p") { Dir = StyleDirection.Rtl };
        root.Add(child);
        Assert.True(M(":dir(ltr)", root));
        Assert.True(M(":dir(rtl)", child));
        Assert.False(M(":dir(ltr)", child));
        Assert.Null(SelectorParser.Parse(":dir(up)"));
        Assert.True(M(":lang(zh)", child));
        Assert.True(M(":lang(zh-Hans)", child));
        Assert.True(M(":lang(\"en\", zh)", child));
        Assert.False(M(":lang(zh-Hant)", child));
        Assert.False(M(":lang(z)", child));
        Assert.True(M(":lang(\"*-Hans\")", child));
    }

    [Theory]
    [InlineData("::marker", PseudoElementKind.Marker)]
    [InlineData("::backdrop", PseudoElementKind.Backdrop)]
    [InlineData("::file-selector-button", PseudoElementKind.FileSelectorButton)]
    [InlineData("::first-letter", PseudoElementKind.FirstLetter)]
    [InlineData(":first-letter", PseudoElementKind.FirstLetter)]
    [InlineData("::first-line", PseudoElementKind.FirstLine)]
    [InlineData("::details-content", PseudoElementKind.DetailsContent)]
    [InlineData("::placeholder", PseudoElementKind.Placeholder)]
    [InlineData("::selection", PseudoElementKind.Selection)]
    [InlineData("::-webkit-scrollbar", PseudoElementKind.Vendor)]
    public void Pseudo_elements_parse(string selector, PseudoElementKind kind)
    {
        var list = SelectorParser.Parse("input" + selector);
        Assert.NotNull(list);
        Assert.Equal(kind, list![0].PseudoElement!.PseudoElement);
        Assert.Equal(new Specificity(0, 0, 2), list[0].Specificity);
    }

    [Fact]
    public void Pseudo_elements_cascade_only_when_requested()
    {
        var engine = CascadeTestHelper.Engine("li::marker { color: red } input::file-selector-button { color: blue } input::file-selector-button:hover { color: green }"
            + " dialog::backdrop { background-color: black } p::first-letter { font-size: 32px } ::-webkit-scrollbar { width: 1px } li { color: gray }");
        var li = new El("li");
        var input = new El("input");
        var dialog = new El("dialog");
        var p = new El("p");
        var root = new El("body").Add(li, input, dialog, p);
        var rs = engine.Resolve(root, null);
        var ls = engine.Resolve(li, rs);
        Assert.Equal("rgb(128, 128, 128)", ls.GetPropertyValueText("color"));
        Assert.Equal("rgb(255, 0, 0)", engine.Resolve(li, ls, PseudoElementKind.Marker, null).GetPropertyValueText("color"));
        var ins = engine.Resolve(input, rs);
        Assert.Equal("rgb(0, 0, 0)", ins.GetPropertyValueText("color"));
        Assert.Equal("rgb(0, 0, 255)", engine.Resolve(input, ins, PseudoElementKind.FileSelectorButton, null).GetPropertyValueText("color"));
        input.State = PseudoStates.Hover;
        Assert.Equal("rgb(0, 128, 0)", engine.Resolve(input, ins, PseudoElementKind.FileSelectorButton, null).GetPropertyValueText("color"));
        var ds = engine.Resolve(dialog, rs);
        Assert.Equal("rgb(0, 0, 0)", engine.Resolve(dialog, ds, PseudoElementKind.Backdrop, null).GetPropertyValueText("background-color"));
        var ps = engine.Resolve(p, rs);
        Assert.Equal("32px", engine.Resolve(p, ps, PseudoElementKind.FirstLetter, null).GetPropertyValueText("font-size"));
        // ::-webkit-* 有效但永不匹配；不影响同一规则的其他选择器
        Assert.Equal("auto", ps.GetPropertyValueText("width"));
    }

    [Fact]
    public void Tailwind_preflight_selector_lists_survive()
    {
        var s = StyleSheet.Parse("*, ::after, ::before, ::backdrop, ::file-selector-button { box-sizing: border-box }"
            + " ::-webkit-date-and-time-value { min-height: 1lh } :-moz-focusring { outline: auto }"
            + " input:where([type=checkbox]):indeterminate { color: red } details > summary::-webkit-details-marker { display: none }");
        Assert.Equal(4, s.Rules.Count); // :-moz-focusring 与 Chromium 一样使整条规则无效
        var first = Assert.IsType<CssStyleRule>(s.Rules[0]);
        Assert.Equal(5, first.Selectors.Count);
    }
}
