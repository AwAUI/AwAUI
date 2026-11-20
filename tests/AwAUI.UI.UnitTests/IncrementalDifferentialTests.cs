using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Svg;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 增量绘制的随机差分测试：同一棵随机树、同一串随机修改（class、内联尺寸、文本、结构、指针悬停），
/// 增量录制 + 按损伤区域局部光栅化的累积结果必须与关闭增量绘制、整帧重绘的结果逐字节相同，命中测试结果也必须相同。
/// </summary>
public class IncrementalDifferentialTests
{
    const string Css = """
        .n { min-height: 4px; }
        .a { background-color: #ff0000; }
        .b { background-color: #00aa00; border: 2px solid #000000; }
        .c { opacity: 0.5; }
        .d { position: relative; left: 7px; top: 3px; }
        .e { position: absolute; left: 30px; top: 20px; width: 40px; height: 30px; background-color: #8800ff; }
        .f { overflow: hidden; }
        .g { z-index: 3; }
        .h { display: none; }
        .i { visibility: hidden; }
        .j { outline: 2px solid #0000ff; outline-offset: 2px; }
        .k { opacity: 0; }
        .l { color: #00aaaa; }
        .m { border-radius: 6px; background-color: #ffaa00; }
        .p { padding: 3px; }
        .q { display: flex; flex-direction: row; gap: 3px; }
        .r { line-height: 4px; font-size: 14px; }
        .s { position: fixed; right: 5px; bottom: 5px; width: 20px; height: 20px; background-color: #444444; }
        .t { z-index: -1; position: relative; background-color: #cccccc; }
        .u { margin-top: -6px; }
        .v { margin-top: 150px; }
        :hover > .hv, .hv:hover { background-color: #ffee00; }
        .xr { transform: rotate(12deg) scale(0.9); }
        .xt { translate: 6px -3px; scale: 1.2; }
        .fb { filter: blur(1.5px); }
        .fd { filter: drop-shadow(3px 2px 2px #000000) grayscale(0.7); }
        .bd { backdrop-filter: blur(2px) invert(1); }
        .cc { clip-path: circle(45%); }
        .mb { mix-blend-mode: multiply; background-color: #66ccff; }
        .gb::before { content: 'ab'; color: #cc00cc; }
        .ga { position: relative; }
        .ga::after { content: ''; position: absolute; left: 2px; top: 2px; width: 6px; height: 6px; background-color: #00cc88; }
        .ud { text-decoration: underline wavy #aa0000; text-shadow: 1px 1px 0 #00aa00; }
        .ls { letter-spacing: 2px; text-indent: 5px; }
        """;

    static readonly string[] Classes = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "p", "q", "r", "s", "t", "u", "v", "k", "c", "hv"];

    /// <summary>第九批：变换、滤镜、背景滤镜、clip-path 与混合模式（只在带效果的变体中使用，原有种子的随机序列不变）。</summary>
    static readonly string[] EffectClasses = [.. Classes, "xr", "xt", "fb", "fd", "bd", "cc", "mb", "xr", "fb"];

    /// <summary>第九批：伪元素（::before 文字、绝对定位的 ::after 盒）、文本装饰与阴影、字距与缩进。</summary>
    static readonly string[] ContentClasses = [.. Classes, "gb", "ga", "ud", "ls", "gb", "ga"];

    const string TintedSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='12' height='12'><circle cx='5' cy='5' r='4' fill='currentColor'/></svg>";

    sealed class Spec
    {
        public int Id;
        public int Kind; // 0 div，1 text，2 svg，3 input
        public HashSet<string> Classes = new();
        public string Style = "";
        public string Text = "x";
        public List<Spec> Children = new();
    }

    sealed class Model
    {
        public Spec Root = new() { Id = 0 };
        public int NextId = 1;
        public readonly Random Rng;
        public readonly SvgDocument Svg = SvgDocument.Parse(TintedSvg);

        readonly string[] _classes;

        public Model(int seed, bool effects = false, bool content = false)
        {
            _classes = content ? ContentClasses : effects ? EffectClasses : Classes;
            Rng = new Random(seed);
            for (int i = 0; i < 14; i++) AddRandom();
        }

        public List<Spec> All()
        {
            var list = new List<Spec>();
            var stack = new Stack<Spec>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                list.Add(s);
                foreach (var c in s.Children) stack.Push(c);
            }
            return list;
        }

        Spec RandomContainer()
        {
            var all = All().Where(s => s.Kind == 0).ToList();
            return all[Rng.Next(all.Count)];
        }

        public void AddRandom()
        {
            var parent = RandomContainer();
            int kind = Rng.Next(12) switch { < 7 => 0, < 10 => 1, 10 => 2, _ => 3 };
            var s = new Spec { Id = NextId++, Kind = kind, Text = RandomText() };
            if (Rng.Next(3) == 0) s.Classes.Add(_classes[Rng.Next(_classes.Length)]);
            s.Style = RandomStyle();
            parent.Children.Insert(Rng.Next(parent.Children.Count + 1), s);
        }

        string RandomText() => Rng.Next(4) switch { 0 => "", 1 => "ab", 2 => "hello world", _ => new string('w', Rng.Next(1, 30)) };

        string RandomStyle()
        {
            var parts = new List<string>();
            if (Rng.Next(2) == 0) parts.Add("width: " + RandomLength() + "px");
            if (Rng.Next(2) == 0) parts.Add("height: " + RandomLength() + "px");
            if (Rng.Next(4) == 0) parts.Add("margin-left: " + (Rng.Next(0, 9) * 0.75f).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px");
            if (Rng.Next(4) == 0) parts.Add("padding-top: " + (Rng.Next(0, 7) * 0.5f).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px");
            return string.Join("; ", parts);
        }

        string RandomLength() => (Rng.Next(2, 60) + Rng.Next(4) * 0.25f).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>一次随机修改；返回描述。</summary>
        public string Mutate()
        {
            var all = All();
            var target = all[Rng.Next(all.Count)];
            // 偏向「先隐藏 / 移出视口 / 逃逸某个子树，再改它的后代，再恢复」：元素在不被录制的帧里变化是缓存失效最容易出错的地方
            if (Hider is { } hider && all.Contains(hider))
            {
                if (Rng.Next(4) == 0)
                {
                    Hider = null;
                    hider.Classes.Remove(HiderClass!);
                    return "restore ." + HiderClass + " on n" + hider.Id;
                }
                var inside = new List<Spec>();
                Collect(hider, inside);
                if (inside.Count > 1 && Rng.Next(3) != 0) target = inside[1 + Rng.Next(inside.Count - 1)];
            }
            else if (Rng.Next(6) == 0)
            {
                var c = HiderClasses[Rng.Next(HiderClasses.Length)];
                if (target.Kind == 0 && target.Children.Count > 0 && target.Classes.Add(c))
                {
                    Hider = target;
                    HiderClass = c;
                    return "hide ." + c + " on n" + target.Id;
                }
            }
            switch (Rng.Next(9))
            {
                case 0:
                case 1:
                case 2:
                {
                    var c = _classes[Rng.Next(_classes.Length)];
                    if (!target.Classes.Remove(c)) target.Classes.Add(c);
                    return "toggle ." + c + " on n" + target.Id;
                }
                case 3:
                    target.Style = RandomStyle();
                    return "style n" + target.Id + " = " + target.Style;
                case 4:
                    target.Text = RandomText();
                    return "text n" + target.Id + " = '" + target.Text + "'";
                case 5:
                    if (all.Count < 40)
                    {
                        AddRandom();
                        return "add";
                    }
                    goto case 6;
                case 6:
                {
                    if (target == Root) return "noop";
                    var parent = all.First(p => p.Children.Contains(target));
                    parent.Children.Remove(target);
                    return "remove n" + target.Id;
                }
                case 7:
                {
                    if (target.Children.Count < 2) return "noop";
                    var ch = target.Children;
                    var x = ch[Rng.Next(ch.Count)];
                    ch.Remove(x);
                    ch.Insert(Rng.Next(ch.Count + 1), x);
                    return "move child n" + x.Id + " in n" + target.Id;
                }
                default:
                {
                    if (target == Root) return "noop";
                    // 整个节点搬到另一个父节点下（保留其子树）
                    var parent = all.First(p => p.Children.Contains(target));
                    var dest = RandomContainer();
                    if (Contains(target, dest)) return "noop";
                    parent.Children.Remove(target);
                    dest.Children.Insert(Rng.Next(dest.Children.Count + 1), target);
                    return "reparent n" + target.Id + " to n" + dest.Id;
                }
            }
        }

        Spec? Hider;
        string? HiderClass;

        static readonly string[] HiderClasses = ["k", "v", "f", "c", "h", "i"];

        static void Collect(Spec s, List<Spec> into)
        {
            into.Add(s);
            foreach (var c in s.Children) Collect(c, into);
        }

        static bool Contains(Spec ancestor, Spec x)
        {
            if (ancestor == x) return true;
            foreach (var c in ancestor.Children)
                if (Contains(c, x)) return true;
            return false;
        }

        public Node Build(Spec s)
        {
            string cls = "n " + string.Join(" ", s.Classes.OrderBy(c => c, StringComparer.Ordinal));
            if (s.Kind == 1) return new Text(s.Text) { Key = s.Id, Id = "n" + s.Id, Class = cls, Style = s.Style };
            if (s.Kind == 2) return new Svg(Svg) { Key = s.Id, Id = "n" + s.Id, Class = cls, Style = s.Style };
            if (s.Kind == 3) return new Input { Key = s.Id, Id = "n" + s.Id, Class = cls, Style = s.Style, Value = s.Text };
            var children = new NodeList();
            foreach (var c in s.Children) children.Add(Build(c));
            if (s.Text.Length > 0 && s.Children.Count == 0) children.Add(s.Text);
            return new Div { Key = s.Id, Id = "n" + s.Id, Class = cls, Style = s.Style, Children = children };
        }
    }

    public static IEnumerable<object[]> Seeds()
    {
        // AWAUI_UI_DIFF_SEEDS=n 跑更多种子（本地压力测试）
        int count = int.TryParse(Environment.GetEnvironmentVariable("AWAUI_UI_DIFF_SEEDS"), out int n) && n > 0 ? n : 24;
        for (int seed = 1; seed <= count; seed++) yield return [seed, seed % 3 == 0 ? 1.5f : seed % 3 == 1 ? 1f : 2f];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_mutations_match_full_repaint(int seed, float scale) => Run(seed, scale, effects: false);

    /// <summary>第九批：随机树中的元素还会切换变换、滤镜、背景滤镜、clip-path 与混合模式。</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_mutations_with_effects_match_full_repaint(int seed, float scale) => Run(seed + 5000, scale, effects: true);

    public static IEnumerable<object[]> ContentSeeds()
    {
        bool hasRegression = false;
        foreach (var seed in Seeds())
        {
            hasRegression |= (int)seed[0] == 101;
            yield return seed;
        }
        if (!hasRegression) yield return [101, 2f];
    }

    /// <summary>第九批：随机树中的元素还会切换伪元素、文本装饰、阴影与缩进。</summary>
    [Theory]
    [MemberData(nameof(ContentSeeds))]
    public void Random_mutations_with_generated_content_match_full_repaint(int seed, float scale) => Run(seed + 9000, scale, effects: false, content: true);

    static void Run(int seed, float scale, bool effects, bool content = false)
    {
        const float W = 220, H = 180;
        var model = new Model(seed, effects, content);
        var version = new State<int>(0);
        var refVersion = new State<int>(0);
        var h = new PipelineHarness(new Lambda { Render = () => { _ = version.Value; return model.Build(model.Root); } }, Css, W, H, scale);
        var reference = new PipelineHarness(new Lambda { Render = () => { _ = refVersion.Value; return model.Build(model.Root); } }, Css, W, H, scale);
        reference.Pipeline.IncrementalPaint = false;
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(h.Pipeline.Viewport, h.Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, scale);
        long rendered = -1;
        PixelRect? lastDamagePx = null;
        var log = new List<string>();

        void Present()
        {
            var p = h.Pipeline;
            PixelRect? damage = null;
            if (rendered >= 0 && p.FrameVersion == rendered + 1 && p.LastDamage is { } d)
            {
                int x0 = Math.Max(0, (int)Math.Floor(d.Left * scale) - 1), y0 = Math.Max(0, (int)Math.Floor(d.Top * scale) - 1);
                int x1 = (int)Math.Ceiling(d.Right * scale) + 1, y1 = (int)Math.Ceiling(d.Bottom * scale) + 1;
                damage = d.Width <= 0 ? new PixelRect(0, 0, 0, 0) : new PixelRect(x0, y0, x1 - x0, y1 - y0);
            }
            lastDamagePx = damage;
            if (p.FrameVersion != rendered && damage is not { IsEmpty: true }) target.Render(p.Frame, new RenderOptions(Color.White, damage));
            rendered = p.FrameVersion;
        }

        void Check(string what)
        {
            using var expected = reference.Render();
            var a = target.Bitmap.ToRgbaUnpremultiplied();
            var b = expected.ToRgbaUnpremultiplied();
            if (!a.AsSpan().SequenceEqual(b))
            {
                int diff = 0, maxDelta = 0;
                var where = new System.Text.StringBuilder();
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == b[i]) continue;
                    maxDelta = Math.Max(maxDelta, Math.Abs(a[i] - b[i]));
                    if (diff++ < 6) where.Append($" ({i / 4 % px.Width},{i / 4 / px.Width}) {a[i]}≠{b[i]}");
                }
                using var full = h.Render();
                bool cacheBug = !full.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(b);
                string kind = cacheBug ? "stale cache" : "damage too small";
                if (!cacheBug && lastDamagePx is { } dpx)
                {
                    // 同一帧整帧光栅化之后再只按损伤矩形重画一遍：结果若变了，说明光栅结果依赖裁剪（渲染器问题），与缓存 / 损伤无关
                    using var t2 = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, scale);
                    t2.Render(h.Pipeline.Frame, new RenderOptions(Color.White));
                    t2.Render(h.Pipeline.Frame, new RenderOptions(Color.White, dpx));
                    if (!t2.Bitmap.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(b)) kind = "raster depends on clip";
                }
                // 第七批起软件光栅结果与裁剪无关，三类差异都不容忍
                Assert.Fail($"seed {seed} scale {scale} step '{what}': {diff} bytes differ, max delta {maxDelta} ({kind}):{where} damage {h.Pipeline.LastDamage}\n" + string.Join("\n", log));
            }
            // 命中测试：增量路径对复用的子树只遍历不录制，结果必须与整帧录制相同
            for (int y = 3; y < H; y += 17)
            for (int x = 3; x < W; x += 13)
            {
                var p = new Point(x, y);
                var hi = h.Pipeline.HitTest(p)?.Id;
                var hr = reference.Pipeline.HitTest(p)?.Id;
                if (hi != hr) Assert.Fail($"seed {seed} step '{what}': hit test at {p}: {hi} vs {hr}\n" + string.Join("\n", log));
            }
        }

        Present();
        Check("mount");
        for (int step = 0; step < 60; step++)
        {
            string what;
            if (model.Rng.Next(5) == 0)
            {
                float x = model.Rng.Next((int)W), y = model.Rng.Next((int)H);
                h.Move(x, y);
                reference.Move(x, y);
                what = $"hover {x},{y}";
            }
            else
            {
                what = model.Mutate();
                version.Value++;
                refVersion.Value++;
            }
            log.Add(what);
            h.Frame();
            reference.Frame();
            Present();
            Check(what);
        }
    }
}
