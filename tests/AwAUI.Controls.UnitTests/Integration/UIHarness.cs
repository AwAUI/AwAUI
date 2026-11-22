using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.Text.Testing;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>按给定函数构建的测试组件。</summary>
sealed partial class Host : Component
{
    public Func<Node> Render { get; set; } = () => new Fragment();

    protected override Node Build() => Render();
}

/// <summary>
/// 无头端到端测试的装配：<see cref="RenderPipeline"/>（默认 <see cref="TestTextEngine"/>：每字符 1em 的方块字体）+ <see cref="UIRoot"/>，
/// 以及指针、键盘与滚轮的模拟。
/// </summary>
sealed class UIHarness
{
    public readonly UIRoot Root;
    public readonly RenderPipeline Pipeline;
    /// <summary>构建过程中发布的诊断（AW1005、AW2002 等），供需要断言无警告的用例使用。</summary>
    public readonly List<UIDiagnostic> Diagnostics = new();
    TimeSpan _time;

    public UIHarness(Func<Node> render, string? css = null, float width = 800, float height = 600, float scale = 1, TextEngine? text = null,
        bool appStyleSheets = false)
    {
        Pipeline = new RenderPipeline(text ?? new TestTextEngine());
        // 只有需要组件库样式的验收（Shadcn）才接入程序集级样式表：它含 preflight，会改变其他用例依赖的默认盒模型
        Pipeline.IncludeAppStyleSheets = appStyleSheets;
        if (css is not null) Pipeline.AddStyleSheet(css, "test.css");
        Pipeline.SetViewport(new Size(width, height), scale);
        Root = new UIRoot { Pipeline = Pipeline };
        Root.Diagnostic += Diagnostics.Add;
        Root.Mount(new Host { Render = render });
        Frame();
    }

    /// <summary>执行帧直到没有待处理的工作（最多 20 帧）。</summary>
    public void Frame()
    {
        for (int i = 0; i < 20; i++)
        {
            Root.RunFrame();
            if (!Root.NeedsFrame) return;
        }
    }

    public Element? Focused => Root.FocusManager.FocusedElement;

    public Element ById(string id) => FindAll(e => e.Id == id).Single();

    public Element? TryById(string id) => FindAll(e => e.Id == id).SingleOrDefault();

    public List<Element> FindAll(Func<Element, bool> predicate)
    {
        var result = new List<Element>();
        var stack = new Stack<Element>();
        stack.Push(Root.RootElement);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (predicate(e)) result.Add(e);
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
        return result;
    }

    public Rect Box(string id) => ById(id).GetBoundingClientRect();

    RawPointerEvent P(RawPointerEventType type, Point p, PointerButtons changed, PointerButtons buttons, KeyModifiers mods = KeyModifiers.None)
    {
        _time += TimeSpan.FromMilliseconds(16);
        return new RawPointerEvent(type, PointerType.Mouse, 1, p, changed, buttons, mods, _time);
    }

    /// <summary>在窗口坐标处点击（命中测试 → 按下 → 抬起），然后执行帧。</summary>
    public void ClickAt(Point p)
    {
        Root.Input.DispatchPointer(P(RawPointerEventType.Move, p, PointerButtons.None, PointerButtons.None));
        Root.Input.DispatchPointer(P(RawPointerEventType.Down, p, PointerButtons.Left, PointerButtons.Left));
        Root.Input.DispatchPointer(P(RawPointerEventType.Up, p, PointerButtons.Left, PointerButtons.None));
        Frame();
    }

    /// <summary>点击元素的中心。</summary>
    public void Click(Element e) => ClickAt(e.GetBoundingClientRect().Center);

    public void Click(string id) => Click(ById(id));

    /// <summary>在窗口坐标处轻触（触摸指针按下 → 抬起），然后执行帧。</summary>
    public void TapAt(Point p)
    {
        _time += TimeSpan.FromMilliseconds(16);
        Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Touch, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, _time));
        Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Touch, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, _time));
        Frame();
    }

    /// <summary>轻触元素的中心。</summary>
    public void Tap(Element e) => TapAt(e.GetBoundingClientRect().Center);

    /// <summary>在窗口坐标处右键点击（命中测试 → 按下 → 抬起），然后执行帧。</summary>
    public void RightClickAt(Point p)
    {
        Root.Input.DispatchPointer(P(RawPointerEventType.Move, p, PointerButtons.None, PointerButtons.None));
        Root.Input.DispatchPointer(P(RawPointerEventType.Down, p, PointerButtons.Right, PointerButtons.Right));
        Root.Input.DispatchPointer(P(RawPointerEventType.Up, p, PointerButtons.Right, PointerButtons.None));
        Frame();
    }

    public void RightClick(Element e) => RightClickAt(e.GetBoundingClientRect().Center);

    public void PointerDown(Point p, PointerButtons button = PointerButtons.Left, KeyModifiers mods = KeyModifiers.None)
    {
        Root.Input.DispatchPointer(P(RawPointerEventType.Move, p, PointerButtons.None, PointerButtons.None, mods));
        Root.Input.DispatchPointer(P(RawPointerEventType.Down, p, button, button, mods));
        Frame();
    }

    public void PointerMove(Point p, PointerButtons buttons = PointerButtons.Left, KeyModifiers mods = KeyModifiers.None)
    {
        Root.Input.DispatchPointer(P(RawPointerEventType.Move, p, PointerButtons.None, buttons, mods));
        Frame();
    }

    public void PointerUp(Point p, PointerButtons button = PointerButtons.Left, KeyModifiers mods = KeyModifiers.None)
    {
        Root.Input.DispatchPointer(P(RawPointerEventType.Up, p, button, PointerButtons.None, mods));
        Frame();
    }

    /// <summary>把文本输入到当前聚焦的输入框。</summary>
    public void TypeText(string text)
    {
        _time += TimeSpan.FromMilliseconds(16);
        Root.Input.DispatchTextInput(text);
        Frame();
    }

    public void Key(PhysicalKey key, string? logical = null, KeyModifiers mods = KeyModifiers.None)
    {
        _time += TimeSpan.FromMilliseconds(16);
        Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, key, mods, false, 0, _time, logical));
        Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Up, key, mods, false, 0, _time, logical));
        Frame();
    }

    public void Tab(bool shift = false) => Key(PhysicalKey.Tab, "Tab", shift ? KeyModifiers.Shift : KeyModifiers.None);

    public void Escape() => Key(PhysicalKey.Escape, "Escape");

    public void Wheel(Point p, float deltaY) => Wheel(p, 0, deltaY);

    /// <summary>横向滚轮（正值 = 向左滚动）。</summary>
    public void WheelX(Point p, float deltaX) => Wheel(p, deltaX, 0);

    void Wheel(Point p, float deltaX, float deltaY)
    {
        _time += TimeSpan.FromMilliseconds(16);
        Root.Input.DispatchPointer(RawPointerEvent.CreateWheel(p, deltaX, deltaY, WheelDeltaMode.Pixel, true, ScrollPhase.None, ScrollPhase.None, PointerButtons.None, KeyModifiers.None, _time));
        Frame();
    }

    /// <summary>用软件后端把当前帧光栅化。</summary>
    public Bitmap Render(Color? clear = null)
    {
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(Pipeline.Viewport, Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, Pipeline.Scale);
        target.Render(Pipeline.Frame, new RenderOptions(clear ?? Color.White));
        var copy = new Bitmap(px);
        target.Bitmap.CopyTo(copy);
        return copy;
    }
}
