using AwAUI.Graphics;
using AwAUI.Render;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 运行中的 CSS 动画与过渡下的增量绘制差分测试：同一棵随机树、同一串随机修改（切换带过渡 / 动画的 class）与同样的时钟推进，
/// 增量录制 + 按损伤区域局部光栅化的累积结果必须与关闭增量绘制、整帧重绘的结果逐字节相同（只推进时间的帧走动画推进路径，
/// 不重新级联，损伤必须仍然覆盖变化）。
/// </summary>
public class AnimationDifferentialTests
{
    const string Css = """
        .n { min-height: 6px; }
        .a { background-color: #ff0000; transition: background-color 0.4s linear, opacity 0.3s ease-out; }
        .b { opacity: 0.3; }
        .c { transform: translateX(10px) rotate(10deg); transition: transform 0.5s ease; }
        .d { width: 30px; transition: width 0.25s linear, height 0.25s steps(3); }
        .e { width: 60px; height: 20px; }
        .f { animation: pulse 0.6s linear infinite alternate; background-color: #0044ff; }
        .g { animation: slide 0.5s ease-in-out 0.1s both; background-color: #00aa00; }
        .h { display: none; }
        .i { color: #0000ff; transition: color 0.3s linear; }
        .j { border: 2px solid #000000; transition: border-color 0.2s linear; }
        .k { border-color: #ff00ff; }
        .l { visibility: hidden; transition: visibility 0.3s; }
        .m { animation: spin 0.8s linear infinite; width: 20px; height: 20px; background-color: #884400; }
        @keyframes pulse { from { opacity: 1 } to { opacity: 0.2 } }
        @keyframes slide { from { margin-left: 0px } to { margin-left: 20px } }
        @keyframes spin { to { rotate: 360deg } }
        """;

    static readonly string[] Classes = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "a", "b", "e"];

    sealed class Spec
    {
        public int Id;
        public HashSet<string> Classes = new();
        public string Text = "";
        public List<Spec> Children = new();
    }

    sealed class Model
    {
        public readonly Spec Root = new() { Id = 0 };
        public readonly Random Rng;
        int _next = 1;

        public Model(int seed)
        {
            Rng = new Random(seed);
            _pinned = new Spec { Id = _next++, Classes = { "f" }, Text = "ab" };
            Root.Children.Add(_pinned);
            for (int i = 0; i < 12; i++)
            {
                var all = All();
                var parent = all[Rng.Next(all.Count)];
                var s = new Spec { Id = _next++, Text = Rng.Next(3) == 0 ? "ab" : "" };
                if (Rng.Next(2) == 0) s.Classes.Add(Classes[Rng.Next(Classes.Length)]);
                parent.Children.Add(s);
            }
        }

        public List<Spec> All()
        {
            var list = new List<Spec>();
            void Walk(Spec s)
            {
                list.Add(s);
                foreach (var c in s.Children) Walk(c);
            }
            Walk(Root);
            return list;
        }

        readonly Spec _pinned;

        public string Toggle()
        {
            var all = All();
            all.Remove(_pinned);
            var t = all[Rng.Next(all.Count)];
            var c = Classes[Rng.Next(Classes.Length)];
            if (!t.Classes.Remove(c)) t.Classes.Add(c);
            return "toggle ." + c + " on n" + t.Id;
        }

        public Node Build(Spec s)
        {
            var children = new NodeList();
            foreach (var c in s.Children) children.Add(Build(c));
            if (s.Text.Length > 0) children.Add(new Text(s.Text) { Key = -s.Id - 1 });
            return new Div { Key = s.Id, Id = "n" + s.Id, Class = "n " + string.Join(" ", s.Classes.OrderBy(c => c, StringComparer.Ordinal)), Children = children };
        }
    }

    public static IEnumerable<object[]> Seeds()
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("AWAUI_UI_DIFF_SEEDS"), out int n) && n > 0 ? n : 12;
        for (int seed = 1; seed <= count; seed++) yield return [seed, seed % 2 == 0 ? 1.5f : 1f];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Running_animations_match_full_repaint(int seed, float scale)
    {
        const float W = 200, H = 160;
        var model = new Model(seed + 9000);
        var version = new State<int>(0);
        var refVersion = new State<int>(0);
        var clock = new ManualUIClock();
        var refClock = new ManualUIClock();
        Node Build(State<int> revision)
        {
            _ = revision.Value;
            // 随机树可能被隐藏；在树外保留一个零面积的无限动画，既能保证时间采样，也不会扩大损伤。
            return new Fragment
            {
                Children =
                [
                    model.Build(model.Root),
                    new Div { Key = "clock", Class = "f", Style = "position: absolute; width: 0; height: 0" },
                ],
            };
        }

        var h = new PipelineHarness(new Lambda { Render = () => Build(version) }, Css, W, H, scale, deferMount: true);
        var reference = new PipelineHarness(new Lambda { Render = () => Build(refVersion) }, Css, W, H, scale, deferMount: true);
        h.Root.Clock = clock;
        reference.Root.Clock = refClock;
        reference.Pipeline.IncrementalPaint = false;
        h.MountNow();
        reference.MountNow();
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(h.Pipeline.Viewport, h.Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, scale);
        long rendered = -1;
        var log = new List<string>();
        int animatedFrames = 0;

        void RunBoth()
        {
            h.Root.RunFrame();
            reference.Root.RunFrame();
            var p = h.Pipeline;
            PixelRect? damage = null;
            if (rendered >= 0 && p.FrameVersion == rendered + 1 && p.LastDamage is { } d)
            {
                int x0 = Math.Max(0, (int)Math.Floor(d.Left * scale) - 1), y0 = Math.Max(0, (int)Math.Floor(d.Top * scale) - 1);
                int x1 = (int)Math.Ceiling(d.Right * scale) + 1, y1 = (int)Math.Ceiling(d.Bottom * scale) + 1;
                damage = d.Width <= 0 ? new PixelRect(0, 0, 0, 0) : new PixelRect(x0, y0, x1 - x0, y1 - y0);
            }
            if (p.FrameVersion != rendered && damage is not { IsEmpty: true }) target.Render(p.Frame, new RenderOptions(Color.White, damage));
            rendered = p.FrameVersion;
            if (h.Pipeline.HasRunningAnimations) animatedFrames++;
        }

        void Check(string what)
        {
            using var expected = reference.Render();
            var a = target.Bitmap.ToRgbaUnpremultiplied();
            var b = expected.ToRgbaUnpremultiplied();
            if (a.AsSpan().SequenceEqual(b)) return;
            int diff = 0, maxDelta = 0;
            var where = new System.Text.StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                maxDelta = Math.Max(maxDelta, Math.Abs(a[i] - b[i]));
                if (diff++ < 6) where.Append($" ({i / 4 % px.Width},{i / 4 / px.Width}) {a[i]}≠{b[i]}");
            }
            using var full = h.Render();
            string kind = full.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(b) ? "damage too small" : "stale cache / different style";
            var dump = new System.Text.StringBuilder();
            foreach (var sp in model.All())
            {
                var el = h.Root.RootElement.FindAll(x => x.Id == "n" + sp.Id).FirstOrDefault();
                if (el is null) continue;
                var cs = el.ComputedStyle;
                dump.Append($"\nn{sp.Id} [{string.Join(" ", sp.Classes)}] rect {el.BoundingRect} op {cs?.Opacity} tr {cs?.Transform} rot {cs?.Rotate} vis {cs?.Visibility} disp {cs?.Display} anim {h.Pipeline.GetAnimationState(el) is not null}");
            }
            Assert.Fail($"seed {seed} scale {scale} step '{what}': {diff} bytes differ, max delta {maxDelta} ({kind}):{where} damage {h.Pipeline.LastDamage}\n" + string.Join("\n", log) + dump);
        }

        RunBoth();
        Check("mount");
        for (int step = 0; step < 50; step++)
        {
            string what;
            if (model.Rng.Next(3) == 0)
            {
                what = model.Toggle();
                version.Value++;
                refVersion.Value++;
            }
            else
            {
                double dt = model.Rng.Next(4) switch { 0 => 0.016, 1 => 0.05, 2 => 0.1, _ => 0.27 };
                clock.Advance(TimeSpan.FromSeconds(dt));
                refClock.Advance(TimeSpan.FromSeconds(dt));
                what = "advance " + dt;
            }
            log.Add(what);
            RunBoth();
            if (Environment.GetEnvironmentVariable("AWAUI_ANIM_TRACE") == "1")
            {
                foreach (var sp in model.All())
                {
                    var el = h.Root.RootElement.FindAll(x => x.Id == "n" + sp.Id).FirstOrDefault();
                    if (el is null) continue;
                    var cs = el.ComputedStyle;
                    log.Add($"   n{sp.Id} p=n{(el.Parent?.Id)} [{string.Join(" ", sp.Classes)}] {el.BoundingRect} op {cs?.Opacity} rot {cs?.Rotate} h {cs?.Height} w {cs?.Width} ml {cs?.MarginLeft} c {cs?.Color}");
                }
                log.Add($"   damage {h.Pipeline.LastDamage}");
            }
            Check(what);
        }
        Assert.True(animatedFrames > 5, "随机序列中应有运行中的动画");
    }
}
