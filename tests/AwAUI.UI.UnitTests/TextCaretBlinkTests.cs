using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>插入点闪烁：获得焦点时按周期切换、键入与光标移动重新开始、失去焦点后保持可见。</summary>
[Collection(nameof(ShortcutConventionsCollection))]
public class TextCaretBlinkTests
{
    static byte[] Pixels(TextInputFixture f) => f.Pixels();

    static (int X, int Y) CaretPixel(InputElement input)
    {
        var b = input.BoundingRect;
        return ((int)MathF.Round(b.X + input.CaretLocal.X),
            (int)MathF.Round(b.Y + input.CaretLocal.Y + input.CaretLocal.Height / 2));
    }

    static (byte R, byte G, byte B, byte A) At(byte[] pixels, int x, int y, int width) =>
        (pixels[(y * width + x) * 4], pixels[(y * width + x) * 4 + 1], pixels[(y * width + x) * 4 + 2], pixels[(y * width + x) * 4 + 3]);

    [Fact]
    public void A_focused_caret_blinks_and_typing_restarts_the_visible_phase()
    {
        var f = TextInputFixture.Simple();
        var clock = new ManualUIClock();
        f.Host.Root.Clock = clock;
        f.Focus();
        var input = f.Input();
        Assert.True(input.CaretVisible);

        clock.Advance(RenderPipeline.CaretBlinkInterval + TimeSpan.FromMilliseconds(1));
        f.Frame();
        Assert.False(input.CaretVisible);

        // 键入后重新开始可见阶段（不等到下一个周期）
        f.Type("a");
        f.Frame();
        Assert.True(input.CaretVisible);

        clock.Advance(RenderPipeline.CaretBlinkInterval + TimeSpan.FromMilliseconds(1));
        f.Frame();
        Assert.False(input.CaretVisible);
    }

    [Fact]
    public void The_painted_caret_disappears_during_the_hidden_phase()
    {
        var f = TextInputFixture.Simple();
        var clock = new ManualUIClock();
        f.Host.Root.Clock = clock;
        f.Focus();
        var input = f.Input();
        f.Frame();

        var (x, y) = CaretPixel(input);
        var visible = At(Pixels(f), x, y, 400);
        Assert.Equal((byte)255, visible.A);

        clock.Advance(RenderPipeline.CaretBlinkInterval + TimeSpan.FromMilliseconds(1));
        f.Frame();
        var hidden = At(Pixels(f), x, y, 400);
        // 隐藏后该像素是背景（白色），与可见时的插入点颜色不同
        Assert.True(hidden.R > visible.R, $"visible={visible} hidden={hidden} at ({x},{y})");
    }

    [Fact]
    public void Per_control_interval_overrides_the_process_default_and_zero_disables_blinking()
    {
        var f = TextInputFixture.Simple();
        var clock = new ManualUIClock();
        f.Host.Root.Clock = clock;
        var input = f.Input();
        input.CaretBlinkInterval = TimeSpan.FromMilliseconds(100);
        f.Focus();
        Assert.True(input.CaretVisible);

        clock.Advance(TimeSpan.FromMilliseconds(101));
        f.Frame();
        Assert.False(input.CaretVisible);

        // TimeSpan.Zero：常亮，不再安排定时器
        input.CaretBlinkInterval = TimeSpan.Zero;
        f.Frame();
        Assert.True(input.CaretVisible);
        clock.Advance(TimeSpan.FromTicks(RenderPipeline.CaretBlinkInterval.Ticks * 4));
        f.Frame();
        Assert.True(input.CaretVisible);
    }

    [Fact]
    public void The_node_property_reaches_the_input_element()
    {
        var f = new TextInputFixture(_ => new Div
        {
            Children = { new Input { Id = "in", CaretBlinkInterval = TimeSpan.FromMilliseconds(250) } },
        }, "input { field-sizing: content; }");
        Assert.Equal(TimeSpan.FromMilliseconds(250), f.Input().CaretBlinkInterval);
    }

    [Fact]
    public void Losing_focus_stops_the_blink_and_keeps_the_caret_visible()
    {
        var f = TextInputFixture.Simple();
        var clock = new ManualUIClock();
        f.Host.Root.Clock = clock;
        f.Focus();
        var input = f.Input();

        f.Focus("other");
        Assert.True(input.CaretVisible);
        Assert.False(input.IsFocused);

        clock.Advance(RenderPipeline.CaretBlinkInterval + RenderPipeline.CaretBlinkInterval);
        f.Frame();
        Assert.True(input.CaretVisible);
    }
}
