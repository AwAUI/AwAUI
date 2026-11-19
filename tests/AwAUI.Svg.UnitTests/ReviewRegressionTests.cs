using System.Diagnostics;
using System.Text;
using AwAUI.Graphics;

namespace AwAUI.Svg.UnitTests;

/// <summary>独立评审发现的问题的回归测试。</summary>
public class ReviewRegressionTests
{
    const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    static (byte R, byte G, byte B, byte A) Probe(SvgDocument doc, int x, int y, int size = 100)
    {
        using var bmp = SvgGolden.Render(doc, size, size, null, Color.Transparent);
        return SvgGolden.Pixel(bmp, x, y);
    }

    static void Bounded(string text, double maxSeconds, long maxBytes)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var doc = SvgDocument.Parse(text);
        var list = doc.ToDisplayList(new Rect(0, 0, 32, 32));
        using (SvgGolden.Render(list, 32, 32)) { }
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sw.Elapsed.TotalSeconds < maxSeconds, $"took {sw.Elapsed}");
        Assert.True(allocated < maxBytes, $"allocated {allocated / (1 << 20)} MiB");
    }

    [Fact]
    public void Clip_path_with_many_empty_children_referenced_many_times_is_bounded()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><clipPath id=\"c\">");
        for (int i = 0; i < 90_000; i++) sb.Append("<rect/>");
        sb.Append("<rect width=\"5\" height=\"5\"/></clipPath></defs>");
        for (int i = 0; i < 90_000; i++) sb.Append("<rect width=\"1\" height=\"1\" clip-path=\"url(#c)\"/>");
        sb.Append("</svg>");
        Bounded(sb.ToString(), 20, 2L << 30);
    }

    [Fact]
    public void Gradient_with_many_children_referenced_many_times_is_bounded()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><linearGradient id=\"g\">");
        for (int i = 0; i < 90_000; i++) sb.Append("<desc/>");
        sb.Append("<stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient></defs>");
        for (int i = 0; i < 90_000; i++) sb.Append("<rect width=\"1\" height=\"1\" fill=\"url(#g)\"/>");
        sb.Append("</svg>");
        Bounded(sb.ToString(), 20, 2L << 30);
    }

    [Fact]
    public void Gradient_with_many_stops_referenced_many_times_is_bounded()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x2=\"10\">");
        for (int i = 0; i < 1024; i++) sb.Append("<stop offset=\"").Append(i / 1024.0).Append("\" stop-color=\"").Append(i % 2 == 0 ? "red" : "blue").Append("\"/>");
        sb.Append("</linearGradient></defs>");
        for (int i = 0; i < 90_000; i++) sb.Append("<rect x=\"").Append(i % 10).Append("\" width=\"1\" height=\"1\" fill=\"url(#g)\" stroke=\"url(#g)\"/>");
        sb.Append("</svg>");
        Bounded(sb.ToString(), 20, 2L << 30);
    }

    static SvgDocument Doc(string body, string attrs = "viewBox=\"0 0 10 10\"") =>
        SvgDocument.Parse($"<svg {Ns} {attrs}>{body}</svg>");

    static void AssertRgb((byte R, byte G, byte B, byte A) px, byte r, byte g, byte b)
    {
        Assert.True(px.A > 250 && Math.Abs(px.R - r) <= 2 && Math.Abs(px.G - g) <= 2 && Math.Abs(px.B - b) <= 2,
            $"expected #{r:x2}{g:x2}{b:x2}, got #{px.R:x2}{px.G:x2}{px.B:x2}{px.A:x2}");
    }

    // ---------------------------------------------------------------- 有界性

    [Fact]
    public void Use_fan_out_of_a_huge_path_is_bounded_by_the_render_budget()
    {
        // 一条 100 万段的路径被 use 引用 5 万次：每个实例都要光栅化一遍；以前预算耗尽后仍为每个实例分配一份「弦」路径
        var d = new StringBuilder("M0 0");
        for (int i = 0; i < 1_000_000; i++) d.Append(" L1 ").Append(i % 7);
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><path id=\"p\" d=\"{d}\"/></defs>");
        for (int i = 0; i < 50_000; i++) sb.Append("<use href=\"#p\"/>");
        sb.Append("</svg>");
        Bounded(sb.ToString(), 30, 2L << 30);
        var doc = SvgDocument.Parse(sb.ToString());
        Assert.Contains(doc.Diagnostics, x => x.Code == SvgDiagnosticCode.LimitExceeded && x.Message.Contains("budget"));
    }

    [Fact]
    public void Class_matching_cost_is_budgeted_and_duplicate_classes_match_once()
    {
        // 20 万个类的复合选择器 × 20 万个类的元素：以前每次匹配是 O(选择器类数 × 元素类数)，约 4e10 步
        var sel = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sel.Append(".x");
        sel.Append(".y{fill:red}");
        var cls = new StringBuilder();
        for (int i = 0; i < 200_000; i++) cls.Append("z ");
        cls.Append('x');
        Bounded($"<svg {Ns} viewBox=\"0 0 10 10\"><style>{sel}</style><rect class=\"{cls}\" width=\"5\" height=\"5\"/><rect class=\"{cls}\" width=\"5\" height=\"5\"/></svg>",
            20, 1L << 30);

        // 重复的类名不改变结果
        var doc = Doc("<style>.a{fill:#00ff00}</style><rect class=\"a a a\" width=\"10\" height=\"10\"/>");
        AssertRgb(Probe(doc, 5, 5, 10), 0, 255, 0);
    }

    [Fact]
    public void Long_style_rule_matching_every_element_is_linear()
    {
        // 以前层叠时对每个元素逐条应用规则的全部声明：50 万条声明 × 4 万个元素 = 2e10 步
        var css = new StringBuilder("* {");
        for (int i = 0; i < 500_000; i++) css.Append(i % 2 == 0 ? "fill:red;" : "fill:blue;");
        css.Append('}');
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><style>{css}</style>");
        for (int i = 0; i < 40_000; i++) sb.Append("<g/>");
        sb.Append("<rect width=\"10\" height=\"10\"/></svg>");
        Bounded(sb.ToString(), 20, 1L << 30);
        AssertRgb(Probe(SvgDocument.Parse(sb.ToString()), 50, 50), 0, 0, 255);
    }

    [Fact]
    public void Skipped_children_of_a_reused_group_count_against_the_node_budget()
    {
        // 组里 9 万个不渲染的子元素（未知元素、title、switch 中被跳过的分支）被 use 引用上万次：
        // 以前它们不计入渲染节点预算，每个实例都完整遍历一遍
        foreach (var child in new[] { "<foo/>", "<title/>", "<linearGradient/>" })
        {
            var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><g id=\"g\"><rect width=\"1\" height=\"1\"/>");
            for (int i = 0; i < 90_000; i++) sb.Append(child);
            sb.Append("</g></defs>");
            for (int i = 0; i < 50_000; i++) sb.Append("<use href=\"#g\"/>");
            sb.Append("</svg>");
            Bounded(sb.ToString(), 20, 1L << 30);
        }
        var sw = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><switch id=\"s\">");
        for (int i = 0; i < 90_000; i++) sw.Append("<g requiredExtensions=\"x\"/>");
        sw.Append("<rect width=\"1\" height=\"1\"/></switch></defs>");
        for (int i = 0; i < 50_000; i++) sw.Append("<use href=\"#s\"/>");
        sw.Append("</svg>");
        Bounded(sw.ToString(), 20, 1L << 30);
    }

    [Fact]
    public void Clip_path_referenced_from_alternating_viewports_is_bounded()
    {
        // clipPath 模板按视口缓存；在两个不同尺寸的嵌套 svg 之间交替引用会反复重建模板
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><clipPath id=\"c\">");
        for (int i = 0; i < 60_000; i++) sb.Append("<rect/>");
        sb.Append("<rect width=\"50%\" height=\"50%\"/></clipPath></defs>");
        for (int i = 0; i < 30_000; i++)
            sb.Append("<svg width=\"").Append(i % 2 == 0 ? "5" : "6").Append("\"><rect width=\"1\" height=\"1\" clip-path=\"url(#c)\"/></svg>");
        sb.Append("</svg>");
        Bounded(sb.ToString(), 20, 2L << 30);
    }

    [Fact]
    public void Huge_clip_geometry_is_harmless()
    {
        foreach (var body in new[]
        {
            "<clipPath id=\"c\"><circle cx=\"1e38\" r=\"1e38\"/></clipPath><rect width=\"10\" height=\"10\" clip-path=\"url(#c)\"/>",
            "<clipPath id=\"c\"><path d=\"M-1e30 -1e30 L1e30 0 L0 1e30z\"/></clipPath><rect width=\"10\" height=\"10\" clip-path=\"url(#c)\"/>",
            "<clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"1e30\" height=\"1e30\" x=\"-1e29\"/></clipPath><rect width=\"10\" height=\"10\" clip-path=\"url(#c)\"/>",
            "<svg x=\"-1e30\" width=\"1e31\" height=\"1e31\"><rect width=\"10\" height=\"10\"/></svg>",
        })
        {
            foreach (var vb in new[] { "0 0 240 160", "0 0 1e-3 1e-3", "0 0 1e9 1e9" })
            {
                var doc = SvgDocument.Parse($"<svg {Ns} viewBox=\"{vb}\">{body}</svg>");
                foreach (var size in new[] { 1, 40, 300 })
                    using (SvgGolden.Render(doc, size, size)) { }
            }
        }
    }

    [Fact]
    public void Maximum_depth_with_layers_and_use_fits_a_1_MiB_stack()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><defs><rect id=\"r\" width=\"1\" height=\"1\" opacity=\"0.9\"/></defs>");
        for (int i = 0; i < 1000; i++) sb.Append("<g opacity=\"0.99\" transform=\"translate(0.001)\">");
        sb.Append("<use href=\"#r\"/>");
        for (int i = 0; i < 1000; i++) sb.Append("</g>");
        sb.Append("</svg>");
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var doc = SvgDocument.Parse(sb.ToString(), new SvgParseOptions { MaxDepth = 1024 });
                using (SvgGolden.Render(doc, 16, 16)) { }
            }
            catch (Exception e) { error = e; }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        Assert.Null(error);
    }

    [Fact]
    public void Gradient_stop_count_is_capped()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox=\"0 0 10 10\"><linearGradient id=\"g\">");
        for (int i = 0; i < 300; i++) sb.Append("<stop offset=\"").Append(i / 300.0).Append("\" stop-color=\"red\"/>");
        sb.Append("</linearGradient><rect width=\"10\" height=\"10\" fill=\"url(#g)\"/></svg>");
        var doc = SvgDocument.Parse(sb.ToString());
        Assert.Contains(doc.Diagnostics, x => x.Code == SvgDiagnosticCode.LimitExceeded && x.Message.Contains("stops"));
        AssertRgb(Probe(doc, 5, 5, 10), 255, 0, 0);
    }

    // ---------------------------------------------------------------- 语义

    [Fact]
    public void ClipPath_transform_applies_after_the_object_bounding_box_mapping()
    {
        // 浏览器与 resvg：内容先按包围盒映射（0.5 → 5 用户单位），再应用 clipPath 的 transform（右移 5）。
        // 以前顺序相反：先平移 5 个「包围盒单位」再放大，裁剪区域整个跑到画面外。
        var doc = Doc("""
            <clipPath id="c" clipPathUnits="objectBoundingBox" transform="translate(5 0)">
              <rect width="0.5" height="1"/>
            </clipPath>
            <rect width="10" height="10" fill="#ff0000" clip-path="url(#c)"/>
            """);
        AssertRgb(Probe(doc, 7, 5, 10), 255, 0, 0);
        Assert.Equal(0, Probe(doc, 2, 5, 10).A);

        // 用户空间单位下 transform 照常作用于内容
        var user = Doc("""
            <clipPath id="c" transform="translate(5 0)"><rect width="5" height="10"/></clipPath>
            <rect width="10" height="10" fill="#ff0000" clip-path="url(#c)"/>
            """);
        AssertRgb(Probe(user, 7, 5, 10), 255, 0, 0);
        Assert.Equal(0, Probe(user, 2, 5, 10).A);
    }

    [Fact]
    public void Use_referencing_its_own_ancestor_is_not_rendered()
    {
        // 以前要等祖先作为 use 目标再次进入时才发现环，结果多画了一份平移后的副本
        var doc = Doc("""<g id="a"><rect width="2" height="2" fill="#ff0000"/><use href="#a" x="5"/></g>""");
        Assert.Contains(doc.Diagnostics, x => x.Code == SvgDiagnosticCode.CircularReference);
        AssertRgb(Probe(doc, 1, 1, 10), 255, 0, 0);
        Assert.Equal(0, Probe(doc, 6, 1, 10).A);
    }

    [Fact]
    public void Css_comments_are_whitespace_in_declarations_and_selectors()
    {
        var doc = Doc("""
            <style>rect /* 注释 */ { fill: /* a; b */ #00ff00 /* c */ ; }</style>
            <rect width="5" height="10"/>
            <rect x="5" width="5" height="10" style="fill:/* x */#0000ff/**/"/>
            """);
        Assert.Empty(doc.Diagnostics);
        AssertRgb(Probe(doc, 2, 5, 10), 0, 255, 0);
        AssertRgb(Probe(doc, 7, 5, 10), 0, 0, 255);
    }

    [Fact]
    public void Invalid_css_declarations_do_not_override_presentation_attributes()
    {
        // CSS 在解析时就丢弃无效声明：写错的规则不能把有效的 fill 属性盖成「无效 → 继承的黑色」
        var doc = Doc("""
            <style>rect { fill: var(--brand) }</style>
            <rect width="5" height="10" fill="#00ff00"/>
            <rect x="5" width="5" height="10" fill="#00ff00" style="fill: bogus"/>
            """);
        Assert.Contains(doc.Diagnostics, x => x.Code == SvgDiagnosticCode.InvalidValue);
        AssertRgb(Probe(doc, 2, 5, 10), 0, 255, 0);
        AssertRgb(Probe(doc, 7, 5, 10), 0, 255, 0);
    }
}
