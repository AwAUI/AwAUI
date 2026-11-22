using AwAUI.Css;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;
using AwAUI.Tailwind.Runtime;
using Xunit;

namespace AwAUI.Tailwind.UnitTests;

/// <summary>
/// 运行时补全（design/23 第 3 层机制 3、6）：基线、按 <see cref="TailwindGeneratedRule.Index"/> 插入的规则顺序、主题变量与
/// <c>@property</c> / <c>@keyframes</c> 的追加、幂等、无效类名与关闭模式；并用 AwAUI.Css 解析生成的组合样式表验证层叠结果。
/// </summary>
public class TailwindRuntimeTests
{
    const string Entry = "@import \"tailwindcss\";";

    static TailwindOptions Options() => new() { ThemeRootSelector = ":root" };

    /// <summary>测试期间打开运行时补全（默认值取决于构建配置：Release 为 false）。</summary>
    static IDisposable RuntimeEnabled()
    {
        bool old = TailwindRuntime.Enabled;
        TailwindRuntime.Enabled = true;
        return new Restore(() => TailwindRuntime.Enabled = old);
    }

    static IDisposable RuntimeDisabled()
    {
        bool old = TailwindRuntime.Enabled;
        TailwindRuntime.Enabled = false;
        return new Restore(() => TailwindRuntime.Enabled = old);
    }

    sealed class Restore : IDisposable
    {
        readonly Action _restore;
        public Restore(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length) count++;
        return count;
    }

    [Fact]
    public void Baseline_candidates_are_part_of_the_stylesheet()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4", "bg-red-500" }, Options());
        Assert.Contains(".p-4 {", runtime.Css);
        Assert.Contains(".bg-red-500 {", runtime.Css);
        Assert.Contains("--color-red-500", runtime.Css);
        Assert.Contains("@layer utilities {", runtime.Css);
        // 基线也是编译器已知的候选：重复加入不产生变化
        Assert.False(runtime.AddCandidates(new[] { "p-4", "bg-red-500" }));
    }

    [Fact]
    public void Incremental_text_matches_a_full_recomposition()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        void Check() => Assert.Equal(runtime.Recompose(), runtime.Css);

        Check();
        // 规则在中间插入（m-2 排在 p-4 之前）
        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        Check();
        // 主题块插在已有主题块之后、全局块之前
        Assert.True(runtime.AddCandidates(new[] { "text-blue-500" }));
        Check();
        // 第一个全局块追加在末尾
        Assert.True(runtime.AddCandidates(new[] { "animate-spin" }));
        Check();
        // 之后到达的主题块仍排在全局块之前
        Assert.True(runtime.AddCandidates(new[] { "bg-lime-500" }));
        Check();
        // 之后到达的全局块追加在已有全局块之后
        Assert.True(runtime.AddCandidates(new[] { "ring-2", "shadow-xs" }));
        Check();
        // 夹在既有规则之间的批量插入
        Assert.True(runtime.AddCandidates(new[] { "mt-1", "hover:bg-red-500", "p-8" }));
        Check();

        // 文本仍能被 AwAUI.Css 正常解析
        Assert.DoesNotContain(StyleSheet.Parse(runtime.Css, "runtime").Diagnostics, d => d.Severity == CssDiagnosticSeverity.Error);
    }

    [Fact]
    public void Late_rules_are_inserted_at_their_index_order()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        Assert.True(runtime.AddCandidates(new[] { "hover:bg-red-500", "m-2" }));

        // 与一次性 Build 的规则顺序对照：m-2 的排序下标在 p-4 之前，hover:bg-red-500 在两者之后
        string fresh = TailwindCompiler.CreateDefault(Options()).Build(new[] { "p-4", "hover:bg-red-500", "m-2" });
        string[] selectors = { ".m-2 {", ".p-4 {", ".hover\\:bg-red-500:hover {" };
        var expected = selectors.Select(s => fresh.IndexOf(s, StringComparison.Ordinal)).ToArray();
        var actual = selectors.Select(s => runtime.Css.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, expected);
        Assert.DoesNotContain(-1, actual);
        Assert.Equal(expected.OrderBy(p => p), expected);
        Assert.Equal(actual.OrderBy(p => p), actual);
    }

    [Fact]
    public void Late_theme_variables_are_appended_and_cascade()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        Assert.True(runtime.AddCandidates(new[] { "text-blue-500", "mt-2" }));

        // 晚到的主题变量进入追加的 @layer theme 块
        Assert.Contains("--color-blue-500", runtime.Css);
        Assert.True(runtime.Css.IndexOf("--color-blue-500", StringComparison.Ordinal)
                    > runtime.Css.IndexOf("@layer base", StringComparison.Ordinal));
        Assert.True(Occurrences(runtime.Css, "@layer theme {") >= 2);

        // AwAUI.Css 解析组合后的样式表：层序仍是 theme < base < utilities，晚到变量在 utilities 规则里生效
        var sheet = StyleSheet.Parse(runtime.Css, "runtime");
        Assert.DoesNotContain(sheet.Diagnostics, d => d.Severity == CssDiagnosticSeverity.Error);
        var engine = new StyleEngine();
        engine.AddStyleSheet(sheet);
        var el = new TestElement("div", "p-4 mt-2 text-blue-500");
        var style = engine.Resolve(el, null);
        Assert.Equal(16f, style.PaddingTop.Resolve(0));
        Assert.Equal(8f, style.MarginTop.Value.Resolve(0));

        // 与一次性 Build 的合并输出对照：同一元素的计算颜色相同，说明追加 theme 块与单份样式表等价
        var mergedEngine = new StyleEngine();
        mergedEngine.AddStyleSheet(StyleSheet.Parse(
            TailwindCompiler.CreateDefault(Options()).Build(new[] { "p-4", "mt-2", "text-blue-500" })));
        var merged = mergedEngine.Resolve(new TestElement("div", "p-4 mt-2 text-blue-500"), null);
        Assert.Equal(Rgba(merged.ResolveColor(merged.Color)), Rgba(style.ResolveColor(style.Color)));
        Assert.NotEqual("0,0,0", Rgba(style.ResolveColor(style.Color)));
    }

    [Fact]
    public void Property_and_keyframes_are_emitted_once_and_updates_are_idempotent()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        Assert.True(runtime.AddCandidates(new[] { "shadow-xs", "animate-spin" }));
        Assert.Equal(1, Occurrences(runtime.Css, "@property --tw-ring-shadow "));
        Assert.Equal(1, Occurrences(runtime.Css, "@keyframes spin"));
        string css = runtime.Css;

        Assert.False(runtime.AddCandidates(new[] { "shadow-xs", "animate-spin" }));
        Assert.Equal(css, runtime.Css);
        Assert.Equal(1, Occurrences(runtime.Css, "@property --tw-ring-shadow "));
        Assert.Equal(1, Occurrences(runtime.Css, "@keyframes spin"));

        // 另一个用到相同 @property 的候选不会重复输出
        Assert.True(runtime.AddCandidates(new[] { "ring-2" }));
        Assert.Equal(1, Occurrences(runtime.Css, "@property --tw-ring-shadow "));
        Assert.Equal(1, Occurrences(runtime.Css, "@property --tw-ring-color "));
    }

    [Fact]
    public void Removed_candidates_retract_their_rules_and_can_be_restored_in_place()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        Assert.True(runtime.AddCandidates(new[] { "m-2", "hover:bg-red-500" }));
        string before = runtime.Css;
        Assert.Equal(runtime.Recompose(), before);

        // 类名从树上消失：对应规则撤回，其余规则原位不动
        Assert.True(runtime.RemoveCandidates(new[] { "m-2" }));
        Assert.DoesNotContain(".m-2 {", runtime.Css);
        Assert.Contains(".p-4 {", runtime.Css);
        Assert.Contains(".hover\\:bg-red-500", runtime.Css);
        Assert.Equal(runtime.Recompose(), runtime.Css);

        // 重复移除、移除从未加入或无效的类名都是无操作
        Assert.False(runtime.RemoveCandidates(new[] { "m-2" }));
        Assert.False(runtime.RemoveCandidates(new[] { "not-a-class" }));

        // 重新加入：从缓存恢复，文本与移除前逐字节相同（规则回到原来的下标）
        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        Assert.Equal(before, runtime.Css);
        Assert.Equal(runtime.Recompose(), runtime.Css);
    }

    [Fact]
    public void Removing_every_rule_leaves_shared_theme_blocks_in_place()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        Assert.True(runtime.AddCandidates(new[] { "text-blue-500" }));
        Assert.Contains(".text-blue-500 {", runtime.Css);

        Assert.True(runtime.RemoveCandidates(new[] { "text-blue-500" }));
        Assert.DoesNotContain(".text-blue-500 {", runtime.Css);
        // 主题变量是共享的一次性输出，不随类名撤回；空的 utilities 层保留，规则槽位不重排
        Assert.Equal(1, Occurrences(runtime.Css, "--color-blue-500:"));
        Assert.Equal(runtime.Recompose(), runtime.Css);

        // 再加回来：规则回到原槽位，主题变量不重复
        Assert.True(runtime.AddCandidates(new[] { "text-blue-500" }));
        Assert.Contains(".text-blue-500 {", runtime.Css);
        Assert.Equal(1, Occurrences(runtime.Css, "--color-blue-500:"));
        Assert.Equal(runtime.Recompose(), runtime.Css);
    }

    [Fact]
    public void Removal_interleaved_with_new_candidates_still_matches_a_full_recomposition()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        void Check()
        {
            Assert.Equal(runtime.Recompose(), runtime.Css);
            Assert.DoesNotContain(StyleSheet.Parse(runtime.Css, "runtime").Diagnostics,
                d => d.Severity == CssDiagnosticSeverity.Error);
        }

        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        Check();
        Assert.True(runtime.RemoveCandidates(new[] { "m-2" }));
        Check();
        // 移除后到达的规则插入到空洞之间的正确位置
        Assert.True(runtime.AddCandidates(new[] { "mt-1", "text-blue-500" }));
        Check();
        // 恢复的规则回到空洞（m-2 仍在 p-4 之前）
        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        Check();
        Assert.True(runtime.Css.IndexOf(".m-2 {", StringComparison.Ordinal)
                    < runtime.Css.IndexOf(".p-4 {", StringComparison.Ordinal));
        // 基线候选也可以撤回，之后的批量插入 / 移除继续成立
        Assert.True(runtime.RemoveCandidates(new[] { "p-4", "mt-1" }));
        Check();
        Assert.True(runtime.AddCandidates(new[] { "hover:bg-red-500" }));
        Check();
        Assert.True(runtime.AddCandidates(new[] { "p-4" }));
        Check();
    }

    [Fact]
    public void Changed_fires_when_rules_are_removed()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        int changed = 0;
        runtime.Changed += _ => changed++;
        Assert.True(runtime.AddCandidates(new[] { "flex", "grid" }));
        Assert.True(runtime.RemoveCandidates(new[] { "grid" }));
        Assert.Equal(2, changed);
        Assert.False(runtime.RemoveCandidates(new[] { "grid" }));
        Assert.Equal(2, changed);
    }

    /// <summary>增量解析从运行时报告的偏移开始，结果必须与整段解析完全一致（规则区间也相同）。</summary>
    static void AssertIncrementalMatchesFull(string before, string after, int from)
    {
        var previous = StyleSheet.Parse(before);
        var incremental = StyleSheet.ParseIncremental(after, null, previous, from);
        var full = StyleSheet.Parse(after);
        Assert.Equal(full.Rules.Count, incremental.Rules.Count);
        for (int i = 0; i < full.Rules.Count; i++)
            Assert.Equal(full.Rules[i].Range, incremental.Rules[i].Range);
        Assert.Equal(full.Diagnostics.Count, incremental.Diagnostics.Count);
    }

    [Fact]
    public void ChangedFrom_reports_the_utilities_block_start()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        var offsets = new List<int>();
        runtime.ChangedFrom += (_, at) => offsets.Add(at);

        string before = runtime.Css;
        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        string after = runtime.Css;

        // 规则都在 @layer utilities 块里：报告块开头，之前的前缀逐字符相同
        int at = Assert.Single(offsets);
        Assert.Equal(after.IndexOf("@layer utilities {", StringComparison.Ordinal), at);
        Assert.Equal(before.Substring(0, at), after.Substring(0, at));
        AssertIncrementalMatchesFull(before, after, at);

        offsets.Clear();
        string beforeRemove = runtime.Css;
        Assert.True(runtime.RemoveCandidates(new[] { "m-2" }));
        int removeAt = Assert.Single(offsets);
        Assert.Equal(beforeRemove.Substring(0, removeAt), runtime.Css.Substring(0, removeAt));
        AssertIncrementalMatchesFull(beforeRemove, runtime.Css, removeAt);
    }

    [Fact]
    public void ChangedFrom_points_at_an_appended_theme_block()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        string before = runtime.Css;
        int at = -1;
        runtime.ChangedFrom += (_, offset) => at = offset;

        Assert.True(runtime.AddCandidates(new[] { "--color-blue-500" }));
        string after = runtime.Css;
        Assert.Contains("--color-blue-500", after);
        Assert.Equal(before.Substring(0, at), after.Substring(0, at));
        Assert.StartsWith("@layer theme {", after.Substring(at));
        AssertIncrementalMatchesFull(before, after, at);
    }

    [Fact]
    public void BindIncremental_applies_the_current_stylesheet_and_the_first_changed_boundary()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        var applied = new List<(string Css, int From)>();
        string initial = runtime.Css;

        var binding = runtime.BindIncremental((css, from) => applied.Add((css, from)));
        Assert.Equal(new[] { (initial, 0) }, applied);

        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        Assert.Equal(2, applied.Count);
        Assert.Equal(runtime.Css, applied[1].Css);
        Assert.Equal(runtime.Css.IndexOf("@layer utilities {", StringComparison.Ordinal), applied[1].From);
        Assert.Equal(applied[1].From, runtime.LastChangeOffset);

        binding.Dispose();
        Assert.True(runtime.AddCandidates(new[] { "grid" }));
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public void Invalid_candidates_are_reported_once_and_never_throw()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        Assert.True(runtime.AddCandidates(new[] { "p-4", "not-a-class", "bg-not-a-color", "p-4" }));
        Assert.Contains("p-4", runtime.Css);
        Assert.Equal(new[] { "not-a-class", "bg-not-a-color" }, runtime.InvalidCandidates);

        // 再次提交无效类名：不重复记录，也不改变样式表
        string css = runtime.Css;
        Assert.False(runtime.AddCandidates(new[] { "not-a-class", "bg-not-a-color" }));
        Assert.Equal(css, runtime.Css);
        Assert.Equal(2, runtime.InvalidCandidates.Count);

        // 重复提交有效类名同样不变化
        Assert.False(runtime.AddCandidates(new[] { "p-4" }));
    }

    [Fact]
    public void TryAddCandidates_ignores_non_tailwind_classes_without_reporting_them()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        var reported = new List<string>();
        runtime.InvalidCandidate += (_, candidate) => reported.Add(candidate);

        // 整树扫描会混入应用自定义类：仍然生成 p-4，但不把 my-widget 当作错误
        Assert.True(runtime.TryAddCandidates(new[] { "my-widget", "p-4" }));
        Assert.Contains(".p-4 {", runtime.Css);
        Assert.Empty(runtime.InvalidCandidates);
        Assert.Empty(reported);

        // 显式提交时仍按普通规则报告
        Assert.False(runtime.AddCandidates(new[] { "my-widget" }));
        Assert.Equal(new[] { "my-widget" }, runtime.InvalidCandidates);
        Assert.Equal(new[] { "my-widget" }, reported);
    }

    [Fact]
    public void Invalid_candidate_event_fires_once_per_class()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        var reported = new List<string>();
        runtime.InvalidCandidate += (_, candidate) => reported.Add(candidate);

        // 同一批里的有效类名照常生效；无效类名按首次出现顺序触发事件
        Assert.True(runtime.AddCandidates(new[] { "not-a-class", "p-4", "bg-not-a-color" }));
        Assert.Contains("p-4", runtime.Css);
        Assert.Equal(new[] { "not-a-class", "bg-not-a-color" }, reported);

        Assert.False(runtime.AddCandidates(new[] { "not-a-class", "bg-not-a-color" }));
        Assert.Equal(2, reported.Count);
    }

    [Fact]
    public void Bind_applies_the_current_stylesheet_and_later_changes()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        var applied = new List<string>();
        string initial = runtime.Css;

        var binding = runtime.Bind(applied.Add);
        Assert.Equal(new[] { initial }, applied);

        Assert.True(runtime.AddCandidates(new[] { "m-2" }));
        Assert.Equal(2, applied.Count);
        Assert.Equal(runtime.Css, applied[1]);
        Assert.Contains("m-2", applied[1]);

        binding.Dispose();
        Assert.True(runtime.AddCandidates(new[] { "grid" }));
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public void Disabled_runtime_is_a_no_op()
    {
        using var _ = RuntimeDisabled();
        var runtime = new TailwindRuntime(Entry, new[] { "p-4" }, Options());
        string css = runtime.Css;
        Assert.False(runtime.AddCandidates(new[] { "m-2", "not-a-class" }));
        Assert.Equal(css, runtime.Css);
        Assert.Empty(runtime.InvalidCandidates);
    }

    [Fact]
    public void Changed_fires_only_when_the_stylesheet_changes()
    {
        using var _ = RuntimeEnabled();
        var runtime = new TailwindRuntime(Entry, null, Options());
        int changed = 0;
        runtime.Changed += _ => changed++;
        Assert.True(runtime.AddCandidates(new[] { "flex" }));
        Assert.Equal(1, changed);
        Assert.False(runtime.AddCandidates(new[] { "flex" }));
        Assert.Equal(1, changed);
        Assert.True(runtime.AddCandidates(new[] { "grid" }));
        Assert.Equal(2, changed);
    }

    static string Rgba(Color color) =>
        $"{(int)Math.Round(color.R * 255)},{(int)Math.Round(color.G * 255)},{(int)Math.Round(color.B * 255)}";

    /// <summary>最小的内存元素，用于让 AwAUI.Css 的级联计算结果。</summary>
    sealed class TestElement : StyleElement
    {
        readonly List<string> _classes = new();

        public TestElement(string tag, string? classes)
        {
            Tag = tag;
            if (!string.IsNullOrEmpty(classes)) _classes.AddRange(classes!.Split(' '));
        }

        public override string Tag { get; }
        public override string? Id => null;
        public override IReadOnlyList<string> Classes => _classes;
        public override string? GetAttribute(string name) => null;
        public override StyleElement? Parent => null;
        public override StyleElement? PreviousSibling => null;
        public override StyleElement? NextSibling => null;
        public override PseudoStates States => PseudoStates.None;
        public override bool IsEmpty => true;
    }
}
