using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;
using Xunit.Abstractions;

namespace AwAUI.UI.UnitTests;

/// <summary>宿主的局部光栅化（软件后端 + 损伤区域）、DevTools 叠加层与 1000 行列表的悬停帧开销。</summary>
public partial class IncrementalHostTests
{
    readonly ITestOutputHelper _output;

    public IncrementalHostTests(ITestOutputHelper output) => _output = output;

    const string ListCss = """
        :root { font-size: 13px; background-color: #f1f5f9; }
        .list { display: flex; flex-direction: column; gap: 4px; padding: 8px; }
        .row { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 6px;
               background-color: #ffffff; border: 1px solid #e2e8f0; }
        .row:hover { background-color: #f8fafc; }
        .row.selected { background-color: #eef2ff; border-color: #a5b4fc; }
        .idx { width: 56px; color: #64748b; }
        .name { flex: 1; }
        .btn { padding: 2px 10px; border-radius: 4px; }
        """;

    /// <summary>与 samples/Counter 的 --stress 相同结构的列表（每行：序号、名字、按钮）。</summary>
    sealed partial class StressList : Component
    {
        public int Count { get; set; }

        public readonly State<int> Selected = new(-1);

        protected override Node Build()
        {
            var rows = new NodeList();
            for (int i = 0; i < Count; i++)
            {
                int index = i;
                rows.Add(new Div
                {
                    Key = i,
                    Id = "row-" + i,
                    Class = i == Selected.Value ? "row selected" : "row",
                    Children =
                    [
                        new Text("#" + i) { Class = "idx" },
                        new Text("Item number " + i + " — the quick brown fox") { Class = "name" },
                        new Button { Class = "btn", OnClick = _ => Selected.Value = index, Children = ["Select"] },
                    ],
                });
            }
            return new Div { Class = "list", Children = rows };
        }
    }

    static void Move(HeadlessWindow window, Point p) =>
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));

    static UIHost CreateHost(HeadlessPlatform platform, StressList list, float w = 800, float h = 600) =>
        AwApp.CreateHost(platform, new WindowOptions { Size = new Size(w, h) }, new SoftwareRenderBackend(), new TestTextEngine(), list, AwAUI.Css.StyleSheet.Parse(ListCss));

    static byte[] Pixels(Bitmap b) => b.ToRgbaUnpremultiplied();

    [Fact]
    public void Hover_rasterizes_only_the_damaged_rows_and_matches_a_full_render()
    {
        var platform = new HeadlessPlatform { DefaultScale = 2 };
        var list = new StressList { Count = 60 };
        var host = CreateHost(platform, list, 400, 300);
        var window = (HeadlessWindow)host.Window;
        platform.RunFrame();
        Assert.Null(host.LastDamage);
        var r3 = host.Root.RootElement.ById("row-3").BoundingRect;
        var r5 = host.Root.RootElement.ById("row-5").BoundingRect;
        Move(window, r3.Center);
        platform.RunFrame();
        Move(window, r5.Center);
        platform.RunFrame();
        var damage = host.LastDamage!.Value;
        // 两行（离开的 row-3 与进入的 row-5）及其间的区域，远小于整个窗口（800×600 设备像素）
        Assert.True(damage.Height < (r5.Bottom - r3.Top) * 2 + 8, damage.ToString());
        Assert.True(damage.Y >= (int)(r3.Top * 2) - 4);

        // 与关闭增量绘制、整帧光栅化的结果逐字节相同
        var reference = new HeadlessPlatform { DefaultScale = 2 };
        var refList = new StressList { Count = 60 };
        var refHost = CreateHost(reference, refList, 400, 300);
        refHost.Pipeline.IncrementalPaint = false;
        refHost.PartialRaster = false;
        var refWindow = (HeadlessWindow)refHost.Window;
        reference.RunFrame();
        Move(refWindow, r3.Center);
        reference.RunFrame();
        Move(refWindow, r5.Center);
        reference.RunFrame();
        Assert.Null(refHost.LastDamage);
        Assert.True(Pixels(window.Framebuffer!).AsSpan().SequenceEqual(Pixels(refWindow.Framebuffer!)));
    }

    [Fact]
    public void DevTools_highlight_forces_a_full_raster_and_follows_cached_elements()
    {
        var platform = new HeadlessPlatform { DefaultScale = 1 };
        var list = new StressList { Count = 20 };
        var host = CreateHost(platform, list, 400, 300);
        var window = (HeadlessWindow)host.Window;
        platform.RunFrame();
        var dt = host.EnableDevTools();
        var row = host.Root.RootElement.ById("row-2");
        dt.Highlight(row);
        host.RequestFrame();
        platform.RunFrame();
        Assert.Null(host.LastDamage);
        Assert.NotSame(host.Pipeline.Frame, host.PresentedFrame);
        // 选中另一行：row-2 的缓存不变（只是复用），高亮仍画在它的布局位置上
        list.Selected.Value = 7;
        platform.RunFrame();
        Assert.DoesNotContain(row, host.Pipeline.LastRecordedElements);
        Assert.Null(host.LastDamage);
        var c = window.Framebuffer!.GetPixel((int)row.BoundingRect.X + 30, (int)row.BoundingRect.Y + 3);
        var plain = new HeadlessPlatform { DefaultScale = 1 };
        var plainHost = CreateHost(plain, new StressList { Count = 20 }, 400, 300);
        plain.RunFrame();
        var p = ((HeadlessWindow)plainHost.Window).Framebuffer!.GetPixel((int)row.BoundingRect.X + 30, (int)row.BoundingRect.Y + 3);
        Assert.NotEqual(p, c); // 高亮的半透明覆盖改变了这一行的颜色
        dt.Highlight(null);
        platform.RunFrame();
        Assert.Null(host.LastDamage);
    }

    /// <summary>
    /// 1000 行列表在两行之间悬停的单帧开销：增量绘制 + 局部光栅化 vs. 整帧重录 + 整帧光栅化。
    /// 设置环境变量 AWAUI_UI_BENCH=1 才运行（用优化构建：-p:Optimize=true --no-incremental）。
    /// </summary>
    [Fact]
    public void Benchmark_hover_frame_on_1000_rows()
    {
        if (Environment.GetEnvironmentVariable("AWAUI_UI_BENCH") != "1") return;
        foreach (bool incremental in new[] { false, true, false, true })
        {
            var platform = new HeadlessPlatform { DefaultScale = 2 };
            var list = new StressList { Count = 1000 };
            var host = CreateHost(platform, list);
            host.Pipeline.IncrementalPaint = incremental;
            host.PartialRaster = incremental;
            var window = (HeadlessWindow)host.Window;
            platform.RunFrame();
            var mount = host.MountStatistics.Pipeline;
            var firstRaster = host.LastFrame.RasterMs;
            var a = host.Root.RootElement.ById("row-1").BoundingRect.Center;
            var b = host.Root.RootElement.ById("row-3").BoundingRect.Center;
            var frames = new List<HostFrameStatistics>();
            for (int i = 0; i < 200; i++)
            {
                Move(window, i % 2 == 0 ? a : b);
                platform.RunFrame();
                if (i >= 40) frames.Add(host.LastFrame);
            }
            static double Median(IEnumerable<double> v)
            {
                var s = v.OrderBy(x => x).ToArray();
                return s[s.Length / 2];
            }
            var last = frames[frames.Count - 1].Pipeline;
            _output.WriteLine(FormattableString.Invariant(
                $"{(incremental ? "incremental" : "full       ")}: style {Median(frames.Select(f => f.Pipeline.StyleMs)):F3}  layout {Median(frames.Select(f => f.Pipeline.LayoutMs)):F3}  paint {Median(frames.Select(f => f.Pipeline.PaintMs)):F3}  raster {Median(frames.Select(f => f.RasterMs)):F3}  total {Median(frames.Select(f => f.TotalMs)):F3} ms; recorded {last.RecordedElements}, reused {last.ReusedElements}, top-level cmds {last.CommandCount}, damage {host.LastDamage?.ToString() ?? "full"}; mount paint {mount.PaintMs:F2} ms ({mount.RecordedElements} recorded), first raster {firstRaster:F2} ms"));
        }
    }
}
