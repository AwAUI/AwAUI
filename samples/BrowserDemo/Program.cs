using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Threading.Tasks;
using AwAUI;
using AwAUI.Browser;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Render;
using RenderDemo;

namespace BrowserDemo;

/// <summary>
/// 入口。main.js 先用 setModuleImports 注册页面回调（<see cref="DemoPage"/>），再调用 runMain；
/// Main 完成初始化后返回，之后一切由浏览器事件驱动（运行时保持存活）。
/// </summary>
public static class Program
{
    internal static DemoApp? App;

    public static async Task<int> Main(string[] args)
    {
        var platform = await BrowserPlatform.CreateAsync();
        var window = platform.AttachCanvas("view");
        var backend = new SoftwareRenderBackend();
        backend.Degraded += msg => DemoPage.Log("[degraded] " + msg);
        var target = backend.CreateRenderTarget(window.Surface);
        App = new DemoApp(platform, window, target);
        window.Events = App;
        window.RequestFrame();
        DemoPage.Log($"AwAUI.Browser ready: {window.ClientSize} @{window.RenderScale.ToString(CultureInfo.InvariantCulture)}x → {window.PixelSize} px");
        return 0;
    }
}

/// <summary>演示应用：RenderDemo 场景 + 拖动圆片 + 触摸标记 + 文本框（输入法回显在页面面板中）。</summary>
internal sealed class DemoApp : IWindowEvents
{
    readonly BrowserPlatform _platform;
    readonly BrowserWindow _window;
    readonly RenderTarget _target;
    readonly DemoScene _scene = new();
    readonly Dictionary<long, Point> _touches = new();
    readonly StringBuilder _text = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    // 拖动圆片
    Point _chip = new(-1, -1);
    long? _dragPointer;
    Point _dragOffset;
    bool _chipHover;

    bool _fieldFocused;
    Rect _field;
    Rect _caret;
    readonly FieldClient _client;

    // 统计
    int _statFrames;
    double _statRender, _statRecord, _statPresent;
    double _statStart;

    public DemoApp(BrowserPlatform platform, BrowserWindow window, RenderTarget target)
    {
        _platform = platform;
        _window = window;
        _target = target;
        window.TextInput.CompositionChanged += OnComposition;
        _client = new FieldClient(this);
        platform.Clipboard.CopyHandler = (_, cut) =>
        {
            if (!_fieldFocused || _text.Length == 0) return null;
            string s = _text.ToString();
            if (cut) { _text.Clear(); UpdateEcho(); }
            return s;
        };
    }

    /// <summary>为 false 时只在输入时出帧（用于验证空闲时不占 CPU）。</summary>
    public bool Animate { get; set; } = true;

    public BrowserWindow Window => _window;
    public BrowserPlatform Platform => _platform;
    public long Frames { get; private set; }
    public double LastRecordMs { get; private set; }
    public double LastRenderMs { get; private set; }
    public string CommittedText => _text.ToString();
    public string? Hovered => _chipHover ? "chip" : _scene.Hovered;
    public int Clicks => _scene.Clicks;
    public Point Chip => _chip;
    public int ActiveTouches => _touches.Count;
    public bool FieldFocused => _fieldFocused;
    public string LastKey { get; private set; } = "";
    public string LastKeyValue { get; private set; } = "";
    public int FocusChanges { get; private set; }
    public int Resizes { get; private set; }
    public int WheelEvents { get; private set; }
    public double WheelY { get; private set; }

    // ---------------------------------------------------------------- 帧

    public void OnFrame(TimeSpan timestamp)
    {
        if (_window.IsClosed) return;
        RenderOnce(timestamp.TotalSeconds);
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _statStart >= 1000 && _statFrames > 0)
        {
            double secs = (now - _statStart) / 1000;
            DemoPage.SetStats(string.Format(CultureInfo.InvariantCulture,
                "{0}×{1} px @{2}x — {3:F0} fps · C# {4:F1} ms/frame (record {5:F2}, raster {6:F1}) · putImageData {7:F2} ms · rAF {8}/{9}",
                _target.PixelSize.Width, _target.PixelSize.Height, _window.RenderScale, _statFrames / secs,
                _statRender / _statFrames, _statRecord / _statFrames, (_statRender - _statPresent - _statRecord) / _statFrames,
                _statPresent / _statFrames, _window.FramesDelivered, _window.FrameCallbacks));
            _statFrames = 0; _statRender = _statRecord = _statPresent = 0; _statStart = now;
        }
        if (Animate) _window.RequestFrame();
    }

    /// <summary>录制 + 光栅化 + 呈现一帧，返回总耗时（毫秒）。</summary>
    public double RenderOnce(double timeSeconds)
    {
        long t0 = Stopwatch.GetTimestamp();
        _scene.Time = timeSeconds;
        var list = Build(_window.ClientSize);
        long t1 = Stopwatch.GetTimestamp();
        _target.Render(list, new RenderOptions(Color.Transparent)); // 提交时 BrowserWindow 立即 putImageData
        long t2 = Stopwatch.GetTimestamp();
        LastRecordMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        LastRenderMs = (t2 - t0) * 1000.0 / Stopwatch.Frequency;
        Frames++;
        _statFrames++;
        _statRender += LastRenderMs;
        _statRecord += LastRecordMs;
        _statPresent += _window.LastPresentMilliseconds;
        return LastRenderMs;
    }

    DisplayList Build(Size size)
    {
        var dc = new DrawingContext();
        dc.DrawDisplayList(_scene.Build(size));

        // 文本框：没有字体渲染（Web 上的字体加载另行实现），字符画成小块，组合文本画成带下划线的蓝块
        float fw = MathF.Min(420, size.Width - 40);
        _field = new Rect((size.Width - fw) / 2, size.Height - 56, fw, 40);
        var field = new RoundRect(_field, 8);
        dc.FillRoundRect(field, Color.Parse(_fieldFocused ? "#ffffff" : "#f8fafc"));
        dc.StrokeRoundRect(field, Color.Parse(_fieldFocused ? "#4f46e5" : "#cbd5e1"), new Stroke(_fieldFocused ? 2 : 1));
        float x = _field.X + 12, y = _field.Y + 12, h = 16;
        int shown = Math.Min(_text.Length, 48);
        for (int i = _text.Length - shown; i < _text.Length; i++)
        {
            float w = _text[i] == ' ' ? 6 : char.IsHighSurrogate(_text[i]) ? 0 : _text[i] > 0x2E80 ? 14 : 8;
            if (w > 0 && _text[i] != ' ') dc.FillRoundRect(new RoundRect(new Rect(x, y, w - 2, h), 2), Color.Parse("#334155"));
            x += w;
        }
        var ime = _window.TextInput;
        if (ime.IsComposing)
        {
            string comp = ime.CompositionText;
            float cx0 = x, caretX = x;
            for (int i = 0; i < comp.Length; i++)
            {
                if (i == ime.CompositionSelectionStart) caretX = x;
                float w = char.IsHighSurrogate(comp[i]) ? 0 : comp[i] > 0x2E80 ? 14 : 8;
                if (w > 0) dc.FillRoundRect(new RoundRect(new Rect(x, y, w - 2, h), 2), Color.Parse("#6366f1"));
                x += w;
            }
            if (ime.CompositionSelectionStart >= comp.Length) caretX = x;
            dc.DrawLine(new Point(cx0, y + h + 3), new Point(x, y + h + 3), Color.Parse("#4f46e5"), new Stroke(2));
            x = caretX;
        }
        if (_fieldFocused)
        {
            dc.FillRect(new Rect(x, y - 2, 2, h + 4), Color.Parse("#4f46e5"));
            _caret = new Rect(x, y - 2, 2, h + 4);
            _window.TextInput.SetCaretRect(_caret);
        }

        // 可拖动的圆片
        if (_chip.X < 0) _chip = new Point(size.Width - 60, size.Height - 100);
        bool dragging = _dragPointer.HasValue;
        dc.DrawBoxShadow(new RoundRect(new Rect(_chip.X - 22, _chip.Y - 22, 44, 44), 22),
            new BoxShadow(0, dragging ? 10 : 4, dragging ? 20 : 10, 0, Color.Parse("#0f172a40")));
        dc.FillCircle(_chip, 22, Color.Parse(dragging ? "#f97316" : _chipHover ? "#fb923c" : "#fdba74"));
        dc.FillCircle(_chip, 6, Color.Parse("#ffffff"));

        // 触摸点
        foreach (var p in _touches.Values)
            dc.StrokeRoundRect(new RoundRect(new Rect(p.X - 26, p.Y - 26, 52, 52), 26), Color.Parse("#10b981"), new Stroke(3));
        return dc.Finish();
    }

    // ---------------------------------------------------------------- 输入

    public void OnPointer(in RawPointerEvent e)
    {
        var p = e.Position;
        if (e.Type == RawPointerEventType.Wheel)
        {
            WheelEvents++;
            WheelY += e.WheelDeltaY;
            DemoPage.Log(FormattableString.Invariant($"wheel dx={e.WheelDeltaX:F1} dy={e.WheelDeltaY:F1} precise={e.PreciseWheel}"));
            return;
        }
        if (e.Device == PointerType.Touch)
        {
            if (e.Type is RawPointerEventType.Down or RawPointerEventType.Move && (e.Buttons != PointerButtons.None || e.Type == RawPointerEventType.Down))
                _touches[e.PointerId] = p;
            else if (e.Type is RawPointerEventType.Up or RawPointerEventType.Cancel or RawPointerEventType.Leave)
                _touches.Remove(e.PointerId);
        }

        // 拖动圆片（按指针 id 跟踪，多点触摸时其他手指不受影响）
        bool overChip = Distance(p, _chip) <= 22;
        switch (e.Type)
        {
            case RawPointerEventType.Down when _dragPointer is null && overChip && e.ChangedButton == PointerButtons.Left:
                _dragPointer = e.PointerId;
                _dragOffset = new Point(_chip.X - p.X, _chip.Y - p.Y);
                break;
            case RawPointerEventType.Move when _dragPointer == e.PointerId:
                _chip = new Point(p.X + _dragOffset.X, p.Y + _dragOffset.Y);
                break;
            case RawPointerEventType.Up or RawPointerEventType.Cancel when _dragPointer == e.PointerId:
                _dragPointer = null;
                break;
        }
        _chipHover = overChip && e.Type != RawPointerEventType.Leave;

        // RenderDemo 场景自己的悬停 / 点击（与 samples/RenderDemo 相同）
        if (_dragPointer is null)
        {
            switch (e.Type)
            {
                case RawPointerEventType.Leave: _scene.Pointer = null; break;
                case RawPointerEventType.Down: _scene.Pressed = true; _scene.Pointer = p; break;
                case RawPointerEventType.Up:
                    if (_scene.Hovered == "primary") _scene.Clicks++;
                    _scene.Pressed = false;
                    break;
                default: _scene.Pointer = p; break;
            }
            _scene.UpdateHover();
        }

        if (e.Type == RawPointerEventType.Down)
        {
            bool inField = _field.Contains(p);
            if (inField != _fieldFocused)
            {
                _fieldFocused = inField;
                // 文本框作为文本输入客户端：输入法组合、提交、粘贴 / 复制都经 ITextInputClient 到达
                _window.TextInput.SetClient(inField ? _client : null);
                if (inField) _window.TextInput.Show(); else _window.TextInput.Hide();
            }
            DemoPage.Log(FormattableString.Invariant($"{e.Type} {e.Device}#{e.PointerId} ({p.X:F1},{p.Y:F1}) {e.ChangedButton} buttons={e.Buttons} p={e.Pressure:F2}"));
        }
        else if (e.Type is RawPointerEventType.Up or RawPointerEventType.Cancel)
            DemoPage.Log(FormattableString.Invariant($"{e.Type} {e.Device}#{e.PointerId} ({p.X:F1},{p.Y:F1}) {e.ChangedButton}"));

        _window.SetCursor(_dragPointer.HasValue || _chipHover || _scene.Hovered != null ? CursorShape.Hand
            : _field.Contains(p) ? CursorShape.IBeam : CursorShape.Arrow);
        _window.RequestFrame();
    }

    public void OnKey(in RawKeyEvent e)
    {
        LastKey = e.Key.ToString();
        LastKeyValue = e.LogicalKey ?? "";
        DemoPage.Log($"key {e.Type} {e.Key} key='{LastKeyValue}' mods={e.Modifiers}{(e.IsRepeat ? " repeat" : "")}");
        if (e.Type != RawKeyEventType.Down || !_fieldFocused) return;
        if (e.Key == PhysicalKey.Backspace && _text.Length > 0)
        {
            int n = _text.Length >= 2 && char.IsLowSurrogate(_text[_text.Length - 1]) ? 2 : 1;
            _text.Remove(_text.Length - n, n);
            UpdateEcho();
        }
        else if (e.Key == PhysicalKey.Escape)
        {
            _text.Clear();
            UpdateEcho();
        }
        _window.RequestFrame();
    }

    public void OnTextInput(string text)
    {
        DemoPage.Log($"text \"{text}\"");
        if (!_fieldFocused) return;
        if (_text.Length < 10_000) _text.Append(text);
        UpdateEcho();
        _window.RequestFrame();
    }

    void OnComposition(TextCompositionEvent e)
    {
        DemoPage.Log($"composition {e.Kind} \"{e.Text}\" [{e.SelectionStart},{e.SelectionEnd})");
        UpdateEcho();
        _window.RequestFrame();
    }

    void UpdateEcho()
    {
        var ime = _window.TextInput;
        DemoPage.SetEcho(_text.ToString(), ime.CompositionText, ime.CompositionSelectionStart, ime.CompositionSelectionEnd);
    }

    public void OnFocusChanged(bool focused)
    {
        FocusChanges++;
        DemoPage.Log(focused ? "focus" : "blur");
        if (!focused && _fieldFocused)
        {
            _fieldFocused = false;
            _window.TextInput.SetClient(null);
            _window.RequestFrame();
        }
    }

    public void OnResized(Size clientSize, double scale)
    {
        Resizes++;
        _chip = new Point(Math.Min(_chip.X, clientSize.Width - 22), Math.Min(_chip.Y, clientSize.Height - 22));
        DemoPage.Log(FormattableString.Invariant($"resize {clientSize} @{scale}x → {_window.PixelSize} px"));
    }

    public bool OnCloseRequested() => true;
    public void OnClosed() { }

    /// <summary>文本框的文本输入客户端：只在末尾追加（演示用），文档 = 已提交文本 + 预编辑文本。</summary>
    sealed class FieldClient : ITextInputClient
    {
        readonly DemoApp _app;

        public FieldClient(DemoApp app) => _app = app;

        string Composition => _app._window.TextInput.CompositionText;

        public TextInputOptions Options => new(TextInputContentType.Text, multiline: true);

        public int Length => _app._text.Length + Composition.Length;

        public TextInputRange Selection
        {
            get
            {
                var ime = _app._window.TextInput;
                int n = _app._text.Length;
                return ime.IsComposing ? new TextInputRange(n + ime.CompositionSelectionStart, n + ime.CompositionSelectionEnd) : TextInputRange.Caret(n);
            }
            set { } // 插入点总在末尾
        }

        TextInputRange? ITextInputClient.Composition =>
            _app._window.TextInput.IsComposing ? new TextInputRange(_app._text.Length, _app._text.Length + Composition.Length) : null;

        public string GetText(TextInputRange range)
        {
            var doc = _app._text + Composition;
            var r = range.Clamp(doc.Length);
            return doc.Substring(r.Start, r.Length);
        }

        public Rect GetCaretRect() => _app._caret;

        public Rect GetTextBounds(TextInputRange range) => _app._caret;

        public void SetComposition(string text, TextInputRange selection) => _app._window.RequestFrame();

        public void Commit(string text) => _app.OnTextInput(text);

        public void ReplaceRange(TextInputRange range, string text)
        {
            var r = range.Clamp(_app._text.Length);
            if (r.Length > 0) _app._text.Remove(r.Start, r.Length);
            if (_app._text.Length + text.Length <= 10_000) _app._text.Insert(r.Start, text);
            _app.UpdateEcho();
            _app._window.RequestFrame();
        }
    }

    static float Distance(Point a, Point b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>页面回调（main.js 用 setModuleImports("BrowserDemo", …) 提供）。</summary>
internal static partial class DemoPage
{
    [JSImport("log", "BrowserDemo")]
    internal static partial void Log(string line);

    [JSImport("setStats", "BrowserDemo")]
    internal static partial void SetStats(string text);

    [JSImport("setEcho", "BrowserDemo")]
    internal static partial void SetEcho(string committed, string composition, int selectionStart, int selectionEnd);
}

/// <summary>供冒烟测试与测量脚本调用的出口（tests/AwAUI.Browser.Tests/node/）。</summary>
internal static partial class DemoProbe
{
    /// <summary>当前状态（JSON）。</summary>
    [JSExport]
    internal static string State()
    {
        var app = Program.App;
        var w = app?.Window;
        if (app is null || w is null) return "null";
        var sb = new StringBuilder(512);
        sb.Append('{');
        Num(sb, "frames", app.Frames); Num(sb, "framesDelivered", w.FramesDelivered); Num(sb, "frameCallbacks", w.FrameCallbacks);
        Bool(sb, "callbackPending", w.IsFrameCallbackPending); Bool(sb, "visible", w.IsVisible); Bool(sb, "focused", w.IsFocused);
        Num(sb, "clientWidth", w.ClientSize.Width); Num(sb, "clientHeight", w.ClientSize.Height); Num(sb, "scale", w.RenderScale);
        Num(sb, "pixelWidth", w.PixelSize.Width); Num(sb, "pixelHeight", w.PixelSize.Height);
        Str(sb, "hovered", app.Hovered); Num(sb, "clicks", app.Clicks); Num(sb, "chipX", app.Chip.X); Num(sb, "chipY", app.Chip.Y);
        Num(sb, "touches", app.ActiveTouches); Bool(sb, "fieldFocused", app.FieldFocused); Str(sb, "text", app.CommittedText);
        Bool(sb, "composing", w.TextInput.IsComposing); Str(sb, "composition", w.TextInput.CompositionText);
        Num(sb, "compositionCaret", w.TextInput.CompositionSelectionStart);
        Str(sb, "lastKey", app.LastKey); Str(sb, "lastKeyValue", app.LastKeyValue); Num(sb, "focusChanges", app.FocusChanges);
        Num(sb, "resizes", app.Resizes); Num(sb, "wheelEvents", app.WheelEvents); Num(sb, "wheelY", app.WheelY);
        Num(sb, "lastRecordMs", app.LastRecordMs); Num(sb, "lastRenderMs", app.LastRenderMs);
        Num(sb, "lastPresentMs", w.LastPresentMilliseconds); Num(sb, "presents", w.PresentCount);
        sb.Length--; // 末尾逗号
        sb.Append('}');
        return sb.ToString();
    }

    [JSExport]
    internal static void SetAnimating(bool animate)
    {
        if (Program.App is { } app)
        {
            app.Animate = animate;
            if (animate) app.Window.RequestFrame();
        }
    }

    /// <summary>
    /// 同步渲染 <paramref name="frames"/> 帧（不经 rAF）并返回各阶段平均耗时（JSON，毫秒）：
    /// record = 录制 DisplayList，raster = 光栅化，present = putImageData（含 JS 边界），total = 三者之和。
    /// </summary>
    [JSExport]
    internal static string Benchmark(int warmup, int frames)
    {
        var app = Program.App;
        var w = app?.Window;
        if (app is null || w is null || frames <= 0) return "null";
        for (int i = 0; i < warmup; i++) app.RenderOnce(0.25 + i / 60.0);
        double record = 0, total = 0, present = 0, min = double.MaxValue;
        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double ms = app.RenderOnce(0.25 + i / 60.0);
            total += ms; record += app.LastRecordMs; present += w.LastPresentMilliseconds;
            samples[i] = ms;
            if (ms < min) min = ms;
        }
        Array.Sort(samples);
        var sb = new StringBuilder("{");
        Num(sb, "pixelWidth", w.PixelSize.Width); Num(sb, "pixelHeight", w.PixelSize.Height); Num(sb, "frames", frames);
        Num(sb, "record", record / frames); Num(sb, "raster", (total - record - present) / frames); Num(sb, "present", present / frames);
        Num(sb, "total", total / frames); Num(sb, "median", samples[frames / 2]); Num(sb, "min", min);
        sb.Length--;
        sb.Append('}');
        return sb.ToString();
    }

    static readonly List<BrowserWindow> TestWindows = new();

    /// <summary>
    /// 额外窗口的自检：<paramref name="canvasId"/> 为空时经 <see cref="BrowserPlatform.CreateWindow"/> 新建 canvas，
    /// 否则附加页面中已有的 canvas（可选透明）。整块填充给定颜色并呈现，返回 canvas id。
    /// </summary>
    [JSExport]
    internal static string TestWindow(string? canvasId, bool transparent, int r, int g, int b, double a)
    {
        var platform = Program.App!.Platform;
        var window = string.IsNullOrEmpty(canvasId)
            ? (BrowserWindow)platform.CreateWindow(new WindowOptions { Title = "AwAUI BrowserDemo", Size = new Size(64, 48) })
            : platform.AttachCanvas(canvasId!, new BrowserWindowOptions { Transparent = transparent, SetsDocumentTitle = false });
        TestWindows.Add(window);
        using var backend = new SoftwareRenderBackend();
        using var target = backend.CreateRenderTarget(window.Surface);
        var dc = new DrawingContext();
        dc.FillRect(new Rect(0, 0, window.ClientSize.Width, window.ClientSize.Height), new Color(r / 255f, g / 255f, b / 255f, (float)a));
        target.Render(dc.Finish(), new RenderOptions(Color.Transparent));
        return window.CanvasId;
    }

    /// <summary>关闭 <see cref="TestWindow"/> 创建的窗口，返回仍附加着的窗口数。</summary>
    [JSExport]
    internal static int CloseTestWindows()
    {
        foreach (var w in TestWindows) w.Close();
        TestWindows.Clear();
        return Program.App!.Platform.Windows.Count;
    }

    /// <summary>经异步剪贴板 API 写入再读回；不可用时返回 "unavailable:写入结果"。</summary>
    [JSExport]
    internal static async Task<string> ClipboardRoundTrip(string text)
    {
        var clipboard = Program.App!.Platform.GetService<BrowserClipboard>()!;
        bool written = await clipboard.SetTextAsync(text);
        var read = await clipboard.GetTextAsync();
        return read ?? "unavailable:" + (written ? "written" : "write-failed");
    }

    static void Num(StringBuilder sb, string name, double v) =>
        sb.Append('"').Append(name).Append("\":").Append(double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "null").Append(',');

    static void Bool(StringBuilder sb, string name, bool v) => sb.Append('"').Append(name).Append("\":").Append(v ? "true" : "false").Append(',');

    static void Str(StringBuilder sb, string name, string? v)
    {
        sb.Append('"').Append(name).Append("\":");
        if (v is null) { sb.Append("null,"); return; }
        sb.Append('"');
        foreach (char c in v)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append("\",");
    }
}
