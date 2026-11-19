using System.Diagnostics;
using System.Text.RegularExpressions;
using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

/// <summary>
/// 页面脚本可以合成任意 DOM 事件（NaN、±∞、巨大数值、任意字符串），翻译层必须对所有输入都不抛异常、输出有限值、耗时线性。
/// </summary>
public class FuzzTests
{
    static readonly double[] Specials =
    {
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, double.MinValue, double.Epsilon,
        -double.Epsilon, 0, -0.0, 1e-300, 1e300, -1e300, 1, -1, 0.5, 1e7, -1e7, 16384, 1e12,
    };

    static double D(Random r) => r.Next(3) == 0 ? Specials[r.Next(Specials.Length)] : (r.NextDouble() - 0.5) * Math.Pow(10, r.Next(-5, 15));
    static int I(Random r) => r.Next(4) switch { 0 => int.MinValue + r.Next(3), 1 => int.MaxValue - r.Next(3), 2 => r.Next(-3, 40), _ => r.Next() };

    static string S(Random r)
    {
        int len = r.Next(8) == 0 ? r.Next(0, 5000) : r.Next(0, 24);
        var c = new char[len];
        for (int i = 0; i < len; i++) c[i] = (char)(r.Next(4) == 0 ? r.Next(0xD800, 0xE000) : r.Next(0, 0x10000));
        return new string(c);
    }

    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    [Fact]
    public void PointerAndWheel()
    {
        var r = new Random(1234);
        for (int i = 0; i < 200_000; i++)
        {
            var client = new Size((float)Math.Abs(D(r) % 5000), (float)Math.Abs(D(r) % 5000));
            var s = new DomPointerSample(r.Next(-1, 8), I(r), I(r), D(r), D(r), D(r), D(r), I(r), I(r), D(r), I(r), D(r));
            if (PointerTranslator.TryTranslate(s, client, out var e))
            {
                Assert.True(Finite(e.Position.X) && Finite(e.Position.Y));
                Assert.True(Math.Abs(e.Position.X) <= DomProtocol.MaxCoordinate && Math.Abs(e.Position.Y) <= DomProtocol.MaxCoordinate);
                Assert.InRange(e.Pressure, 0f, 1f);
                Assert.True(e.Timestamp >= TimeSpan.Zero);
                Assert.True(e.Type != RawPointerEventType.Up || (e.Buttons & e.ChangedButton) == 0);
            }
            var w = PointerTranslator.TranslateWheel(new DomWheelSample(D(r), D(r), D(r), D(r), D(r), D(r), I(r), D(r), I(r), I(r), D(r)), client);
            Assert.True(Finite(w.WheelDeltaX) && Finite(w.WheelDeltaY) && Finite(w.Position.X) && Finite(w.Position.Y));
            Assert.True(Math.Abs(w.WheelDeltaY) <= DomProtocol.MaxCoordinate);
        }
    }

    [Fact]
    public void Metrics()
    {
        var r = new Random(99);
        for (int i = 0; i < 200_000; i++)
        {
            var m = CanvasMetrics.Compute(D(r), D(r), D(r), D(r), D(r));
            Assert.True(Finite(m.ClientSize.Width) && Finite(m.ClientSize.Height) && m.ClientSize.Width >= 0 && m.ClientSize.Height >= 0);
            Assert.InRange(m.Scale, 1e-9, CanvasMetrics.MaxScale);
            Assert.True(m.PixelSize.Width >= 0 && m.PixelSize.Height >= 0);
            Assert.True(m.PixelSize.Width <= CanvasMetrics.MaxPixelDimension && m.PixelSize.Height <= CanvasMetrics.MaxPixelDimension);
            Assert.True((long)m.PixelSize.Width * m.PixelSize.Height <= CanvasMetrics.MaxPixelArea);
            Assert.Equal(m.ClientSize.IsEmpty, m.PixelSize.IsEmpty);
        }
    }

    [Fact]
    public void KeysTextAndComposition()
    {
        var r = new Random(7);
        var sink = new PairingSink();
        var t = new CompositionTracker(sink);
        var keys = Enum.GetValues(typeof(PhysicalKey));
        for (int i = 0; i < 100_000; i++)
        {
            DomKeyMap.ToPhysicalKey(S(r));
            KeyboardPolicy.ShouldPreventDefault((PhysicalKey)keys.GetValue(r.Next(keys.Length))!, DomKeyMap.ToModifiers(I(r)), r.Next(2) == 0, I(r),
                r.Next(10) == 0 ? null : S(r), r.Next(2) == 0);
            var attach = AttachResult.Parse(r.Next(10) == 0 ? null : Enumerable.Range(0, r.Next(0, 9)).Select(_ => D(r)).ToArray(), r.Next(2) == 0);
            if (attach.Status == AttachStatus.Ok) Assert.True(attach.Metrics.Scale > 0);
            TextInputFilter.Classify(S(r), r.Next(2) == 0);
            var clean = TextInputFilter.SanitizeCommit(S(r));
            foreach (var c in clean) Assert.False(c < 0x20 && c != '\t' && c != '\n');
            t.Handle(r.Next(-1, 4), r.Next(10) == 0 ? null : S(r), I(r), I(r));
            Assert.InRange(t.SelectionStart, 0, t.Text.Length);
            Assert.InRange(t.SelectionEnd, t.SelectionStart, t.Text.Length);
            Assert.True(t.Text.Length <= CompositionTracker.MaxCompositionLength);
            Assert.Equal(t.IsComposing, sink.Open);
        }
        Assert.True(sink.Count > 1000);
    }

    /// <summary>逐个检查事件序列配对：Start 只在没有进行中的组合时出现，Update / End 只在组合中出现，提交非空。</summary>
    sealed class PairingSink : ITextInputSink
    {
        public bool Open;
        public int Count;
        public void OnComposition(in TextCompositionEvent e)
        {
            Count++;
            if (e.Kind == TextCompositionKind.Start) { Assert.False(Open); Open = true; }
            else Assert.True(Open);
            if (e.Kind == TextCompositionKind.End) Open = false;
            Assert.InRange(e.SelectionStart, 0, e.Text.Length);
            Assert.InRange(e.SelectionEnd, e.SelectionStart, e.Text.Length);
        }
        public void OnCommit(string text) => Assert.NotEmpty(text);
    }

    [Fact]
    public void Scheduler()
    {
        var r = new Random(5);
        var s = new FrameScheduler();
        bool jsPending = false; // 模拟 JS 端是否真的挂了 rAF
        long last = -1;
        for (int i = 0; i < 100_000; i++)
        {
            switch (r.Next(5))
            {
                case 0: if (s.Request()) { Assert.False(jsPending); jsPending = true; } break;
                case 1:
                    if (!jsPending) break; // 浏览器只会回调已挂的 rAF
                    jsPending = false;
                    if (s.OnAnimationFrame(D(r), out var ts)) { Assert.True(ts.Ticks >= last); last = ts.Ticks; }
                    break;
                case 2:
                    switch (s.SetVisible(r.Next(2) == 0))
                    {
                        case VisibilityAction.Schedule: Assert.False(jsPending); jsPending = true; break;
                        case VisibilityAction.Cancel: Assert.True(jsPending); jsPending = false; break;
                    }
                    break;
                default: break;
            }
            Assert.Equal(jsPending, s.CallbackPending);
            // 不变量：有需求且可见时一定挂着回调（不会丢帧请求）
            if (s.FrameRequested && s.Visible) Assert.True(s.CallbackPending);
        }
    }

    [Fact]
    public void LargeInputsAreLinear()
    {
        // 1 MB 的粘贴 / 组合文本：清洗与截断都是线性的
        var sw = Stopwatch.StartNew();
        var big = new string('\u0001', 4 << 20);
        Assert.Equal("", TextInputFilter.SanitizeCommit(big));
        var t = new CompositionTracker(new RecordingSink());
        for (int i = 0; i < 1000; i++) t.Update(big, -1, -1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }
}

/// <summary>JS 胶水与 C# 两侧约定的一致性（按源码文本检查，防止只改了一边）。</summary>
public class ProtocolConsistencyTests
{
    static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AwAUI.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    static string Glue => RepoFile("src/Browser/AwAUI.Browser/wwwroot/awaui-browser.js");

    static int JsConst(string js, string name)
    {
        var m = Regex.Match(js, @"\b" + name + @"\s*=\s*(-?\d+)");
        Assert.True(m.Success, name);
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public void ConstantsMatch()
    {
        var js = Glue;
        Assert.Equal(DomProtocol.PointerDown, JsConst(js, "POINTER_DOWN"));
        Assert.Equal(DomProtocol.PointerUp, JsConst(js, "POINTER_UP"));
        Assert.Equal(DomProtocol.PointerMove, JsConst(js, "POINTER_MOVE"));
        Assert.Equal(DomProtocol.PointerEnter, JsConst(js, "POINTER_ENTER"));
        Assert.Equal(DomProtocol.PointerLeave, JsConst(js, "POINTER_LEAVE"));
        Assert.Equal(DomProtocol.PointerCancel, JsConst(js, "POINTER_CANCEL"));
        Assert.Equal(DomProtocol.CompositionStart, JsConst(js, "COMPOSITION_START"));
        Assert.Equal(DomProtocol.CompositionUpdate, JsConst(js, "COMPOSITION_UPDATE"));
        Assert.Equal(DomProtocol.CompositionEnd, JsConst(js, "COMPOSITION_END"));
        // 修饰键与设备编码写在 JS 的 lambda 里
        Assert.Contains("(e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0)", js);
        Assert.Contains("e.getModifierState('AltGraph') ? 16 : 0", js);
        Assert.Equal(16, DomProtocol.ModAltGraph);
        Assert.Equal(AttachResult.ErrorAlreadyAttached, JsConst(js, "ATTACH_ALREADY"));
        Assert.Equal(AttachResult.ErrorNoContext, JsConst(js, "ATTACH_NO_CONTEXT"));
        Assert.Contains("t === 'touch' ? 1 : t === 'pen' ? 2 : 0", js);
        Assert.Equal((DomProtocol.ModShift, DomProtocol.ModControl, DomProtocol.ModAlt, DomProtocol.ModMeta), (1, 2, 4, 8));
        Assert.Equal((DomProtocol.DeviceMouse, DomProtocol.DeviceTouch, DomProtocol.DevicePen), (0, 1, 2));
    }

    [Fact]
    public void EveryJSImportIsExportedByTheGlue()
    {
        var js = Glue;
        var cs = RepoFile("src/Browser/AwAUI.Browser/Interop/BrowserInterop.cs");
        var imports = Regex.Matches(cs, @"\[JSImport\(""(\w+)"", ModuleName\)\]").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(imports);
        foreach (var name in imports)
            Assert.Matches(@"export (async )?function " + name + @"\(", js);
    }

    [Fact]
    public void EveryCallbackUsedByTheGlueIsAJSExport()
    {
        var js = Glue;
        var cs = RepoFile("src/Browser/AwAUI.Browser/Interop/BrowserCallbacks.cs");
        var exports = new HashSet<string>(Regex.Matches(cs, @"\[JSExport\]\s+internal static \S+ (\w+)\(").Cast<Match>().Select(m => m.Groups[1].Value));
        var used = Regex.Matches(js, @"\bcb\.(\w+)\(").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(used);
        foreach (var name in used) Assert.Contains(name, exports);
    }

    [Fact]
    public void CallbackArityMatches()
    {
        // JS 调用 cb.X(...) 的实参个数必须等于 C# [JSExport] 的形参个数（JSExport 不校验个数，少传会得到默认值）
        var js = Glue;
        var cs = RepoFile("src/Browser/AwAUI.Browser/Interop/BrowserCallbacks.cs");
        foreach (Match m in Regex.Matches(cs, @"\[JSExport\]\s+internal static \S+ (\w+)\(([^)]*)\)"))
        {
            string name = m.Groups[1].Value;
            int expected = m.Groups[2].Value.Trim().Length == 0 ? 0 : m.Groups[2].Value.Split(',').Length;
            foreach (Match call in Regex.Matches(js, @"\bcb\." + name + @"\("))
                Assert.Equal(expected, CountArgs(js, call.Index + call.Length));
        }
    }

    /// <summary>从左括号之后数顶层逗号（忽略嵌套括号与字符串中的逗号）。</summary>
    static int CountArgs(string s, int i)
    {
        int depth = 0, args = 0;
        bool any = false;
        char quote = '\0';
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') { quote = c; any = true; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) return any ? args + 1 : 0;
                depth--;
            }
            else if (c == ',' && depth == 0) args++;
            else if (!char.IsWhiteSpace(c)) any = true;
        }
        throw new InvalidOperationException("unterminated call");
    }
}
