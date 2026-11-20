using AwAUI.Headless;
using AwAUI.Platform;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 随机的滚动 / 变更序列：增量绘制 + 局部光栅化的结果与关闭增量、整帧光栅化的参照逐字节相同，命中测试也相同。
/// 场景含嵌套滚动容器、包含块在滚动容器内外的绝对定位、fixed、overflow:hidden 与视口滚动。种子数可用 AWAUI_SCROLL_DIFF_SEEDS 加大。
/// </summary>
public partial class ScrollDifferentialTests
{
    const string DiffCss = """
        :root { font-size: 12px; background-color: #f1f5f9; }
        .outer { position: relative; margin: 8px; height: 170px; overflow: auto; border: 3px solid #64748b; background-color: #fff; }
        .inner { height: 70px; overflow: auto; margin: 6px; border: 1px solid #16a34a; background-color: #f0fdf4; }
        .clip { height: 40px; overflow: hidden; background-color: #fae8ff; }
        .row { height: 22px; border-bottom: 1px solid #cbd5e1; background-color: #e0e7ff; }
        .row.big { height: 37px; background-color: #fee2e2; }
        .row.wide { width: 420px; }
        .abs { position: absolute; right: 4px; top: 30px; width: 18px; height: 18px; background-color: #f97316; }
        .rel { position: relative; height: 30px; background-color: #ecfccb; }
        .relabs { position: absolute; left: 40px; top: 6px; width: 30px; height: 10px; background-color: #0ea5e9; }
        .fix { position: fixed; left: 2px; bottom: 2px; width: 14px; height: 14px; background-color: #dc2626; }
        .tall { height: 140px; background-color: #fef9c3; }
        .row.xf { transform: rotate(-4deg) translateX(9px) scale(0.9); background-color: #c7d2fe; }
        .row.flt { filter: drop-shadow(2px 3px 1px #1e293b) hue-rotate(90deg); }
        .row.glass { position: relative; z-index: 1; margin-top: -10px; backdrop-filter: blur(2px) grayscale(1); background-color: rgba(255, 255, 255, 0.3); }
        .row.cp { clip-path: inset(3px 10px round 6px); background-color: #fda4af; }
        """;

    sealed partial class Scene : Component
    {
        public State<int> Version { get; } = new(0);
        public List<(int Id, string Class)> Rows { get; } = new();
        public List<(int Id, string Class)> InnerRows { get; } = new();
        public bool Tall { get; set; }

        protected override Node Build()
        {
            _ = Version.Value;
            var outer = new NodeList();
            var inner = new NodeList();
            foreach (var (id, cls) in InnerRows) inner.Add(new Div { Key = id, Id = "i" + id, Class = cls, Children = ["i" + id] });
            outer.Add(new Div { Key = "inner", Id = "inner", Class = "inner", Children = inner });
            outer.Add(new Div { Key = "abs", Id = "abs", Class = "abs" });
            outer.Add(new Div { Key = "rel", Class = "rel", Children = [new Div { Id = "relabs", Class = "relabs" }] });
            outer.Add(new Div { Key = "clip", Id = "clip", Class = "clip", Children = [new Div { Class = "row" }, new Div { Class = "row big" }, new Div { Class = "row" }] });
            outer.Add(new Div { Key = "fix", Id = "fix", Class = "fix" });
            foreach (var (id, cls) in Rows) outer.Add(new Div { Key = id, Id = "r" + id, Class = cls, Children = ["r" + id] });
            var children = new NodeList { new Div { Key = "outer", Id = "outer", Class = "outer", Children = outer } };
            if (Tall) children.Add(new Div { Key = "tall", Class = "tall" });
            return new Div { Children = children };
        }
    }

    public static IEnumerable<object[]> Seeds()
    {
        int n = int.TryParse(Environment.GetEnvironmentVariable("AWAUI_SCROLL_DIFF_SEEDS"), out var v) && v > 0 ? v : 12;
        for (int i = 0; i < n; i++) yield return new object[] { i, i % 3 == 0 ? 1.0 : i % 3 == 1 ? 1.5 : 2.0 };
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_scrolling_matches_a_full_non_incremental_render(int seed, double scale) => Run(seed, scale, effects: false);

    /// <summary>第九批：行可以带变换、滤镜、背景滤镜与 clip-path（在滚动容器内、随滚动平移）。</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_scrolling_with_effects_matches_a_full_non_incremental_render(int seed, double scale) => Run(seed + 1000, scale, effects: true);

    static void Run(int seed, double scale, bool effects)
    {
        var rnd = new Random(seed);
        Scene Make()
        {
            var s = new Scene();
            for (int i = 0; i < 25; i++) s.Rows.Add((i, "row"));
            for (int i = 0; i < 8; i++) s.InnerRows.Add((i, "row"));
            return s;
        }
        var sa = Make();
        var sb = Make();
        var a = Create(sa, 260, 220, scale, DiffCss, smooth: rnd.Next(2) == 0);
        var b = Create(sb, 260, 220, scale, DiffCss, smooth: a.Host.Pipeline.SmoothScrolling);
        b.Host.Pipeline.IncrementalPaint = false;
        b.Host.PartialRaster = false;
        if (rnd.Next(3) == 0)
        {
            a.Host.Pipeline.ScrollbarStyle = ScrollbarStyle.Classic;
            b.Host.Pipeline.ScrollbarStyle = ScrollbarStyle.Classic;
        }
        int nextId = 100;
        var log = new List<string>();
        for (int step = 0; step < 60; step++)
        {
            int op = rnd.Next(12);
            var p = new Point(rnd.Next(0, 260), rnd.Next(0, 220));
            switch (op)
            {
                case 0:
                case 1:
                case 2:
                {
                    float notches = (float)Math.Round(rnd.NextDouble() * 4 - 1.5, 2);
                    bool shift = rnd.Next(4) == 0;
                    log.Add($"wheel {p} {notches} shift={shift}");
                    foreach (var w in new[] { a.Window, b.Window }) w.WheelNotches(p, notches, modifiers: shift ? KeyModifiers.Shift : KeyModifiers.None);
                    break;
                }
                case 3:
                {
                    var phase = rnd.Next(3) switch { 0 => ScrollPhase.Began, 1 => ScrollPhase.Changed, _ => ScrollPhase.Ended };
                    float d = (float)Math.Round(rnd.NextDouble() * 60 - 20, 1);
                    log.Add($"trackpad {p} {d} {phase}");
                    foreach (var w in new[] { a.Window, b.Window }) w.TrackpadScroll(p, d, 0, phase);
                    break;
                }
                case 4:
                {
                    string id = rnd.Next(3) switch { 0 => "outer", 1 => "inner", _ => "clip" };
                    var to = new Point(rnd.Next(0, 200), (float)Math.Round(rnd.NextDouble() * 700 - 50, 1));
                    log.Add($"jump {id} {to}");
                    a.Host.Root.RootElement.ById(id).ScrollController.JumpTo(to);
                    b.Host.Root.RootElement.ById(id).ScrollController.JumpTo(to);
                    break;
                }
                case 5:
                {
                    // 行变化：高度 / 宽度 / 背景
                    var list = rnd.Next(3) == 0 ? (sa.InnerRows, sb.InnerRows) : (sa.Rows, sb.Rows);
                    if (list.Item1.Count == 0) break;
                    int k = rnd.Next(list.Item1.Count);
                    string cls = rnd.Next(effects ? 7 : 3) switch
                    {
                        0 => "row", 1 => "row big", 2 => "row wide", 3 => "row xf", 4 => "row flt", 5 => "row glass", _ => "row cp",
                    };
                    log.Add($"class {list.Item1[k].Id} = {cls}");
                    list.Item1[k] = (list.Item1[k].Id, cls);
                    list.Item2[k] = (list.Item2[k].Id, cls);
                    sa.Version.Value++;
                    sb.Version.Value++;
                    break;
                }
                case 6:
                {
                    bool add = rnd.Next(2) == 0 || sa.Rows.Count < 3;
                    if (add)
                    {
                        int at = rnd.Next(sa.Rows.Count + 1);
                        sa.Rows.Insert(at, (nextId, "row"));
                        sb.Rows.Insert(at, (nextId, "row"));
                        log.Add($"add {nextId} at {at}");
                        nextId++;
                    }
                    else
                    {
                        int count = Math.Min(sa.Rows.Count - 1, rnd.Next(1, 12));
                        sa.Rows.RemoveRange(0, count);
                        sb.Rows.RemoveRange(0, count);
                        log.Add($"remove {count}");
                    }
                    sa.Version.Value++;
                    sb.Version.Value++;
                    break;
                }
                case 7:
                {
                    log.Add($"hover {p}");
                    foreach (var w in new[] { a.Window, b.Window }) Pointer(w, RawPointerEventType.Move, p);
                    break;
                }
                case 8:
                {
                    sa.Tall = sb.Tall = !sa.Tall;
                    log.Add($"tall {sa.Tall}");
                    sa.Version.Value++;
                    sb.Version.Value++;
                    break;
                }
                case 9:
                {
                    var key = rnd.Next(4) switch { 0 => PhysicalKey.ArrowDown, 1 => PhysicalKey.PageDown, 2 => PhysicalKey.Home, _ => PhysicalKey.ArrowRight };
                    log.Add($"key {key}");
                    a.Window.TypeKey(key);
                    b.Window.TypeKey(key);
                    break;
                }
                default:
                {
                    double ms = rnd.Next(1, 400);
                    log.Add($"advance {ms}");
                    a.Clock.Advance(TimeSpan.FromMilliseconds(ms));
                    b.Clock.Advance(TimeSpan.FromMilliseconds(ms));
                    break;
                }
            }
            Step(a.Host, a.Clock);
            Step(b.Host, b.Clock);
            var pa = Pixels(a.Window);
            var pb = Pixels(b.Window);
            if (!pa.AsSpan().SequenceEqual(pb))
            {
                bool rasterOk = FullRaster(a.Host).AsSpan().SequenceEqual(pb);
                Assert.Fail($"seed {seed} scale {scale} step {step}: pixels differ ({(rasterOk ? "damage too small" : "stale cache")}), damage {a.Host.LastDamage}\n" + string.Join("\n", log));
            }
            for (int y = 3; y < 220; y += 13)
            for (int x = 3; x < 260; x += 11)
            {
                var q = new Point(x, y);
                string? ha = a.Host.Pipeline.HitTest(q)?.ToString(), hb = b.Host.Pipeline.HitTest(q)?.ToString();
                if (ha != hb) Assert.Fail($"seed {seed} step {step}: hit test at {q}: {ha} vs {hb}\n" + string.Join("\n", log));
            }
        }
    }
}
