using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>帧调度（只在脏时请求帧）、点击 → State → 重建 → 重排 → 重绘的往返，以及窗口宿主（无窗口平台）。</summary>
public partial class PipelineFrameTests
{
    partial class Clicker : Component
    {
        readonly State<int> count = new(0);

        protected override Node Build() => new Div
        {
            Class = "row", Children =
            [
                new Text("n=" + count.Value) { Id = "label" },
                new Button { Id = "inc", OnClick = _ => count.Value++, Children = ["+"] },
            ],
        };
    }

    const string RowCss = ".row { display: flex; align-items: center; gap: 4px; } text { font-size: 10px; } button { font-size: 10px; padding: 2px; border-width: 0; }";

    [Fact]
    public void Idle_after_mount_and_frames_only_when_dirty()
    {
        var h = new PipelineHarness(new Clicker(), RowCss);
        Assert.False(h.Root.NeedsFrame);
        int requests = h.FrameRequests;
        long version = h.Pipeline.FrameVersion;

        // 没有变化的帧：不重录
        h.Root.RunFrame();
        Assert.Equal(version, h.Pipeline.FrameVersion);
        Assert.Equal(requests, h.FrameRequests);
        Assert.False(h.Pipeline.LastTimings.Repainted);
    }

    [Fact]
    public void Click_round_trip_rebuilds_relayouts_and_repaints()
    {
        var h = new PipelineHarness(new Clicker(), RowCss);
        // "n=0" 3 个字符 × 10px
        Assert.Equal(30, h.Box("label").Width);
        var inc = h.Box("inc");
        int requests = h.FrameRequests;

        h.Click(inc.Center.X, inc.Center.Y);
        Assert.True(h.FrameRequests > requests, "点击后应请求一帧");
        Assert.True(h.Root.NeedsFrame);
        long version = h.Pipeline.FrameVersion;
        h.Frame();
        Assert.Equal("n=1", h.El("label").TextContent);
        Assert.True(h.Pipeline.FrameVersion > version);

        for (int i = 0; i < 9; i++)
        {
            h.Click(h.Box("inc").Center.X, h.Box("inc").Center.Y);
            h.Frame();
        }
        Assert.Equal("n=10", h.El("label").TextContent);
        Assert.Equal(40, h.Box("label").Width);
        Assert.Equal(44, h.Box("inc").X); // 按钮随文本变宽右移
        Assert.True(h.Pipeline.LastTimings.LayoutRan);
        Assert.False(h.Root.NeedsFrame);
    }

    [Fact]
    public void Keyboard_tab_and_enter_activate_buttons()
    {
        var h = new PipelineHarness(new Clicker(), RowCss);
        h.Root.Press(PhysicalKey.Tab);
        Assert.Equal("inc", h.Root.FocusManager.FocusedElement?.Id);
        h.Root.Press(PhysicalKey.Enter);
        h.Root.Press(PhysicalKey.Space);
        h.Frame();
        Assert.Equal("n=2", h.El("label").TextContent);
    }

    [Fact]
    public void Paint_only_change_does_not_relayout()
    {
        var hot = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Text("abc") { Id = "t", Style = hot.Value ? "color: #ff0000" : "color: #000000" },
        });
        hot.Value = true;
        h.Frame();
        Assert.False(h.Pipeline.LastTimings.LayoutRan);
        Assert.True(h.Pipeline.LastTimings.Repainted);
        Assert.Equal(1, h.Pipeline.LastTimings.ParagraphsBuilt);
    }

    [Fact]
    public void Deep_tree_does_not_crash_the_pipeline()
    {
        Node node = new Text("leaf");
        for (int i = 0; i < 3000; i++) node = new Div { Children = [node] };
        var diags = new List<UIDiagnostic>();
        var pipeline = new RenderPipeline(new TestTextEngine());
        var root = new UIRoot { Pipeline = pipeline };
        root.Diagnostic += diags.Add;
        root.UnhandledError += _ => { };
        var ex = Record.Exception(() => root.Mount(new Lambda { Render = () => node }));
        Assert.Null(ex);
    }

    [Fact]
    public void Host_drives_frames_input_cursor_and_resize_through_headless_window()
    {
        var platform = new HeadlessPlatform { DefaultScale = 2 };
        var host = AwApp.CreateHost(platform, new WindowOptions { Size = new Size(200, 100) }, new SoftwareRenderBackend(), new TestTextEngine(),
            new Clicker(), Css.StyleSheet.Parse(RowCss + " button { cursor: pointer; } #inc { background-color: #00ff00; }"));
        var window = (HeadlessWindow)host.Window;
        int frames = 0;
        host.FrameCompleted += _ => frames++;
        platform.RunFrame();
        Assert.Equal(1, frames);
        Assert.True(host.LastFrame.Rendered);
        Assert.NotNull(window.Framebuffer);
        Assert.Equal(new PixelSize(400, 200), window.Framebuffer!.Size);

        // 空闲：没有帧请求就不会渲染
        platform.RunFrame();
        Assert.Equal(1, frames);

        var inc = host.Root.RootElement.ById("inc").BoundingRect.Center;
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, inc, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal(CursorShape.Hand, window.Cursor);
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, inc, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, inc, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        platform.RunFrame();
        Assert.True(host.LastFrame.Rendered);
        Assert.Equal("n=1", host.Root.RootElement.ById("label").TextContent);
        // 按钮背景出现在帧缓冲中（2x）
        var incRect = host.Root.RootElement.ById("inc").BoundingRect;
        var px = window.Framebuffer!.GetPixel((int)(incRect.X * 2) + 2, (int)(inc.Y * 2));
        ColorAssert.Near(Color.Parse("#00ff00"), px, 0.05f);

        window.SendPointer(new RawPointerEvent(RawPointerEventType.Leave, PointerType.Mouse, 1, inc, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal(CursorShape.Arrow, window.Cursor);

        window.Resize(new Size(300, 120), 1);
        platform.RunFrame();
        Assert.True(host.LastFrame.Rendered);
        Assert.Equal(new Size(300, 120), host.Pipeline.Viewport);
        Assert.Equal(new PixelSize(300, 120), window.Framebuffer!.Size);

        window.Close();
        Assert.Empty(platform.Windows);
    }
}
