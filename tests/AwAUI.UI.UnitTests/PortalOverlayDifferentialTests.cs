using AwAUI.Graphics;
using AwAUI.Render;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 覆盖层的增量绘制差分测试：打开 / 关闭 Portal（含嵌套与不同层级）、在 <see cref="UIRoot.LayoutCompleted"/> 中按锚点定位浮层、
/// 滚动锚点所在的容器、修改层内内容——增量录制 + 按损伤区域局部光栅化的累积结果必须与整帧重绘逐字节相同，命中测试结果也必须相同。
/// </summary>
public class PortalOverlayDifferentialTests
{
    const string Css = """
        .scroller { position: absolute; left: 10px; top: 10px; width: 160px; height: 120px; overflow: auto; background-color: #eeeeee; }
        .row { height: 30px; margin-bottom: 6px; background-color: #88aaff; }
        .anchor { width: 40px; height: 16px; background-color: #ff8800; }
        .pop { position: fixed; left: 0; top: 0; width: 70px; height: 34px; background-color: #22aa44; border: 2px solid #000000; }
        .pop.wide { width: 110px; }
        .pop .inner { width: 20px; height: 10px; background-color: #ffffff; }
        .scrim { position: fixed; left: 0; top: 0; right: 0; bottom: 0; background-color: rgba(0, 0, 0, 0.3); }
        .dialog { position: fixed; left: 60px; top: 40px; width: 100px; height: 70px; background-color: #ffffff; z-index: 50; }
        .toast { position: fixed; right: 4px; bottom: 4px; width: 50px; height: 20px; background-color: #aa00aa; }
        """;

    sealed class Spec
    {
        public bool Popover;
        public bool Wide;
        public bool Inner;
        public bool Dialog;
        public bool NestedInDialog;
        public bool Toast;
    }

    static Node Build(Spec s)
    {
        var rows = new NodeList();
        for (int i = 0; i < 8; i++)
            rows.Add(new Div { Id = "row" + i, Class = "row", Children = { i == 3 ? new Div { Id = "anchor", Class = "anchor" } : null } });
        return new Div
        {
            Children =
            {
                new Div { Id = "scroller", Class = "scroller", Children = rows },
                s.Toast ? new Portal { Key = "toast", ZOrder = OverlayZOrder.Toast, Children = { new Div { Id = "toast", Class = "toast" } } } : null,
                s.Dialog
                    ? new Portal
                    {
                        Key = "dialog",
                        Children =
                        {
                            new Div { Id = "scrim", Class = "scrim" },
                            new Div
                            {
                                Id = "dialog",
                                Class = "dialog",
                                Children = { s.NestedInDialog ? new Portal { Children = { new Div { Id = "nested", Class = "pop" } } } : null },
                            },
                        },
                    }
                    : null,
                s.Popover
                    ? new Portal
                    {
                        Key = "pop",
                        Children = { new Div { Id = "pop", Class = s.Wide ? "pop wide" : "pop", Children = { s.Inner ? new Div { Class = "inner" } : null } } },
                    }
                    : null,
            },
        };
    }

    /// <summary>浮层定位：贴在锚点下方（与 PopperContent 的做法相同：布局完成时写运行时覆盖）。</summary>
    static void Position(UIRoot root)
    {
        var anchor = Find(root.RootElement, "anchor");
        if (anchor is null) return;
        var a = anchor.GetBoundingClientRect();
        foreach (var id in new[] { "pop", "nested" })
        {
            var pop = Find(root.RootElement, id);
            if (pop is null) continue;
            pop.SetStyleOverride("left", a.Left.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px");
            pop.SetStyleOverride("top", (a.Bottom + (id == "nested" ? 20 : 2)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px");
        }
    }

    static Element? Find(Element e, string id)
    {
        if (e.Id == id) return e;
        foreach (var c in e.Children)
            if (Find(c, id) is { } f) return f;
        return null;
    }

    public static IEnumerable<object[]> Seeds()
    {
        for (int seed = 1; seed <= 12; seed++) yield return [seed, seed % 3 == 0 ? 1.5f : seed % 3 == 1 ? 1f : 2f];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Overlay_changes_match_full_repaint(int seed, float scale)
    {
        const float W = 200, H = 160;
        var rng = new Random(seed);
        var spec = new Spec();
        var version = new State<int>(0);
        var refVersion = new State<int>(0);
        var h = new PipelineHarness(new Lambda { Render = () => { _ = version.Value; return Build(spec); } }, Css, W, H, scale);
        var reference = new PipelineHarness(new Lambda { Render = () => { _ = refVersion.Value; return Build(spec); } }, Css, W, H, scale);
        reference.Pipeline.IncrementalPaint = false;
        // 经典滚动条不随时间淡出：两条管线的滚动条外观只取决于滚动位置
        h.Pipeline.ScrollbarStyle = Platform.ScrollbarStyle.Classic;
        reference.Pipeline.ScrollbarStyle = Platform.ScrollbarStyle.Classic;
        h.Root.LayoutCompleted += Position;
        reference.Root.LayoutCompleted += Position;
        h.Frame();
        reference.Frame();
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(h.Pipeline.Viewport, h.Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, scale);
        long rendered = -1;
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
                int diff = 0;
                var where = new System.Text.StringBuilder();
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == b[i]) continue;
                    if (diff++ < 4) where.Append($" ({i / 4 % px.Width},{i / 4 / px.Width}) {a[i]}≠{b[i]}");
                }
                using var full = h.Render();
                string kind = full.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(b) ? "damage too small" : "stale cache";
                Assert.Fail($"seed {seed} scale {scale} step '{what}': {diff} bytes differ ({kind}){where}, damage {h.Pipeline.LastDamage}\n" + string.Join("\n", log));
            }
            for (int y = 3; y < H; y += 11)
            for (int x = 3; x < W; x += 9)
            {
                var p = new Point(x, y);
                var hi = h.Pipeline.HitTest(p)?.Id;
                var hr = reference.Pipeline.HitTest(p)?.Id;
                if (hi != hr) Assert.Fail($"seed {seed} step '{what}': hit test at {p}: {hi} vs {hr}\n" + string.Join("\n", log));
            }
        }

        Present();
        Check("initial");
        for (int step = 0; step < 40; step++)
        {
            string what;
            bool scroll = false;
            float to = 0;
            switch (rng.Next(8))
            {
                case 0: spec.Popover = !spec.Popover; what = "popover " + spec.Popover; break;
                case 1: spec.Dialog = !spec.Dialog; what = "dialog " + spec.Dialog; break;
                case 2: spec.NestedInDialog = !spec.NestedInDialog; what = "nested " + spec.NestedInDialog; break;
                case 3: spec.Toast = !spec.Toast; what = "toast " + spec.Toast; break;
                case 4: spec.Wide = !spec.Wide; what = "wide " + spec.Wide; break;
                case 5: spec.Inner = !spec.Inner; what = "inner " + spec.Inner; break;
                default:
                    scroll = true;
                    to = rng.Next(0, 9) * 17.5f;
                    what = "scroll " + to;
                    break;
            }
            log.Add(what);
            if (scroll)
            {
                h.El("scroller").ScrollController.JumpTo(new Point(0, to));
                reference.El("scroller").ScrollController.JumpTo(new Point(0, to));
            }
            else
            {
                version.Value++;
                refVersion.Value++;
            }
            h.Root.RunFrame();
            Present();
            for (int i = 0; i < 5 && h.Root.NeedsFrame; i++)
            {
                h.Root.RunFrame();
                Present();
            }
            reference.Frame();
            Check(what);
            // 浮层与锚点在同一帧内对齐
            if (spec.Popover)
            {
                var a = h.El("anchor").GetBoundingClientRect();
                Assert.Equal(a.Left, h.El("pop").GetBoundingClientRect().Left, 2);
            }
        }
    }
}
