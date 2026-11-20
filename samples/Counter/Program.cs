using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AwAUI;
using AwAUI.Css;
using AwAUI.DevTools;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.MacOS;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Rhi.Metal;
using AwAUI.Samples;
using AwAUI.Text;
using AwAUI.UI;
using CounterSample;

// 用法：
//   Counter                                   打开 macOS 窗口（其他平台退回无窗口模式）；默认 GPU（Metal），没有 Metal 时软件
//   Counter --gpu | --software                选择窗口的渲染后端（--gpu 在不支持 Metal 时退回软件并提示）；无窗口模式总是软件
//   Counter --exit-after-ms 3000              窗口运行 3 秒后自动关闭（冒烟测试），打印帧耗时统计
//   Counter --frames 30                       窗口执行 30 帧后关闭
//   Counter --auto-click                      （窗口）每帧结束后点击一次「增加」，持续出帧（测量帧耗时用）
//   Counter --self-check [毫秒]                （窗口）启动后若干毫秒（默认 1500）自检：GPU 读回呈现的可绘制纹理，两种后端都截取
//                                             合成后的窗口内容，与软件后端渲染同一帧 DisplayList 的结果比较
//   Counter --simulate-device-loss N          （GPU 窗口）第 N 帧后模拟 GPU 设备丢失，验证回退到软件呈现
//   Counter --headless out.png [--click-count N] [--type 文本] [--scale 2]
//                                             无窗口渲染到 PNG；可先模拟点击「增加」N 次、在输入框中输入并提交
//   Counter --bench [--scale 2]               计数器的逐阶段帧耗时
//   Counter --stress 1000 [--scale 2]         1000 行列表的样式 / 布局 / 绘制耗时
//   --markup                                  使用 .awaui 写的同一个应用（Markup/，组件样式随组件首次挂载自动注册）
//   --devtools [端口]                          启用 DevTools：F12 切换拾取模式（点选元素后在控制台打印它命中的规则），
//                                             并在 ws://127.0.0.1:端口/devtools 上监听（默认端口 9230；0 表示系统分配）
//   --devtools-panel                          打开内嵌 DevTools 面板（第二个窗口，经进程内协议检查本窗口；关闭后 F12 重新打开）。
//                                             无窗口模式下另写一张「应用 | 面板」并排的 PNG（<输出>-devtools.png），面板选中 --highlight 指定的元素（默认 count）
//   --highlight 元素id                         （无窗口模式）用 DevTools 叠加层高亮该元素后再截图
//   --serve-ms N                              （无窗口模式 + --devtools）先服务 DevTools 连接 N 毫秒（期间照常出帧）再截图
//   --extra-css "选择器 { ... }"               追加一张样式表（实验用）
string? headlessOut = Arg("--headless");
int clicks = int.TryParse(Arg("--click-count"), out var c) ? c : 0;
float scale = float.TryParse(Arg("--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 2f;
int maxFrames = int.TryParse(Arg("--frames"), out var f) ? f : 0;
int exitAfterMs = int.TryParse(Arg("--exit-after-ms"), out var ms) ? ms : 0;
string? typeText = Arg("--type");
bool bench = args.Contains("--bench");
bool markup = args.Contains("--markup");
bool devTools = args.Contains("--devtools");
bool devToolsPanel = args.Contains("--devtools-panel");
int devToolsPort = int.TryParse(Arg("--devtools"), out var dp) ? dp : 9230;
string? highlightId = Arg("--highlight");
int serveMs = int.TryParse(Arg("--serve-ms"), out var sm) ? sm : 0;
int stress = int.TryParse(Arg("--stress"), out var st) ? st : 0;
bool software = args.Contains("--software");
bool autoClick = args.Contains("--auto-click");
bool selfCheck = args.Contains("--self-check");
int selfCheckMs = int.TryParse(Arg("--self-check"), out var scm) && scm > 0 ? scm : 1500;
int lossAfter = int.TryParse(Arg("--simulate-device-loss"), out var la) ? la : 0;

var textEngine = SampleSetup.CreateTextEngine(out bool cjk);
Strings.Current = cjk ? Strings.Chinese : Strings.English;
// .awaui 版本的组件样式写在各自的 <style> 里，由运行时注册：只加载全局的 app.css
var sheets = SampleSetup.LoadStyleSheets(markup ? new[] { "app.css" } : new[] { "app.css", "Counter.css" });
Node CreateApp() => markup ? new CounterSample.Markup.App() : new App();
if (Arg("--extra-css") is { } extraCss) sheets = sheets.Append(StyleSheet.Parse(extraCss, "--extra-css")).ToArray();
var options = new WindowOptions { Title = "AwAUI Counter", Size = new Size(720, 460) };

if (stress > 0)
{
    Benchmarks.Stress(stress, scale, textEngine);
    return;
}
if (bench)
{
    Benchmarks.Counter(scale, textEngine, sheets, options, gpu: !software);
    return;
}
if (headlessOut is not null || !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    RunHeadless(headlessOut ?? "counter.png");
    return;
}
RunWindow();

void RunHeadless(string path)
{
    var platform = new HeadlessPlatform { DefaultScale = scale };
    var renderBackend = new SoftwareRenderBackend();
    var host = AwApp.CreateHost(platform, options, renderBackend, textEngine, CreateApp(), sheets);
    var window = (HeadlessWindow)host.Window;
    if (devTools) SampleSetup.EnableDevTools(host, devToolsPort);
    else if (highlightId is not null) host.EnableDevTools();
    platform.RunFrame();
    Console.WriteLine("first frame: " + host.LastFrame);
    for (int i = 0; i < clicks; i++)
    {
        SampleSetup.ClickElement(window, host, "increment");
        platform.RunFrame();
    }
    if (clicks > 0) Console.WriteLine($"after {clicks} click(s): " + host.LastFrame);
    if (typeText is not null)
    {
        SampleSetup.ClickElement(window, host, "name");
        platform.RunFrame();
        window.SendText(typeText);
        platform.RunFrame();
        window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.Enter, KeyModifiers.None, false, 0, TimeSpan.Zero));
        window.SendKey(new RawKeyEvent(RawKeyEventType.Up, PhysicalKey.Enter, KeyModifiers.None, false, 0, TimeSpan.Zero));
        platform.RunFrame();
        Console.WriteLine("after typing: " + host.LastFrame);
    }
    if (highlightId is not null)
    {
        var target = host.Root.RootElement.FindById(highlightId) ?? throw new InvalidOperationException("没有找到 #" + highlightId);
        host.DevTools!.Highlight(target);
        platform.RunFrame();
    }
    if (devTools && serveMs > 0)
    {
        // DevTools 请求在 UI 线程（无窗口平台的调度器）上执行：帧循环同时泵送它们
        var serve = Stopwatch.StartNew();
        while (serve.ElapsedMilliseconds < serveMs)
        {
            platform.RunFrame();
            Thread.Sleep(5);
        }
    }
    // 让悬停等后续状态稳定下来
    for (int i = 0; i < 3; i++) platform.RunFrame();
    if (devToolsPanel) SampleSetup.SavePanelScreenshot(platform, host, renderBackend, textEngine, highlightId ?? "count",
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-devtools.png"));
    PngCodec.Save(window.Framebuffer!, path);
    var count = host.Root.RootElement.FindById("count");
    Console.WriteLine($"count text: \"{count?.TextContent}\"");
    Console.WriteLine($"Wrote {path} ({window.Framebuffer!.Width}x{window.Framebuffer.Height} px)");
}

void RunWindow()
{
    var platform = new MacPlatform();
    // 组合根：GPU 可用时用 GPU（设备丢失时自动回退软件），否则软件
    using var backend = MetalRenderer.Create(preferGpu: !software);
    if (args.Contains("--gpu") && backend is not MetalRenderBackend) Console.Error.WriteLine("Metal is not available; using the software backend.");
    backend.Degraded += m => Console.Error.WriteLine("[degraded] " + m);
    var metal = backend as MetalRenderBackend;
    if (selfCheck && metal is not null) metal.CapturePresentedFrames = true;
    var host = AwApp.CreateHost(platform, options, backend, textEngine, CreateApp(), sheets);
    var window = (MacWindow)host.Window;
    Console.WriteLine($"Backend: {backend.Name}, presentation: {window.PresentationMode}");
    if (devTools) SampleSetup.EnableDevTools(host, devToolsPort);
    if (devToolsPanel)
    {
        // 面板窗口与应用共用后端与文本引擎；面板只经进程内协议访问应用
        var panel = DevToolsPanel.Attach(host, platform, backend, textEngine);
        int panelFrames = 0;
        panel.Opened += p => p.Host!.FrameCompleted += h => { if (h.LastFrame.Rendered) panelFrames++; };
        panel.Open();
        Console.WriteLine("DevTools panel: open (F12 in the app window = pick an element; reopens the panel after closing it)");
        host.Closed += _ => Console.WriteLine($"DevTools panel: {panelFrames} rendered frame(s), selected node {panel.SelectedNodeId}");
    }
    int frames = 0;
    bool pauseClicks = false;
    var rendered = new List<HostFrameStatistics>();
    var gpuCpu = new List<double>();
    var waits = new List<double>();
    var total = Stopwatch.StartNew();
    host.FrameCompleted += h =>
    {
        frames++;
        if (frames <= 2 || frames % 60 == 0) Console.WriteLine(h.LastFrame);
        if (h.LastFrame.Rendered)
        {
            rendered.Add(h.LastFrame);
            if (metal is { IsSoftwareFallback: false, LastFrameStats: { } gs })
            {
                gpuCpu.Add((gs.RecordTime + gs.EncodeTime).TotalMilliseconds);
                waits.Add(metal.LastDrawableWait.TotalMilliseconds);
            }
        }
        if (lossAfter > 0 && frames == lossAfter && metal is not null)
        {
            Console.WriteLine($"Simulating GPU device loss after frame {frames}");
            metal.SimulateDeviceLoss();
        }
        if (maxFrames > 0 && frames >= maxFrames)
        {
            h.Window.Close();
            return;
        }
        // 连续出帧：经窗口事件接口注入一次点击（与平台真实输入走同一条路径），下一帧计数变化、重新光栅化
        if (autoClick && !pauseClicks) platform.Dispatcher.Post(() => SampleSetup.ClickElement(host, "increment"));
    };
    host.Closed += _ => platform.Exit();
    if (selfCheck)
    {
        new Thread(() =>
        {
            Thread.Sleep(selfCheckMs);
            platform.Dispatcher.Post(() => pauseClicks = true);
            Thread.Sleep(300); // 等最后一次点击的帧上屏
            platform.Dispatcher.Post(() =>
            {
                RunSelfCheck(host, metal);
                pauseClicks = false;
                if (autoClick) SampleSetup.ClickElement(host, "increment");
            });
        }) { IsBackground = true }.Start();
    }
    if (exitAfterMs > 0)
    {
        new Thread(() =>
        {
            Thread.Sleep(exitAfterMs);
            platform.Dispatcher.Post(() => host.Window.Close());
        }) { IsBackground = true }.Start();
    }
    platform.Run();
    Console.WriteLine($"Window closed after {total.Elapsed.TotalSeconds:F1} s, {frames} frame(s), backend {backend.Name}, presentation {window.PresentationMode}.");
    // 帧数偏少时用来区分「窗口被遮挡（按设计暂停）」与节拍问题
    Console.WriteLine($"  frame clock {window.FrameClock}: {window.FrameClockTicks} tick(s), {window.FramesDelivered} frame(s) delivered, refresh {window.RefreshInterval.TotalMilliseconds:F2} ms, occluded at exit: {window.IsOccluded}");
    PrintStats(rendered, gpuCpu, waits);
}

static void RunSelfCheck(UIHost host, MetalRenderBackend? metal)
{
    var window = (MacWindow)host.Window;
    var frame = host.PresentedFrame;
    if (metal is not null)
    {
        var r = FrameCheck.CompareWithSoftware(metal, frame, window.RenderScale, host.ClearColor);
        Console.WriteLine(r is { } d ? "[self-check] presented drawable vs software: " + d : $"[self-check] no presented frame captured ({metal.Name})");
    }
    var w = FrameCheck.CompareWindow(window, frame, window.RenderScale, host.ClearColor);
    Console.WriteLine(w is { } c ? $"[self-check] window capture ({window.PresentationMode}) vs software: {c}" : "[self-check] window capture unavailable");
}

static void PrintStats(List<HostFrameStatistics> rendered, List<double> gpuCpu, List<double> waits)
{
    // 跳过前 5 帧（JIT、字形与图集冷启动）
    var frames = rendered.Skip(Math.Min(5, Math.Max(0, rendered.Count - 5))).ToList();
    if (frames.Count == 0) return;
    static double Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }
    static double P95(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[(int)(a.Length * 0.95)];
    }
    Console.WriteLine(FormattableString.Invariant(
        $"  rendered frames: {frames.Count}; median total {Median(frames.Select(f => f.TotalMs)):F3} ms (p95 {P95(frames.Select(f => f.TotalMs)):F3}), raster+present {Median(frames.Select(f => f.RasterMs)):F3} ms (p95 {P95(frames.Select(f => f.RasterMs)):F3}), style+layout+paint {Median(frames.Select(f => f.Pipeline.StyleMs + f.Pipeline.LayoutMs + f.Pipeline.PaintMs)):F3} ms"));
    if (gpuCpu.Count > 5)
    {
        Console.WriteLine(FormattableString.Invariant($"  GPU path CPU (record + encode): median {Median(gpuCpu.Skip(5)):F3} ms (p95 {P95(gpuCpu.Skip(5)):F3})"));
        Console.WriteLine(FormattableString.Invariant($"  waiting in nextDrawable (part of raster+present): median {Median(waits.Skip(5)):F3} ms (p95 {P95(waits.Skip(5)):F3}, max {waits.Skip(5).DefaultIfEmpty().Max():F3})"));
    }
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static class SampleSetup
{
    /// <summary>
    /// 文本引擎：注册随附的 Noto Sans（OFL）并作为通用族 sans-serif / system-ui 的默认字体（平台还没有系统字体枚举）。
    /// 若本机有中日韩字体，按顺序取第一个注册为回退字体：macOS 的 Arial Unicode（TrueType 轮廓），没有时用系统自带的
    /// 冬青黑体（Hiragino Sans GB）或ヒラギノ角ゴシック W3（CFF 轮廓，.ttc）。
    /// </summary>
    public static TextEngine CreateTextEngine(out bool hasCjk)
    {
        var fonts = new FontCollection();
        var dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
        fonts.RegisterFile(Path.Combine(dir, "NotoSans-Regular.ttf"));
        foreach (var generic in new[] { "sans-serif", "system-ui", "ui-sans-serif", "serif", "monospace" })
            fonts.SetGenericFamily(generic, "Noto Sans");
        hasCjk = false;
        foreach (var candidate in new[]
                 {
                     "/System/Library/Fonts/Supplemental/Arial Unicode.ttf", "/Library/Fonts/Arial Unicode.ttf",
                     "/System/Library/Fonts/Hiragino Sans GB.ttc", "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                fonts.RegisterFile(candidate);
                hasCjk = true;
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[font] {candidate}: {ex.Message}");
            }
        }
        return new DefaultTextEngine(fonts);
    }

    /// <summary>
    /// 启用 DevTools：F12 进入拾取模式，点选的元素在控制台打印出它命中的规则（内嵌面板还没有，见 design/06）；
    /// WebSocket 上可用协议检查与实时修改（Tree / Css / Layout / Overlay 域）。
    /// </summary>
    public static void EnableDevTools(UIHost host, int port)
    {
        var dt = host.EnableDevTools(new UIDevToolsOptions { ListenWebSocket = true, Port = port, AppName = "Counter" });
        Console.WriteLine($"DevTools: {dt.WebSocketEndpoint} (F12 = inspect element)");
        dt.ElementPicked += e => Console.WriteLine("[devtools] picked\n" + dt.DescribeElement(e));
    }

    /// <summary>
    /// 无窗口模式的 <c>--devtools-panel</c>：打开面板，在面板里选中 #<paramref name="selectId"/>（与用户点击树中的行相同，经协议），
    /// 把「应用 | 面板」并排写成一张 PNG。之后关闭面板，应用的截图不含高亮。
    /// </summary>
    public static void SavePanelScreenshot(HeadlessPlatform platform, UIHost host, RenderBackend backend, TextEngine textEngine, string selectId, string path)
    {
        var panel = DevToolsPanel.Attach(host, platform, backend, textEngine);
        panel.Open();
        for (int i = 0; i < 6; i++) platform.RunFrame();
        var target = host.Root.RootElement.FindById(selectId) ?? throw new InvalidOperationException("没有找到 #" + selectId);
        // 点击面板树中对应的行（行带 data-node = 应用元素的 UniqueId）
        var row = FindByAttribute(panel.Host!.Root.RootElement, "data-node", target.UniqueId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (row is null) throw new InvalidOperationException("面板树中没有 #" + selectId + " 的行");
        var panelWindow = (HeadlessWindow)panel.Window!;
        var p = row.BoundingRect.Center;
        panelWindow.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        panelWindow.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        panelWindow.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        for (int i = 0; i < 8; i++) platform.RunFrame();
        var app = ((HeadlessWindow)host.Window).Framebuffer!;
        var side = panelWindow.Framebuffer!;
        const int gap = 24;
        using (var composite = new Bitmap(new PixelSize(app.Width + gap + side.Width, Math.Max(app.Height, side.Height)), app.Format))
        {
            composite.Clear(new Color(0.85f, 0.87f, 0.9f));
            for (int y = 0; y < app.Height; y++) app.GetRow(y).Slice(0, app.Width * 4).CopyTo(composite.GetRow(y));
            for (int y = 0; y < side.Height; y++) side.GetRow(y).Slice(0, side.Width * 4).CopyTo(composite.GetRow(y).Slice((app.Width + gap) * 4));
            PngCodec.Save(composite, path);
        }
        Console.WriteLine($"Wrote {path} (app | DevTools panel, selected #{selectId})");
        panel.Dispose();
        for (int i = 0; i < 3; i++) platform.RunFrame();
    }

    static Element? FindByAttribute(Element root, string name, string value)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.GetAttribute(name) == value) return e;
            foreach (var ch in e.Children) stack.Push(ch);
        }
        return null;
    }

    public static StyleSheet[] LoadStyleSheets(string[] names)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Styles");
        var list = new List<StyleSheet>();
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            var sheet = StyleSheet.Parse(File.ReadAllText(path), name);
            foreach (var d in sheet.Diagnostics) Console.Error.WriteLine($"[css] {name}: {d}");
            list.Add(sheet);
        }
        return list.ToArray();
    }

    public static Element? FindById(this Element root, string id)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Id == id) return e;
            foreach (var ch in e.Children) stack.Push(ch);
        }
        return null;
    }

    /// <summary>在元素中心模拟一次鼠标移动 + 左键按下 + 抬起（经平台窗口事件 → 命中测试 → 事件派发）。</summary>
    public static void ClickElement(HeadlessWindow window, UIHost host, string id)
    {
        var e = host.Root.RootElement.FindById(id) ?? throw new InvalidOperationException("没有找到 #" + id);
        var p = e.BoundingRect.Center;
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
    }

    /// <summary>真实窗口上的同一操作：经宿主的 <see cref="IWindowEvents"/>（平台把原生事件交给宿主的同一个入口）注入。</summary>
    public static void ClickElement(UIHost host, string id)
    {
        var e = host.Root.RootElement.FindById(id);
        if (e is null) return;
        var p = e.BoundingRect.Center;
        IWindowEvents events = host;
        events.OnPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        events.OnPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        events.OnPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
    }
}
