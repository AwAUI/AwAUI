using AwAUI.MacOS;
using AwAUI.Platform;
using AwAUI.Win32;
using AwAUI.X11;

namespace AwAUI.Platform.UnitTests;

/// <summary>
/// 滚轮输入的平台无关部分（<see cref="RawPointerEvent.CreateWheel"/>、<see cref="WheelDeltas"/>）与各后端的纯逻辑：
/// Win32 的 WHEEL_DELTA 换算与按页累加、X11 的按钮 4–7、macOS 的 NSEventPhase 映射。原生部分（消息循环、SystemParametersInfo、
/// NSEvent 读取）只能在对应系统上运行，这里没有覆盖。
/// </summary>
public class WheelInputTests
{
    [Fact]
    public void Wheel_event_carries_mode_precision_and_phases_and_the_legacy_constructor_stays_compatible()
    {
        var e = RawPointerEvent.CreateWheel(new Point(3, 4), -1, 2, WheelDeltaMode.Pixel, precise: true, ScrollPhase.Changed, ScrollPhase.None,
            PointerButtons.Left, KeyModifiers.Shift, TimeSpan.FromSeconds(1));
        Assert.Equal(RawPointerEventType.Wheel, e.Type);
        Assert.Equal(new Point(3, 4), e.Position);
        Assert.Equal(-1, e.WheelDeltaX);
        Assert.Equal(2, e.WheelDeltaY);
        Assert.True(e.PreciseWheel);
        Assert.Equal(WheelDeltaMode.Pixel, e.WheelDeltaMode);
        Assert.Equal(ScrollPhase.Changed, e.ScrollPhase);
        Assert.Equal(ScrollPhase.None, e.MomentumPhase);
        Assert.Equal(PointerButtons.Left, e.Buttons);
        Assert.Equal(KeyModifiers.Shift, e.Modifiers);

        var legacy = new RawPointerEvent(RawPointerEventType.Wheel, PointerType.Mouse, 0, default, PointerButtons.None, PointerButtons.None,
            KeyModifiers.None, TimeSpan.Zero, 0, 0, 40);
        Assert.Equal(WheelDeltaMode.Line, legacy.WheelDeltaMode);
        Assert.Equal(ScrollPhase.None, legacy.ScrollPhase);
        var legacyPrecise = new RawPointerEvent(RawPointerEventType.Wheel, PointerType.Mouse, 0, default, PointerButtons.None, PointerButtons.None,
            KeyModifiers.None, TimeSpan.Zero, 0, 0, 3, preciseWheel: true);
        Assert.Equal(WheelDeltaMode.Pixel, legacyPrecise.WheelDeltaMode);
    }

    [Fact]
    public void Non_finite_and_huge_deltas_are_sanitized()
    {
        var e = RawPointerEvent.CreateWheel(default, float.NaN, float.PositiveInfinity, WheelDeltaMode.Pixel, true, ScrollPhase.None, ScrollPhase.None,
            PointerButtons.None, KeyModifiers.None, TimeSpan.Zero);
        Assert.Equal(0, e.WheelDeltaX);
        Assert.Equal(0, e.WheelDeltaY);
        Assert.Equal(WheelDeltas.MaxDelta, WheelDeltas.Sanitize(1e30f));
        Assert.Equal(-WheelDeltas.MaxDelta, WheelDeltas.Sanitize(-1e30f));
    }

    [Fact]
    public void Notches_convert_with_the_system_lines_setting()
    {
        Assert.Equal(100, WheelDeltas.NotchesToPixels(1, 3), 3);
        Assert.Equal(-200, WheelDeltas.NotchesToPixels(-1, 6), 3);
        Assert.Equal(50, WheelDeltas.NotchesToPixels(0.5f, 3), 3);
        // 非法设置按默认 3 行，过大的钳制到 100 行
        Assert.Equal(100, WheelDeltas.NotchesToPixels(1, 0), 3);
        Assert.Equal(100 * WheelDeltas.PixelsPerLine, WheelDeltas.NotchesToPixels(1, 100000), 2);
    }

    [Fact]
    public void Win32_wheel_scales_by_lines_reports_fractional_deltas_as_precise_and_flips_horizontal()
    {
        var t = new Win32WheelTranslator();
        var size = new Size(800, 600);
        Assert.True(t.Translate(false, 120, size, out float dx, out float dy, out var mode, out bool precise));
        Assert.Equal((0f, 100f, WheelDeltaMode.Line, false), (dx, dy, mode, precise));
        Assert.True(t.Translate(false, -240, size, out dx, out dy, out mode, out precise));
        Assert.Equal(-200, dy, 3);
        // 高分辨率滚轮 / 精确触控板：不足一格按比例，标为精确（框架不再做平滑动画）
        Assert.True(t.Translate(false, 30, size, out dx, out dy, out mode, out precise));
        Assert.Equal(25, dy, 3);
        Assert.True(precise);
        Assert.Equal(WheelDeltaMode.Pixel, mode);
        // WM_MOUSEHWHEEL 正值 = 向右，框架约定正值向左
        Assert.True(t.Translate(true, 120, size, out dx, out dy, out mode, out precise));
        Assert.Equal(-100, dx, 3);
        Assert.Equal(0, dy);
        t.CharsPerNotch = 1;
        Assert.True(t.Translate(true, 120, size, out dx, out _, out _, out _));
        Assert.Equal(-WheelDeltas.PixelsPerLine, dx, 3);
        // 行数设置为 0：不滚动
        t.LinesPerNotch = 0;
        Assert.False(t.Translate(false, 120, size, out _, out _, out _, out _));
        Assert.False(t.Translate(false, 0, size, out _, out _, out _, out _));
    }

    [Fact]
    public void Win32_page_scroll_setting_accumulates_wheel_delta_until_a_full_notch()
    {
        var t = new Win32WheelTranslator { LinesPerNotch = Win32WheelTranslator.WheelPageScroll };
        var size = new Size(800, 600);
        Assert.False(t.Translate(false, 60, size, out _, out _, out _, out _));
        Assert.True(t.Translate(false, 60, size, out _, out float dy, out var mode, out _));
        Assert.Equal(WheelDeltaMode.Page, mode);
        Assert.Equal(600, dy);
        // 余数保留
        Assert.True(t.Translate(false, 200, size, out _, out dy, out _, out _));
        Assert.Equal(600, dy);
        Assert.True(t.Translate(false, 40, size, out _, out dy, out _, out _));
        Assert.Equal(600, dy);
        // 方向改变时清零
        Assert.False(t.Translate(false, 100, size, out _, out _, out _, out _));
        Assert.False(t.Translate(false, -100, size, out _, out _, out _, out _));
        Assert.True(t.Translate(false, -20, size, out _, out dy, out _, out _));
        Assert.Equal(-600, dy);
    }

    [Fact]
    public void X11_buttons_4_to_7_map_to_wheel_directions()
    {
        Assert.True(X11Wheel.TryGetDelta(4, 3, out float dx, out float dy));
        Assert.Equal((0f, 100f), (dx, dy));
        Assert.True(X11Wheel.TryGetDelta(5, 3, out dx, out dy));
        Assert.Equal(-100, dy, 3);
        Assert.True(X11Wheel.TryGetDelta(6, 3, out dx, out dy));
        Assert.Equal(100, dx, 3);
        Assert.True(X11Wheel.TryGetDelta(7, 3, out dx, out dy));
        Assert.Equal(-100, dx, 3);
        Assert.False(X11Wheel.TryGetDelta(1, 3, out _, out _));
        Assert.False(X11Wheel.TryGetDelta(8, 3, out _, out _));
        Assert.True(X11Wheel.IsWheelButton(4));
        Assert.False(X11Wheel.IsWheelButton(3));
    }

    [Theory]
    [InlineData(0UL, ScrollPhase.None)]
    [InlineData(1UL, ScrollPhase.Began)]
    [InlineData(2UL, ScrollPhase.Changed)]
    [InlineData(4UL, ScrollPhase.Changed)]
    [InlineData(8UL, ScrollPhase.Ended)]
    [InlineData(16UL, ScrollPhase.Cancelled)]
    [InlineData(32UL, ScrollPhase.MayBegin)]
    public void Mac_event_phases_map_to_scroll_phases(ulong nsPhase, ScrollPhase expected) => Assert.Equal(expected, MacWheel.MapPhase(nsPhase));

    [Fact]
    public void Mac_precise_deltas_are_points_and_line_deltas_scale_by_40()
    {
        MacWheel.Translate(1.5, -2.25, precise: true, out float dx, out float dy, out var mode);
        Assert.Equal((1.5f, -2.25f, WheelDeltaMode.Pixel), (dx, dy, mode));
        MacWheel.Translate(0, 1, precise: false, out dx, out dy, out mode);
        Assert.Equal((0f, 40f, WheelDeltaMode.Line), (dx, dy, mode));
        MacWheel.Translate(double.NaN, double.PositiveInfinity, precise: false, out dx, out dy, out _);
        Assert.Equal((0f, 0f), (dx, dy));
    }
}
