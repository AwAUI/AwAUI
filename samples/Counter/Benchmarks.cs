using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI;
using AwAUI.Css;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Rhi.Metal;
using AwAUI.Text;
using AwAUI.UI;

namespace CounterSample;

/// <summary>
/// 帧耗时测量（无窗口平台 + 软件后端；有 Metal 时另把同样的帧交给 GPU 后端的离屏目标，比较光栅化的 CPU 耗时）。
/// 数字是墙钟时间，受机器负载影响，只作量级参考。
/// </summary>
static class Benchmarks
{
    public static void Counter(float scale, TextEngine textEngine, StyleSheet[] sheets, WindowOptions options, bool gpu)
    {
        var platform = new HeadlessPlatform { DefaultScale = scale };
        var host = AwApp.CreateHost(platform, options, new SoftwareRenderBackend(), textEngine, new App(), sheets);
        var window = (HeadlessWindow)host.Window;
        platform.RunFrame();
        Console.WriteLine($"Counter @{scale}x, {window.ClientSize.Width}x{window.ClientSize.Height} logical px");
        Console.WriteLine("  mount frame (cold, includes JIT): " + host.MountStatistics);
        Console.WriteLine("  first raster:                     " + host.LastFrame);

        for (int i = 0; i < 30; i++) Click(platform, window, host); // 预热 JIT
        var clicks = new List<HostFrameStatistics>();
        for (int i = 0; i < 300; i++) clicks.Add(Click(platform, window, host));
        Report("  click → +1 (restyle :active, text change, relayout, repaint, raster)", clicks);

        if (gpu) GpuRaster(platform, window, host);

        var hovers = new List<HostFrameStatistics>();
        var inc = host.Root.RootElement.FindById("increment")!.BoundingRect.Center;
        var empty = new Point(4, 4);
        for (int i = 0; i < 300; i++)
        {
            Move(window, i % 2 == 0 ? empty : inc);
            platform.RunFrame();
            hovers.Add(host.LastFrame);
        }
        Report("  hover on/off button (restyle 1 element, repaint, raster)", hovers);

        var full = new List<HostFrameStatistics>();
        for (int i = 0; i < 100; i++)
        {
            host.Pipeline.InvalidateAll();
            platform.RunFrame();
            full.Add(host.LastFrame);
        }
        Report("  full invalidation (restyle all, rebuild paragraphs, relayout, repaint, raster)", full);
    }

    /// <summary>
    /// 同样的点击帧，光栅化分别交给软件与 GPU 后端的离屏目标（同尺寸、同缩放），只计光栅化：样式 / 布局 / 绘制与后端无关。
    /// GPU 的 CPU 侧是 Render 调用（录制 + 编码 + 提交），逐帧等待 GPU 完成，GPU 执行时间单列。
    /// </summary>
    static void GpuRaster(HeadlessPlatform platform, HeadlessWindow window, UIHost host)
    {
        using var metal = MetalRenderer.TryCreate();
        if (metal is null)
        {
            Console.WriteLine("  (Metal is not available: GPU raster comparison skipped)");
            return;
        }
        var size = window.Framebuffer!.Size;
        double scale = window.RenderScale;
        var lists = new List<DisplayList>();
        for (int i = 0; i < 330; i++)
        {
            Click(platform, window, host);
            lists.Add(host.PresentedFrame);
        }
        using var sw = new SoftwareRenderBackend();
        using var swTarget = sw.CreateOffscreenTarget(size, scale);
        using var gpuTarget = metal.CreateOffscreenTarget(size, scale);
        var swMs = new List<double>();
        var gpuCpu = new List<double>();
        var gpuExec = new List<double>();
        for (int i = 0; i < lists.Count; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            swTarget.Render(lists[i], new RenderOptions(host.ClearColor));
            double a = t.Elapsed.TotalMilliseconds;
            t.Restart();
            gpuTarget.Render(lists[i], new RenderOptions(host.ClearColor));
            double b = t.Elapsed.TotalMilliseconds;
            metal.WaitForGpu();
            if (i < 30) continue;
            swMs.Add(a);
            gpuCpu.Add(b);
            if (metal.LastFrameStats?.GpuTime is { } g) gpuExec.Add(g.TotalMilliseconds);
        }
        static string Med(List<double> v)
        {
            var a = v.OrderBy(x => x).ToArray();
            return a.Length == 0 ? "n/a" : FormattableString.Invariant($"median {a[a.Length / 2]:F3} ms, p95 {a[(int)(a.Length * 0.95)]:F3} ms");
        }
        Console.WriteLine($"  raster only, same {lists.Count - 30} click frames, full-frame, {size.Width}x{size.Height} px ({metal.Device.Name}):");
        Console.WriteLine("    software:               " + Med(swMs));
        Console.WriteLine("    GPU CPU side:           " + Med(gpuCpu));
        Console.WriteLine("    GPU execution:          " + Med(gpuExec));
    }

    static HostFrameStatistics Click(HeadlessPlatform platform, HeadlessWindow window, UIHost host)
    {
        SampleSetup.ClickElement(window, host, "increment");
        platform.RunFrame();
        return host.LastFrame;
    }

    static void Move(HeadlessWindow window, Point p) =>
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));

    static void Report(string title, List<HostFrameStatistics> frames)
    {
        static double Median(IEnumerable<double> v)
        {
            var a = v.OrderBy(x => x).ToArray();
            return a.Length == 0 ? 0 : a[a.Length / 2];
        }
        var rendered = frames.Where(f => f.Rendered).ToList();
        Console.WriteLine(title);
        Console.WriteLine(FormattableString.Invariant(
            $"    median of {frames.Count}: build {Median(frames.Select(f => f.BuildMs)):F3}  style {Median(frames.Select(f => f.Pipeline.StyleMs)):F3}  layout {Median(frames.Select(f => f.Pipeline.LayoutMs)):F3}  paint {Median(frames.Select(f => f.Pipeline.PaintMs)):F3}  raster {Median(rendered.Select(f => f.RasterMs)):F3}  total {Median(frames.Select(f => f.TotalMs)):F3} ms"));
        var last = frames[frames.Count - 1].Pipeline;
        Console.WriteLine($"    last frame: {last.StyledElements} styled, {last.ParagraphsBuilt} paragraphs, {last.CommandCount} cmds, rendered {rendered.Count}/{frames.Count}");
    }

    // ------------------------------------------------------------ 压力测试：N 行列表

    const string StressCss = """
        :root { font-family: sans-serif; font-size: 13px; background-color: #f1f5f9; }
        .list { display: flex; flex-direction: column; gap: 4px; padding: 8px; }
        .row { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 6px;
               background-color: #ffffff; border: 1px solid #e2e8f0; }
        .row:hover { background-color: #f8fafc; }
        .row.selected { background-color: #eef2ff; border-color: #a5b4fc; }
        .idx { width: 56px; color: #64748b; }
        .name { flex: 1; }
        .btn { padding: 2px 10px; border-radius: 4px; }
        """;

    public static void Stress(int rows, float scale, TextEngine textEngine)
    {
        var platform = new HeadlessPlatform { DefaultScale = scale };
        var options = new WindowOptions { Title = "stress", Size = new Size(800, 600) };
        var list = new StressList { Count = rows };
        var host = AwApp.CreateHost(platform, options, new SoftwareRenderBackend(), textEngine, list, StyleSheet.Parse(StressCss));
        var window = (HeadlessWindow)host.Window;
        platform.RunFrame();
        Console.WriteLine($"Stress list: {rows} rows ({rows * 6 + 2} elements incl. text nodes) @{scale}x, viewport 800x600");
        Console.WriteLine("  mount frame (cold, includes JIT): " + host.MountStatistics);
        Console.WriteLine("  first raster:                     " + host.LastFrame);

        var full = new List<HostFrameStatistics>();
        for (int i = 0; i < 12; i++)
        {
            host.Pipeline.InvalidateAll();
            platform.RunFrame();
            full.Add(host.LastFrame);
        }
        Report("  full invalidation (restyle all, rebuild paragraphs, relayout, repaint, raster)", full);

        var select = new List<HostFrameStatistics>();
        for (int i = 0; i < 40; i++)
        {
            list.Select(i % 10);
            platform.RunFrame();
            select.Add(host.LastFrame);
        }
        Report("  select a row (rebuild list, 2 class changes → restyle 2 rows, repaint)", select);

        var hover = new List<HostFrameStatistics>();
        var r0 = host.Root.RootElement.FindById("row-1")!.BoundingRect.Center;
        var r1 = host.Root.RootElement.FindById("row-3")!.BoundingRect.Center;
        for (int i = 0; i < 40; i++)
        {
            Move(window, i % 2 == 0 ? r0 : r1);
            platform.RunFrame();
            hover.Add(host.LastFrame);
        }
        Report("  hover between rows (restyle 2 rows, repaint)", hover);

        var text = new List<HostFrameStatistics>();
        for (int i = 0; i < 40; i++)
        {
            list.Rename(5, "Renamed item " + i);
            platform.RunFrame();
            text.Add(host.LastFrame);
        }
        Report("  change one row's text (rebuild 1 paragraph, relayout path, repaint)", text);
    }
}

/// <summary>压力测试用的列表组件。</summary>
public partial class StressList : Component
{
    public int Count { get; set; }

    readonly State<int> selected = new(-1);
    readonly State<(int Index, string Name)> renamed = new((-1, ""));

    public void Select(int i) => selected.Value = i;

    public void Rename(int i, string name) => renamed.Value = (i, name);

    protected override Node Build()
    {
        var rows = new NodeList();
        for (int i = 0; i < Count; i++)
        {
            int index = i;
            string name = renamed.Value.Index == i ? renamed.Value.Name : "Item number " + i + " — the quick brown fox";
            rows.Add(new Div
            {
                Key = i,
                Id = "row-" + i,
                Class = i == selected.Value ? "row selected" : "row",
                Children =
                [
                    new Text("#" + i) { Class = "idx" },
                    new Text(name) { Class = "name" },
                    new Button { Class = "btn", OnClick = _ => selected.Value = index, Children = ["Select"] },
                ],
            });
        }
        return new Div { Class = "list", Children = rows };
    }
}
