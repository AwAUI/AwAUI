using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.CodeGen;
using AwAUI.Markup.Compiler.Formatting;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Tests.Shared;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 编译器前端运行在源生成器与 IDE 中，每次编辑都会执行：病态输入也必须是线性时间，嵌套再深也不能栈溢出。
/// 模糊测试的输入很短，发现不了二次方复杂度，这里用成规模的重复模式专门检查。
/// </summary>
public class ScalingTests
{
    static string R(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    /// <summary>病态模式：每个都曾经（或很容易）因为反复扫描到文件末尾而退化为二次方。</summary>
    static readonly Dictionary<string, Func<int, string>> Patterns = new()
    {
        ["code-parens"] = n => "@{ " + R("(", n) + " }",
        ["code-mixed"] = n => "@{ " + R("([{", n),
        ["code-if-lines"] = n => "@{ " + R("if(\n", n) + " }",
        ["implicit-paren-lines"] = n => R("@a(\n", n),
        ["explicit-lines"] = n => R("@(\n", n),
        ["switch-case-paren"] = n => "@switch (x) { " + R("case (\n", n) + " }",
        ["switch-case-brace"] = n => "@switch (x) { " + R("case {\n", n) + " }",
        ["switch-case-no-colon"] = n => "@switch (x) { " + R("case 1\n", n) + " }",
        ["switch-case-no-colon-oneline"] = n => "@switch (x) { " + R("case 1 ", n) + " }",
        ["directive-unknown"] = n => R("@functions {\n", n),
        ["hash-in-code-line"] = n => "@{ var x = 1;" + R(" #", n) + " }",
        ["if-no-paren-oneline"] = n => R("@if x ", n),
        ["if-paren-oneline"] = n => R("@if (", n),
        ["foreach-no-paren-oneline"] = n => R("@foreach x ", n),
        ["attr-quote-phases"] = n => R("<i a='@(' b=\"@(\" \n/>\n", n),
        ["attr-explicit-unclosed"] = n => R("<i a=\"@(\" />\n", n),
        ["type-args"] = n => R("<Foo<", n),
        ["end-tag-type-args"] = n => R("</A<", n),
        ["interpolated-holes"] = n => "@{ var s = " + R("$\"{", n) + " }",
        ["many-attributes"] = n => "<div" + string.Concat(Enumerable.Range(0, n).Select(i => " a" + i)) + " />",
        ["many-bind-props"] = n => "<Foo" + string.Concat(Enumerable.Range(0, n).Select(i => " @bind-A" + i + "=\"x\"")) + " />",
        ["stray-end-tags"] = n => R("<div>", 150) + R("</span>", n),
        ["deep-braces"] = n => "@code {\n" + R("{\n", n) + R("}\n", n) + "}",
        ["line-text-chain"] = n => R("@:", n),
        ["style-selectors-oneline"] = n => "<style>" + R(".a > .b:is(.c, .d)::before, ", n) + ".e { color: red }</style>",
        ["style-nested-rules"] = n => "<style>.a { " + R("& .b { color: red; ", n) + R("}", n) + " }</style>",
        ["style-unclosed-blocks"] = n => "<style>" + R("@media x { .a { ", n) + "</style>",
        ["code-members"] = n => "@code {" + R(" public int P { get; set; } = 1; State<int> s = new(0); void M() { }", n) + " }",
        ["code-members-unbalanced"] = n => "@code {" + R(" public int P { get; (", n) + " }",
        // 成员头部里反复出现、前面没有标识符的 (：每个 ( 都曾从成员开头重新扫描（二次方）；单次扫描很快，放大 8 倍才能越过计时下限
        ["code-member-header-parens"] = n => "@code { " + R("()", 8 * n) + " }",
        ["code-member-header-tuples"] = n => "@code { public static " + R("(,)", 8 * n) + " }",
        ["valid-markup"] = n => R("<div class=\"row @cls\" @onclick=\"() => Go(1)\">@if (a < b) { <span>@x.Name</span> } else { <text>@y</text> }</div>\n", n),
    };

    public static TheoryData<string> PatternNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Patterns.Keys) data.Add(name);
        return data;
    }

    /// <summary>完整走一遍编译器前端，返回本线程消耗的 CPU 毫秒数（见 <see cref="ThreadCpuClock"/>）。</summary>
    static double MeasureMs(string text)
    {
        double start = ThreadCpuClock.NowMs();
        var a = MarkupCompiler.Analyze(text, "P.awaui");
        MarkupFormatter.Format(a.Tree);
        MarkupFormatter.Format(a.Tree.Root); // 错误恢复得到的树也要格式化一遍
        MarkupCompiler.Compile(a);
        return ThreadCpuClock.NowMs() - start;
    }

    static void OnBigThread(Action action, int stackSize = 16 * 1024 * 1024)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        }, stackSize);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "did not finish within 120 s");
        if (error != null) throw new Exception("failure", error);
    }

    /// <summary>基准规模 n 与放大倍数：线性实现耗时约 ×8（实测 7–13），二次方约 ×64。</summary>
    const int BaseSize = 5000, Scale = 8;

    /// <summary>
    /// 耗时比值的判定上限：取在 8 与 64 之间（几何中点约 23）。只做了一轮时用更严的上限，
    /// 避免二次方实现恰好因为小输入那一次被抢占而蒙混过关。
    /// </summary>
    const double RatioLimit = 24, FirstRoundRatioLimit = 16;

    /// <summary>计时下限：小于它的耗时按它计，吸收计时器分辨率（Windows 的线程时间约 15.6 ms 一跳）与零星停顿。</summary>
    const double FloorMs = 5;

    /// <summary>最多交错测量的轮数。正常机器上一两轮即通过，满载机器上靠更多轮次取到未被抢占的样本。</summary>
    const int MaxRounds = 7;

    [Theory]
    [MemberData(nameof(PatternNames))]
    public void Pathological_inputs_take_linear_time(string name) => OnBigThread(() =>
    {
        // 并行跑整个解决方案时机器满载（负载 16–60），墙钟耗时会被抢占任意拉长，而且越长的测量被拉长得越多：
        // 几毫秒的 n 常能落在一个完整的调度时间片里，几百毫秒的 8n 却必然被抢占多次 —— 即使各取多轮最小值，
        // 实测线性实现的墙钟比值也会从 8–13 涨到 25–50。因此：
        //   1. 分配字节数之比（确定性，与负载无关）：在第一轮测量；
        //   2. 线程 CPU 时间之比（不计等待 CPU 的时间）：n 与 8n 交错测量多轮，各取最小值，
        //      吸收缓存争用、GC、降频等剩余干扰；交错使两者经历相近的机器状态。
        // 二次方实现的比值约 64，远离判定上限：要蒙混过关，n 的每一轮都得被拖慢近 3 倍而 8n 不受影响。
        // 线性实现在满载下也只需若干轮里 n 与 8n 各有一次未被严重抢占即可通过。
        // 局限：二次方部分在 n 时只占总耗时很小一部分（例如向量化 IndexOf 逐位置扫到末尾）时比值可能落在上限以下
        var make = Patterns[name];
        string small = make(BaseSize), large = make(Scale * BaseSize);
        MeasureMs(make(200)); // 预热 JIT
        MeasureMs(small);     // 让分层编译把热路径升到优化代码，避免第一轮的 n 偏慢

        double minSmall = double.MaxValue, minLarge = double.MaxValue;
        var samples = new List<string>();
        for (int round = 1; round <= MaxRounds; round++)
        {
            GC.Collect(); // 让上一次测量留下的垃圾不在本次测量里触发回收
#if NET
            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
            double tSmall = MeasureMs(small);
#if NET
            long smallBytes = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            GC.Collect();
#if NET
            before = GC.GetAllocatedBytesForCurrentThread();
#endif
            double tLarge = MeasureMs(large);
#if NET
            long largeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round == 1)
            {
                // 线性实现实测 2.4–9.2；按块增长的缓冲区最多带来常数倍，二次方的逐位置子串 / 复制约为 64
                double allocRatio = (double)largeBytes / Math.Max(smallBytes, 64 * 1024);
                Assert.True(allocRatio < 2 * Scale,
                    $"{name}: n={BaseSize} allocated {smallBytes / 1024} KB, {Scale}n allocated {largeBytes / 1024} KB (ratio {allocRatio:F1}, limit {2 * Scale})");
            }
#endif
            minSmall = Math.Min(minSmall, tSmall);
            minLarge = Math.Min(minLarge, tLarge);
            samples.Add($"{tSmall:F1}/{tLarge:F1}");
            double ratio = minLarge / Math.Max(minSmall, FloorMs);
            if (ratio < (round == 1 ? FirstRoundRatioLimit : RatioLimit)) return;
        }
        string clock = ThreadCpuClock.IsThreadCpuTime ? "thread CPU" : "wall clock";
        Assert.Fail($"{name}: min {clock} time over {MaxRounds} interleaved rounds: n={BaseSize} took {minSmall:F1} ms, {Scale}n took {minLarge:F1} ms " +
                    $"(ratio {minLarge / Math.Max(minSmall, FloorMs):F1}, limit {RatioLimit}; floor {FloorMs} ms). Rounds (n/{Scale}n ms): {string.Join(", ", samples)}");
    });

    [Fact]
    public void Thread_cpu_clock_counts_running_time_only()
    {
        // 复杂度测试依赖它不计等待时间；悄悄退回墙钟会让测试在满载机器上重新变得不稳定
        Assert.True(ThreadCpuClock.IsThreadCpuTime, "no thread CPU clock on this platform: ScalingTests fall back to the wall clock");
        double start = ThreadCpuClock.NowMs();
        Thread.Sleep(100);
        Assert.True(ThreadCpuClock.NowMs() - start < 50, "sleeping consumed CPU time");
        // 忙等直到线程 CPU 时间前进 40 ms：时钟确实在走（满载时墙钟可能长得多，给足上限）
        var wall = System.Diagnostics.Stopwatch.StartNew();
        start = ThreadCpuClock.NowMs();
        long spins = 0;
        while (ThreadCpuClock.NowMs() - start < 40)
        {
            spins++;
            Assert.True(wall.Elapsed < TimeSpan.FromSeconds(30), "thread CPU clock does not advance");
        }
        Assert.True(spins > 0);
    }

    [Theory]
    [InlineData("<div>", "</div>")]
    [InlineData("<div>@if (a) {", "}</div>")]
    [InlineData("@if (a) {", "}")]
    [InlineData("@if (a) {} else if (b) {", "}")]
    [InlineData("@switch (a) { case 1: <div>", "</div> }")]
    [InlineData("@{ @:<div>", "")]
    [InlineData("<>@{", "}</>")]
    [InlineData("@foreach (var x in y) { <div @key=\"x\">", "</div> }")]
    [InlineData("@:", "")]
    public void Deep_nesting_is_capped_so_every_tree_walker_fits_in_a_small_stack(string open, string close)
    {
        // 512 KB 的线程栈（源生成器所在的线程池线程通常有 1 MB 以上）。超限会栈溢出使测试进程崩溃 —— 同样是失败
        string text = R(open, 2000) + R(close, 2000);
        OnBigThread(() =>
        {
            var a = MarkupCompiler.Analyze(text, "P.awaui");
            Assert.Contains(a.Diagnostics, d => d.Message.Contains("嵌套"));
            TestUtil.CheckSpans(a.Tree);
            MarkupFormatter.Format(a.Tree);
            MarkupFormatter.Format(a.Tree.Root);
            MarkupCompiler.Compile(a);
            SyntaxPrinter.Print(a.NormalizedRoot, includeSpans: true);
            SyntaxPrinter.Print(a.Tree.Root);
            _ = a.Tree.Root.DescendantNodes().Count();
        }, 512 * 1024);
    }
}
