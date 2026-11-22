using AwAUI.Css;
using AwAUI.Css.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 应用样式表的热更新（design/23 机制 3、6）：<see cref="AppStyleSheet.Update"/> 改变内容版本与 <see cref="AppStyleSheets.Revision"/>，
/// 渲染管线在下一帧移除旧级联表、加入新表并全量重算样式；未变化的表不会重复加入。
/// </summary>
public class AppStyleSheetLiveTests
{
    static Color C(string hex) => Color.Parse(hex);

    static Color BackgroundOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.BackgroundColor);

    static List<CascadeStyleSheet> Applied(PipelineHarness h, AppStyleSheet sheet) =>
        h.Pipeline.StyleEngine.StyleSheets.Where(s => s.Sheet.SourceName == sheet.SourceName).ToList();

    static string Name(string tag) => "test:app-sheet:" + tag + ":" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Update_replaces_the_cascade_entry_and_restyles()
    {
        long revision = AppStyleSheets.Revision;
        var sheet = AppStyleSheets.Register(
            "#awaui-live-box { background-color: #112233; }", Name("live"));
        Assert.True(AppStyleSheets.Revision > revision);
        Assert.Equal(0, sheet.Version);

        var h = new PipelineHarness(new Div { Id = "awaui-live-box" });
        h.Frame();
        Assert.Equal(C("#112233"), BackgroundOf(h.El("awaui-live-box")));
        var before = Assert.Single(Applied(h, sheet));

        revision = AppStyleSheets.Revision;
        sheet.Update("#awaui-live-box { background-color: #ff0000; }");
        Assert.Equal(1, sheet.Version);
        Assert.True(AppStyleSheets.Revision > revision);
        Assert.Equal("#awaui-live-box { background-color: #ff0000; }", sheet.Text);

        h.Frame();
        Assert.Equal(C("#ff0000"), BackgroundOf(h.El("awaui-live-box")));
        // 旧表已移除：级联里只有一张同名表，且是新加入的句柄
        var after = Assert.Single(Applied(h, sheet));
        Assert.NotSame(before, after);
        Assert.Contains("background-color: #ff0000", after.Sheet.Text);
    }

    [Fact]
    public void Updating_one_sheet_does_not_readd_unchanged_sheets()
    {
        var first = AppStyleSheets.Register(
            "#awaui-live-first { background-color: #010203; }", Name("first"));
        var second = AppStyleSheets.Register(
            "#awaui-live-second { background-color: #040506; }", Name("second"));

        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div { Id = "awaui-live-first" },
                new Div { Id = "awaui-live-second" },
            ],
        });
        h.Frame();
        var firstBefore = Assert.Single(Applied(h, first));
        var secondBefore = Assert.Single(Applied(h, second));
        Assert.Equal(C("#010203"), BackgroundOf(h.El("awaui-live-first")));

        second.Update("#awaui-live-second { background-color: #070809; }");
        h.Frame();

        // 未变化的表仍是原来的级联句柄，没有重复加入
        Assert.Same(firstBefore, Assert.Single(Applied(h, first)));
        Assert.NotSame(secondBefore, Assert.Single(Applied(h, second)));
        Assert.Equal(C("#010203"), BackgroundOf(h.El("awaui-live-first")));
        Assert.Equal(C("#070809"), BackgroundOf(h.El("awaui-live-second")));
    }

    [Fact]
    public void Incremental_update_reuses_the_parsed_prefix_and_only_reparses_the_tail()
    {
        const string before =
            "#awaui-inc-a { background-color: #112233; }\n#awaui-inc-b { background-color: #445566; }\n";
        var sheet = AppStyleSheets.Register(before, Name("incremental"));
        var first = sheet.StyleSheet;
        Assert.Equal(2, first.Rules.Count);

        const string after =
            "#awaui-inc-a { background-color: #112233; }\n#awaui-inc-b { background-color: #778899; }\n#awaui-inc-c { background-color: #aabbcc; }\n";
        int changedAt = after.IndexOf("#awaui-inc-b", StringComparison.Ordinal);
        long revision = AppStyleSheets.Revision;
        sheet.Update(after, changedAt);

        Assert.Equal(1, sheet.Version);
        Assert.True(AppStyleSheets.Revision > revision);
        Assert.Equal(after, sheet.Text);

        var second = sheet.StyleSheet;
        Assert.NotSame(first, second);
        // 变化点之前的规则仍是同一个对象（绝对区间继续有效），尾部规则按新文本的绝对偏移解析
        Assert.Same(first.Rules[0], second.Rules[0]);
        Assert.Equal(3, second.Rules.Count);
        Assert.Equal(after.IndexOf("#awaui-inc-c", StringComparison.Ordinal), second.Rules[2].Range.Start.Offset);
    }

    [Fact]
    public void Incremental_update_with_a_stale_offset_falls_back_to_a_full_parse()
    {
        const string before = "#awaui-inc-x { background-color: #010203; }";
        var sheet = AppStyleSheets.Register(before, Name("incremental-fallback"));
        var first = sheet.StyleSheet;

        const string after = "#awaui-inc-y { background-color: #040506; }";
        // 偏移声称前缀相同，但实际不同：退回整段解析，结果仍正确
        sheet.Update(after, before.Length - 1);
        var second = sheet.StyleSheet;

        Assert.NotSame(first.Rules[0], second.Rules[0]);
        Assert.Equal(after, second.Text);
        Assert.Equal(StyleSheet.Parse(after).Rules[0].Range, second.Rules[0].Range);
    }

    [Fact]
    public void Back_to_back_incremental_updates_parse_the_latest_text()
    {
        const string text0 = "#awaui-inc-m { color: red; }\n";
        var sheet = AppStyleSheets.Register(text0, Name("incremental-twice"));
        Assert.Single(sheet.StyleSheet.Rules);

        const string text1 = "#awaui-inc-m { color: red; }\n#awaui-inc-n { margin: 0 }\n";
        const string text2 = "#awaui-inc-m { color: red; }\n#awaui-inc-n { margin: 1px }\n#awaui-inc-o { padding: 0 }\n";
        // 第一次增量尚未被读取时又来了第二次：无法叠加偏移，应安全地退化为整段解析
        sheet.Update(text1, text1.IndexOf("#awaui-inc-n", StringComparison.Ordinal));
        sheet.Update(text2, text2.IndexOf("#awaui-inc-n", StringComparison.Ordinal));

        Assert.Equal(2, sheet.Version);
        Assert.Equal(text2, sheet.Text);
        var parsed = sheet.StyleSheet;
        Assert.Equal(3, parsed.Rules.Count);
        Assert.Equal(text2.IndexOf("#awaui-inc-m", StringComparison.Ordinal), parsed.Rules[0].Range.Start.Offset);
    }

    [Fact]
    public void Incremental_update_rejects_an_out_of_range_offset()
    {
        var sheet = AppStyleSheets.Register(".a { color: red; }", Name("incremental-invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Update(".a { }", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Update(".a { }", 7));
        Assert.Throws<ArgumentNullException>(() => sheet.Update(null!, 0));
        Assert.Equal(0, sheet.Version);
    }

    [Fact]
    public void Repeated_update_with_same_text_still_bumps_version_revision_and_replaces()
    {
        var sheet = AppStyleSheets.Register("#awaui-live-same { background-color: #102030; }", Name("same"));
        var h = new PipelineHarness(new Div { Id = "awaui-live-same" });
        h.Frame();
        var first = Assert.Single(Applied(h, sheet));

        long revision = AppStyleSheets.Revision;
        sheet.Update(sheet.Text);
        Assert.Equal(1, sheet.Version);
        Assert.True(AppStyleSheets.Revision > revision);
        h.Frame();
        Assert.Equal(C("#102030"), BackgroundOf(h.El("awaui-live-same")));
        Assert.NotSame(first, Assert.Single(Applied(h, sheet)));
    }
}
