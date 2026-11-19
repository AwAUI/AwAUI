using System.Text;
using AwAUI.Css;
using AwAUI.Diagnostics.Json;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.UI;
using AwAUI.UI.UnitTests;
using CounterSample;

namespace AwAUI.Samples.Tests;

/// <summary>
/// M1 验收（design/07）：「计数器 / 表单示例分别用 .awaui 与纯 C# 写成且行为一致；F12 可检查元素并实时改 CSS」。
/// 两个版本在无窗口平台（2x）上用软件后端渲染，执行同一串点击与输入，每一步都比较元素树与帧缓冲的 PNG 编码。
/// </summary>
public class CounterTests
{
    static CounterTests()
    {
        // 测试只注册拉丁字体（Noto Sans）：界面文字用英文，结果与本机字体无关
        Strings.Current = Strings.English;
    }

    static readonly string SamplesDir = FindSamplesDir();

    static string FindSamplesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "Counter");
            if (Directory.Exists(Path.Combine(candidate, "Styles"))) return candidate;
        }
        throw new DirectoryNotFoundException("samples/Counter not found above " + AppContext.BaseDirectory);
    }

    internal static TextEngine CreateTextEngine()
    {
        var fonts = new FontCollection();
        fonts.RegisterFile(Path.Combine(SamplesDir, "..", "..", "tests", "TestFiles", "Fonts", "NotoSans-Regular.ttf"));
        foreach (var generic in new[] { "sans-serif", "system-ui", "ui-sans-serif", "serif", "monospace" })
            fonts.SetGenericFamily(generic, "Noto Sans");
        return new DefaultTextEngine(fonts);
    }

    static StyleSheet Sheet(string name) => StyleSheet.Parse(File.ReadAllText(Path.Combine(SamplesDir, "Styles", name)), name);

    /// <summary>与 samples/Counter/Program.cs 相同的装配：C# 版加载 app.css + Counter.css，.awaui 版只加载 app.css（组件样式随组件注册）。</summary>
    internal sealed class App
    {
        public readonly HeadlessPlatform Platform = new() { DefaultScale = 2 };
        public readonly UIHost Host;
        public readonly HeadlessWindow Window;

        public App(bool markup)
        {
            Node root = markup ? new CounterSample.Markup.App() : new CounterSample.App();
            var sheets = markup ? new[] { Sheet("app.css") } : new[] { Sheet("app.css"), Sheet("Counter.css") };
            Host = AwApp.CreateHost(Platform, new WindowOptions { Size = new Size(720, 460) }, new SoftwareRenderBackend(), CreateTextEngine(), root, sheets);
            Window = (HeadlessWindow)Host.Window;
            Frames(2);
        }

        public Element Root => Host.Root.RootElement;

        public Element Find(Func<Element, bool> predicate) => All(predicate).Single();

        public List<Element> All(Func<Element, bool> predicate)
        {
            var list = new List<Element>();
            var stack = new Stack<Element>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (predicate(e)) list.Add(e);
                for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
            }
            return list;
        }

        public Element ById(string id) => Find(e => e.Id == id);

        public void Frames(int n = 1)
        {
            for (int i = 0; i < n; i++) Platform.RunFrame();
        }

        public void Pointer(RawPointerEventType type, Point p, PointerButtons changed, PointerButtons buttons) =>
            Window.SendPointer(new RawPointerEvent(type, PointerType.Mouse, 1, p, changed, buttons, KeyModifiers.None, TimeSpan.Zero));

        public void Click(Element e)
        {
            var p = e.BoundingRect.Center;
            Pointer(RawPointerEventType.Move, p, PointerButtons.None, PointerButtons.None);
            Pointer(RawPointerEventType.Down, p, PointerButtons.Left, PointerButtons.Left);
            Pointer(RawPointerEventType.Up, p, PointerButtons.Left, PointerButtons.None);
            Frames();
        }

        public void Key(PhysicalKey key)
        {
            Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, key, KeyModifiers.None, false, 0, TimeSpan.Zero));
            Window.SendKey(new RawKeyEvent(RawKeyEventType.Up, key, KeyModifiers.None, false, 0, TimeSpan.Zero));
            Frames();
        }

        public void Type(string text)
        {
            Window.SendText(text);
            Frames();
        }

        public byte[] Png() => PngCodec.Encode(Window.Framebuffer!);

        /// <summary>
        /// 元素树的完整描述：标签、id、class、属性（自定义与反射的）、文本、伪状态与布局矩形。
        /// 作用域样式标记（<see cref="Element.Scope"/>）只有 .awaui 版有，不计入（单独断言）。
        /// </summary>
        public string Tree()
        {
            var sb = new StringBuilder();
            var stack = new Stack<(Element, int)>();
            stack.Push((Root, 0));
            while (stack.Count > 0)
            {
                var (e, depth) = stack.Pop();
                sb.Append(' ', depth * 2);
                if (e is TextNodeElement t) sb.Append('"').Append(t.Text).Append('"');
                else
                {
                    sb.Append(e.Tag);
                    if (e.Id is not null) sb.Append('#').Append(e.Id);
                    foreach (var c in e.Classes) sb.Append('.').Append(c);
                    foreach (var kv in e.Attributes) sb.Append(" [").Append(kv.Key).Append('=').Append(kv.Value).Append(']');
                    foreach (var name in new[] { "role", "tabindex", "value", "placeholder", "disabled", "style" })
                        if (e.GetAttribute(name) is { } v) sb.Append(" [").Append(name).Append('=').Append(v).Append(']');
                    if (e is TextElement { Content: { } content }) sb.Append(" content=").Append(content);
                    sb.Append(" {").Append(e.States).Append('}');
                }
                var r = e.BoundingRect;
                sb.Append(FormattableString.Invariant($" @{r.X},{r.Y},{r.Width},{r.Height}"));
                sb.Append('\n');
                for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push((e.Children[i], depth + 1));
            }
            return sb.ToString();
        }
    }

    static void AssertSame(App cs, App markup, string step)
    {
        Assert.True(cs.Tree() == markup.Tree(), step + ": element trees differ\n--- C#\n" + cs.Tree() + "--- .awaui\n" + markup.Tree());
        Assert.True(cs.Png().AsSpan().SequenceEqual(markup.Png()), step + ": rendered frames differ");
    }

    [Fact]
    public void Csharp_and_awaui_counters_produce_identical_trees_and_frames_step_by_step()
    {
        var cs = new App(markup: false);
        var md = new App(markup: true);
        AssertSame(cs, md, "mount");
        Assert.Contains("Count: 0", cs.ById("count").TextContent);

        // .awaui 版的元素带作用域标记，组件样式表由运行时注册（作用域 + 全局两张）
        Assert.StartsWith("aw-", md.ById("count").Scope);
        Assert.Null(cs.ById("count").Scope);
        Assert.Equal(2, md.Host.Root.ComponentStyleSheets.Count);
        Assert.Empty(cs.Host.Root.ComponentStyleSheets);

        var steps = new List<(string Name, Action<App> Run)>
        {
            ("increment x3", a => { for (int i = 0; i < 3; i++) a.Click(a.ById("increment")); }),
            ("decrement", a => a.Click(a.ById("decrement"))),
            ("step x5", a => a.Click(a.Find(e => e.Tag == "button" && e.TextContent == "×5"))),
            ("increment by 5", a => a.Click(a.ById("increment"))),
            ("empty submit", a => a.Click(a.ById("greet"))),
            ("focus input", a => a.Click(a.ById("name"))),
            ("type", a => a.Type("Ada")),
            ("enter", a => a.Key(PhysicalKey.Enter)),
            ("tab focus", a => a.Key(PhysicalKey.Tab)),
            ("space activates", a => a.Key(PhysicalKey.Space)),
            ("hover chip", a => a.Pointer(RawPointerEventType.Move, a.Find(e => e.Tag == "button" && e.TextContent == "×10").BoundingRect.Center, PointerButtons.None, PointerButtons.None)),
            ("press and hold", a => a.Pointer(RawPointerEventType.Down, a.ById("increment").BoundingRect.Center, PointerButtons.Left, PointerButtons.Left)),
            ("release", a => a.Pointer(RawPointerEventType.Up, a.ById("increment").BoundingRect.Center, PointerButtons.Left, PointerButtons.None)),
        };
        foreach (var (name, run) in steps)
        {
            run(cs);
            run(md);
            cs.Frames(2);
            md.Frames(2);
            AssertSame(cs, md, name);
        }
        Assert.Equal("Count: 12", cs.ById("count").TextContent);
        Assert.Equal("Count: 12", md.ById("count").TextContent);
        Assert.Equal("Hello, Ada!", md.ById("greeting").TextContent);
    }

    static JsonObject P(string key, JsonValue value) => new() { [key] = value };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DevTools_inspects_the_counter_and_live_css_edits_change_the_next_frame(bool markup)
    {
        var app = new App(markup);
        var dt = app.Host.EnableDevTools();
        app.Frames();
        var client = new DevToolsClient(dt);

        // 元素树：根 → .app → 卡片 → 计数器
        client.Call("Tree.enable");
        var doc = client.Call("Tree.getDocument", P("depth", -1))["root"]!;
        var mirror = new TreeMirror();
        mirror.Load(doc);
        var count = app.ById("count");
        Assert.Equal("text#count", mirror.Labels[count.UniqueId]);
        var described = client.Call("Tree.describeNode", DevToolsClient.Node(count.UniqueId))["node"]!;
        Assert.Equal("Counter", described["componentName"]!.GetString());
        Assert.Equal("count", described["classes"]![0]!.GetString());
        var actions = app.Find(e => e.HasClass("actions"));
        Assert.Equal(new[] { app.ById("decrement").UniqueId, app.ById("increment").UniqueId }, mirror.Children[actions.UniqueId]);

        // 命中的规则：来自 Counter.css（C# 版）或 Counter.awaui 的 <style>（作用域选择器）
        var inc = app.ById("increment");
        var matched = client.Call("Css.getMatchedRules", DevToolsClient.Node(inc.UniqueId))["matched"]!.AsArray();
        var selectors = matched.Select(m => m["rule"]!["selectorText"]!.GetString()).ToList();
        Assert.Contains("button.primary", selectors);
        var actionRule = selectors.Single(s => s.StartsWith(".actions > button", StringComparison.Ordinal));
        if (markup) Assert.Contains("[" + inc.Scope + "]", actionRule);
        var primary = matched.First(m => m["rule"]!["selectorText"]!.GetString() == "button.primary")["rule"]!;
        Assert.Equal(markup ? typeof(CounterSample.Markup.Counter).FullName + " (global)" : "Counter.css", primary["source"]!["file"]!.GetString());
        Assert.Contains(primary["declarations"]!.AsArray(), d => d["name"]!.GetString() == "background-color" && d["status"]!.GetString() == "active");

        // 实时改 CSS：把 button.primary 改成纯色（去掉渐变），下一帧按钮中心变成该颜色
        var center = inc.BoundingRect.Center;
        var before = app.Window.Framebuffer!.GetPixel((int)(center.X * 2), (int)(center.Y * 2 - 24));
        client.Call("Css.setStyleText", new JsonObject
        {
            ["target"] = new JsonObject { ["ruleId"] = primary["id"]!.GetString() },
            ["text"] = "color: #ffffff; background-color: #00aa00; border: 1px solid #006600;",
        });
        app.Frames();
        var after = app.Window.Framebuffer!.GetPixel((int)(center.X * 2), (int)(center.Y * 2 - 24));
        Assert.NotEqual(before, after);
        Near(Color.Parse("#00aa00"), after);
        Near(Color.Parse("#00aa00"), app.Window.Framebuffer!.GetPixel((int)(app.ById("greet").BoundingRect.Center.X * 2), (int)(app.ById("greet").BoundingRect.Y * 2 + 6)));

        // 内联修改与计算值
        client.Call("Css.setStyleText", new JsonObject { ["target"] = DevToolsClient.Node(count.UniqueId), ["text"] = "color: #ff0000" });
        app.Frames();
        var color = client.Call("Css.getComputedStyle", DevToolsClient.Node(count.UniqueId))["properties"]!.AsArray()
            .Single(p => p["name"]!.GetString() == "color");
        Assert.Contains("255, 0, 0", color["value"]!.GetString());

        // 高亮：叠加层画在应用帧之上
        client.Call("Overlay.enable");
        var countBox = count.BoundingRect;
        var plain = app.Window.Framebuffer!.GetPixel((int)(countBox.X * 2 + 4), (int)(countBox.Center.Y * 2));
        client.Call("Overlay.highlightNode", new JsonObject { ["nodeId"] = count.UniqueId });
        app.Frames();
        var lit = app.Window.Framebuffer!.GetPixel((int)(countBox.X * 2 + 4), (int)(countBox.Center.Y * 2));
        Assert.NotEqual(plain, lit);

        // F12 拾取：点在「增加」上选中它，事件发给前端，应用没有收到点击
        string countText = count.TextContent;
        app.Key(PhysicalKey.F12);
        app.Click(inc);
        var picked = Assert.Single(client.Events("Overlay.nodePicked"));
        Assert.Equal(inc.UniqueId, picked["nodeId"]!.GetInt64());
        Assert.Equal(countText, app.ById("count").TextContent);
        Assert.Contains("button.primary", dt.DescribeElement(inc));

        // 还原全部修改（并隐藏高亮）
        client.Call("Overlay.hideHighlight");
        client.Call("Css.clearOverrides");
        app.Frames();
        Near(before, app.Window.Framebuffer!.GetPixel((int)(center.X * 2), (int)(center.Y * 2 - 24)), 0.06f);
    }

    static void Near(Color expected, Color actual, float tolerance = 0.03f)
    {
        bool ok = Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
                  && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance;
        Assert.True(ok, $"expected {expected}, actual {actual}");
    }
}
