using AwAUI.Css;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.UI;
using CounterSample;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 无窗口平台上的「应用 + 面板」：应用窗口运行 Counter 示例（或自定义根节点），面板经进程内协议附加到它。
/// 两个窗口共用一个平台（一个 UI 线程、一个调度器），<see cref="Frames"/> 推进两者。
/// </summary>
internal sealed class Harness : IDisposable
{
    static Harness()
    {
        // 只注册拉丁字体：界面文字用英文
        Strings.Current = Strings.English;
    }

    public readonly HeadlessPlatform Platform;
    public readonly SoftwareRenderBackend Backend = new();
    public readonly TextEngine TextEngine = CreateTextEngine();
    public readonly UIHost App;
    public readonly HeadlessWindow AppWindow;
    public readonly DevToolsPanel? Panel;

    public Harness(Node? root = null, StyleSheet[]? sheets = null, double scale = 1, bool attach = true, DevToolsPanelOptions? options = null,
        Size? appSize = null)
    {
        Platform = new HeadlessPlatform { DefaultScale = scale };
        App = AwApp.CreateHost(Platform, new WindowOptions { Title = "Counter", Size = appSize ?? new Size(720, 460) }, Backend, TextEngine,
            root ?? new CounterSample.App(), sheets ?? CounterSheets());
        AppWindow = (HeadlessWindow)App.Window;
        if (attach) Panel = DevToolsPanel.Attach(App, Platform, Backend, TextEngine, options);
        Frames(2);
    }

    public static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    public static TextEngine CreateTextEngine()
    {
        var fonts = new FontCollection();
        fonts.RegisterFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSans-Regular.ttf"));
        foreach (var generic in new[] { "sans-serif", "system-ui", "ui-sans-serif", "serif", "monospace" })
            fonts.SetGenericFamily(generic, "Noto Sans");
        return new DefaultTextEngine(fonts);
    }

    public static StyleSheet[] CounterSheets() => new[] { Sheet("app.css"), Sheet("Counter.css") };

    static StyleSheet Sheet(string name) => StyleSheet.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Styles", name)), name);

    public DevToolsPanel P => Panel ?? throw new InvalidOperationException("No panel attached.");

    public InspectorModel Model => P.Model ?? throw new InvalidOperationException("The panel is not open.");

    public HeadlessWindow PanelWindow => (HeadlessWindow)(P.Window ?? throw new InvalidOperationException("The panel is not open."));

    public Element PanelRoot => P.Host!.Root.RootElement;

    /// <summary>推进 <paramref name="n"/> 帧（每帧之前与之后都清空调度器：协议回调与面板状态在帧之间生效）。</summary>
    public void Frames(int n = 1)
    {
        for (int i = 0; i < n; i++) Platform.RunFrame();
    }

    /// <summary>足够让一次交互的请求、响应、重建与两边的重绘都完成。</summary>
    public void Settle() => Frames(8);

    public void Open()
    {
        P.Open();
        Settle();
        Assert.True(Model.Loaded, "Panel did not load the document: " + Model.Status);
    }

    // ------------------------------------------------------------ 元素查找

    public static IEnumerable<Element> Descendants(Element root)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
    }

    public Element AppById(string id) => Descendants(App.Root.RootElement).Single(e => e.Id == id);

    public Element? PanelById(string id) => Descendants(PanelRoot).FirstOrDefault(e => e.Id == id);

    /// <summary>面板树视图中节点 <paramref name="nodeId"/> 的行（只有可见行才有元素）。</summary>
    public Element? PanelRow(long nodeId)
    {
        string key = nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Descendants(PanelRoot).FirstOrDefault(e => e.GetAttribute("data-node") == key);
    }

    public List<Element> PanelRows() => Descendants(PanelRoot).Where(e => e.GetAttribute("data-node") is not null).ToList();

    // ------------------------------------------------------------ 输入

    static void Send(HeadlessWindow w, RawPointerEventType type, Point p, PointerButtons changed, PointerButtons buttons, float wheelY = 0) =>
        w.SendPointer(new RawPointerEvent(type, PointerType.Mouse, 1, p, changed, buttons, KeyModifiers.None, TimeSpan.Zero, 0.5f, 0, wheelY));

    public static void Click(HeadlessWindow w, Point p)
    {
        Send(w, RawPointerEventType.Move, p, PointerButtons.None, PointerButtons.None);
        Send(w, RawPointerEventType.Down, p, PointerButtons.Left, PointerButtons.Left);
        Send(w, RawPointerEventType.Up, p, PointerButtons.Left, PointerButtons.None);
    }

    public void ClickPanel(Element e)
    {
        Click(PanelWindow, e.BoundingRect.Center);
        Settle();
    }

    public void ClickApp(Element e)
    {
        Click(AppWindow, e.BoundingRect.Center);
        Settle();
    }

    public void MovePanel(Point p)
    {
        Send(PanelWindow, RawPointerEventType.Move, p, PointerButtons.None, PointerButtons.None);
        Settle();
    }

    public void LeavePanel()
    {
        Send(PanelWindow, RawPointerEventType.Leave, new Point(-1, -1), PointerButtons.None, PointerButtons.None);
        Settle();
    }

    public void WheelPanel(Point p, float dy)
    {
        Send(PanelWindow, RawPointerEventType.Wheel, p, PointerButtons.None, PointerButtons.None, dy);
        Frames();
        WheelFrame = P.Host!.LastFrame;
        Settle();
    }

    /// <summary>最近一次 <see cref="WheelPanel"/> 之后面板的第一帧。</summary>
    public UI.HostFrameStatistics WheelFrame { get; private set; }

    public static void Key(HeadlessWindow w, PhysicalKey key, KeyModifiers mods = KeyModifiers.None)
    {
        w.SendKey(new RawKeyEvent(RawKeyEventType.Down, key, mods, false, 0, TimeSpan.Zero));
        w.SendKey(new RawKeyEvent(RawKeyEventType.Up, key, mods, false, 0, TimeSpan.Zero));
    }

    // ------------------------------------------------------------ 像素

    public Color AppPixel(Point p) => AppWindow.Framebuffer!.GetPixel((int)(p.X * AppWindow.RenderScale), (int)(p.Y * AppWindow.RenderScale));

    /// <summary>
    /// 应用帧缓冲中 <paramref name="r"/> 区域里满足条件的像素数。先隐藏 DevTools 高亮（面板总是高亮选中的元素，
    /// 它的半透明覆盖层会改变颜色），量完再恢复。
    /// </summary>
    public int CountAppPixels(Rect r, Func<Color, bool> predicate)
    {
        var dt = App.DevTools;
        var highlighted = dt?.HighlightedElement;
        if (highlighted is not null)
        {
            dt!.Highlight(null);
            Frames(2);
        }
        try
        {
            return CountPixels(r, predicate);
        }
        finally
        {
            if (highlighted is not null)
            {
                dt!.Highlight(highlighted);
                Frames(2);
            }
        }
    }

    int CountPixels(Rect r, Func<Color, bool> predicate)
    {
        var fb = AppWindow.Framebuffer!;
        double s = AppWindow.RenderScale;
        int x0 = Math.Max(0, (int)(r.X * s)), y0 = Math.Max(0, (int)(r.Y * s));
        int x1 = Math.Min(fb.Width, (int)Math.Ceiling(r.Right * s)), y1 = Math.Min(fb.Height, (int)Math.Ceiling(r.Bottom * s));
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (predicate(fb.GetPixel(x, y))) n++;
        return n;
    }

    public static bool IsRed(Color c) => c.R > 0.75f && c.G < 0.3f && c.B < 0.3f;

    public void Dispose()
    {
        Panel?.Dispose();
        App.Dispose();
        Backend.Dispose();
    }
}
