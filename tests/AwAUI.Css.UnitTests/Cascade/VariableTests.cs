using System.Diagnostics;
using AwAUI.Css.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

public class VariableTests
{
    static (ComputedStyle Parent, ComputedStyle Child) Resolve(string css, CssEnvironment? env = null)
    {
        var e = new StyleEngine(env);
        e.AddStyleSheet(StyleSheet.Parse(css));
        var child = new El("div");
        var root = new El("section").Add(child);
        var ps = e.Resolve(root, null);
        return (ps, e.Resolve(child, ps));
    }

    [Fact]
    public void Basic_substitution_and_inheritance()
    {
        var (p, c) = Resolve("section { --gap: 4px; --c: red } div { margin-top: var(--gap); color: var(--c) }");
        Assert.Equal("4px", c.GetPropertyValueText("margin-top"));
        Assert.Equal("rgb(255, 0, 0)", c.GetPropertyValueText("color"));
        Assert.Equal("4px", c.GetCustomPropertyText("--gap"));
        // 子元素没有声明自定义属性：与父元素共享同一映射
        Assert.Same(p.CustomProperties, c.CustomProperties);
    }

    [Fact]
    public void Substitution_inside_functions_and_calc()
    {
        var (_, c) = Resolve("div { --n: 3; --w: 10px; width: calc(var(--w) * var(--n)); background-color: rgb(var(--n) 0 0) }");
        Assert.Equal("30px", c.GetPropertyValueText("width"));
        Assert.Equal("rgb(3, 0, 0)", c.GetPropertyValueText("background-color"));
    }

    [Fact]
    public void Fallbacks()
    {
        var (_, c) = Resolve("div { width: var(--missing, 7px); height: var(--missing, var(--also-missing, 8px)); --e: ; min-width: var(--x,) 3px }");
        Assert.Equal("7px", c.GetPropertyValueText("width"));
        Assert.Equal("8px", c.GetPropertyValueText("height"));
        Assert.Equal("3px", c.GetPropertyValueText("min-width"));
    }

    [Fact]
    public void Missing_variable_without_fallback_is_invalid_at_computed_value_time()
    {
        var (_, c) = Resolve("section { color: green; width: 5px } div { color: red; color: var(--nope); width: 9px; width: var(--nope) }");
        // 计算值时无效 → unset：继承属性取父值，非继承属性取初始值（而不是回退到被覆盖的声明）
        Assert.Equal("rgb(0, 128, 0)", c.GetPropertyValueText("color"));
        Assert.Equal("auto", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Substituted_value_that_does_not_parse_is_invalid()
    {
        var (_, c) = Resolve("div { --v: blue; width: var(--v) }");
        Assert.Equal("auto", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Cycles_make_all_members_invalid()
    {
        var (_, c) = Resolve("div { --a: var(--b); --b: var(--a); --self: var(--self, 1px); --c: var(--a, 5px); --d: var(--b, 6px) 2px;"
                             + " width: var(--a, 11px); height: var(--self, 12px); min-width: var(--c); min-height: var(--d) }");
        Assert.Null(c.GetCustomPropertyText("--a"));
        Assert.Null(c.GetCustomPropertyText("--b"));
        Assert.Null(c.GetCustomPropertyText("--self"));
        Assert.Equal("11px", c.GetPropertyValueText("width"));
        Assert.Equal("12px", c.GetPropertyValueText("height"));
        // 引用循环但不在循环中的属性使用自己的回退
        Assert.Equal("5px", c.GetCustomPropertyText("--c"));
        Assert.Equal("5px", c.GetPropertyValueText("min-width"));
        Assert.Equal("6px 2px", c.GetCustomPropertyText("--d"));
    }

    [Fact]
    public void Cycle_member_with_fallback_is_still_invalid()
    {
        var (_, c) = Resolve("div { --a: var(--b, 1px); --b: var(--a, 2px); width: var(--a, 30px) }");
        Assert.Null(c.GetCustomPropertyText("--a"));
        Assert.Equal("30px", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Custom_property_is_computed_on_declaring_element()
    {
        // --a 在父元素上按父元素的 --b 计算；子元素继承的是计算结果
        var (p, c) = Resolve("section { --b: 1px; --a: var(--b) } div { --b: 2px; width: var(--a) }");
        Assert.Equal("1px", c.GetPropertyValueText("width"));
        Assert.Equal("2px", c.GetCustomPropertyText("--b"));
        Assert.NotSame(p.CustomProperties, c.CustomProperties);
    }

    [Fact]
    public void Custom_property_names_are_case_sensitive()
    {
        var (_, c) = Resolve("div { --A: 1px; --a: 2px; width: var(--A) }");
        Assert.Equal("1px", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Initial_and_inherit_on_custom_properties()
    {
        var (_, c) = Resolve("section { --x: 1px; --y: 2px } div { --x: initial; --y: inherit; width: var(--x, 9px); height: var(--y) }");
        Assert.Null(c.GetCustomPropertyText("--x"));
        Assert.Equal("9px", c.GetPropertyValueText("width"));
        Assert.Equal("2px", c.GetPropertyValueText("height"));
    }

    [Fact]
    public void Css_wide_keyword_from_fallback()
    {
        var (_, c) = Resolve("section { color: green } div { color: red; color: var(--none, inherit); width: var(--none, initial) }");
        Assert.Equal("rgb(0, 128, 0)", c.GetPropertyValueText("color"));
        Assert.Equal("auto", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Env_safe_area_insets()
    {
        var env = new CssEnvironment { SafeAreaInsetTop = 44, SafeAreaInsetBottom = 34 };
        var (_, c) = Resolve("div { padding-top: env(safe-area-inset-top); padding-bottom: calc(env(safe-area-inset-bottom) + 8px);"
                             + " padding-left: env(unknown-thing, 5px); padding-right: env(SAFE-AREA-INSET-LEFT) }", env);
        Assert.Equal("44px", c.GetPropertyValueText("padding-top"));
        Assert.Equal("42px", c.GetPropertyValueText("padding-bottom"));
        Assert.Equal("5px", c.GetPropertyValueText("padding-left"));
        Assert.Equal("0px", c.GetPropertyValueText("padding-right"));
        Assert.True(c.Flags.HasFlag(ComputedStyleFlags.DependsOnEnvironment));
    }

    [Fact]
    public void Exponential_expansion_is_bounded()
    {
        // 每一层把上一层复制 4 份：不加限制会产生 4^40 个记号
        var sb = new System.Text.StringBuilder("div { --v0: 1px;");
        for (int i = 1; i <= 40; i++) sb.Append($" --v{i}: var(--v{i - 1}) var(--v{i - 1}) var(--v{i - 1}) var(--v{i - 1});");
        sb.Append(" --ok: var(--v3); width: var(--v40, 3px) }");
        var sw = Stopwatch.StartNew();
        var (_, c) = Resolve(sb.ToString());
        sw.Stop();
        Assert.Equal("3px", c.GetPropertyValueText("width"));
        Assert.Null(c.GetCustomPropertyText("--v40"));
        Assert.NotNull(c.GetCustomPropertyText("--ok"));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Total_substitution_per_element_is_bounded()
    {
        // 每个属性都在单值上限之内，但合计远超每元素上限：总量被截断，耗时与内存有界
        var sb = new System.Text.StringBuilder("div { --b: " + string.Join(" ", Enumerable.Repeat("x", 1000)) + ";");
        var refs = string.Join(" ", Enumerable.Repeat("var(--b)", 30));
        for (int i = 0; i < 300; i++) sb.Append($" --w{i}: {refs};");
        sb.Append(" width: 4px }");
        var sw = Stopwatch.StartNew();
        var (_, c) = Resolve(sb.ToString());
        sw.Stop();
        Assert.Equal("4px", c.GetPropertyValueText("width"));
        int defined = 0;
        for (int i = 0; i < 300; i++) if (c.GetCustomPropertyText("--w" + i) is not null) defined++;
        Assert.InRange(defined, 1, 299);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Long_reference_chain_does_not_overflow_the_stack()
    {
        var sb = new System.Text.StringBuilder("div { --v0: 1px;");
        for (int i = 1; i <= 3000; i++) sb.Append($" --v{i}: var(--v{i - 1});");
        sb.Append(" width: var(--v3000, 2px); height: var(--v100) }");
        Exception? error = null;
        ComputedStyle? child = null;
        var t = new Thread(() =>
        {
            try { child = Resolve(sb.ToString()).Child; }
            catch (Exception ex) { error = ex; }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.Equal("2px", child!.GetPropertyValueText("width"));
        Assert.Equal("1px", child.GetPropertyValueText("height"));
    }

    [Fact]
    public void Custom_property_map_flattens_long_chains()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("* { --depth: x } .k { --k: y }"));
        var root = new El("r");
        var node = root;
        for (int i = 0; i < 30; i++)
        {
            var c = new El("e", classes: i % 2 == 0 ? "k" : null);
            node.Add(c);
            node = c;
        }
        ComputedStyle? s = null;
        for (StyleElementWalker w = new(node); w.Next(out var el);) s = e.Resolve(el, s);
        Assert.Equal("x", s!.GetCustomPropertyText("--depth"));
        Assert.Equal("y", s.GetCustomPropertyText("--k"));
        Assert.Equal(2, s.CustomProperties.Count);
    }

    /// <summary>从根到给定元素的遍历。</summary>
    struct StyleElementWalker
    {
        readonly List<El> _chain;
        int _i;

        public StyleElementWalker(El leaf)
        {
            _chain = new List<El>();
            for (Selectors.StyleElement? x = leaf; x is not null; x = x.Parent) _chain.Add((El)x);
            _chain.Reverse();
            _i = 0;
        }

        public bool Next(out El el)
        {
            el = _i < _chain.Count ? _chain[_i] : null!;
            return _i++ < _chain.Count;
        }
    }
}
