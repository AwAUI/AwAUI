using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

/// <summary>WheelEvent.deltaMode → <see cref="WheelDeltaMode"/>（框架按它决定是否平滑、按页时按滚动容器的页尺寸重新换算）。</summary>
public class WheelModeTests
{
    static readonly Size Client = new(640, 440);

    static DomWheelSample Wheel(double dx, double dy, int mode, double legacy = double.NaN) =>
        new(10, 20, 640, 440, dx, dy, mode, legacy, 0, 0, 1000);

    [Fact]
    public void Delta_modes_map_to_wheel_modes_and_browsers_never_report_phases()
    {
        var px = PointerTranslator.TranslateWheel(Wheel(0, 53, DomProtocol.DeltaPixel), Client);
        Assert.Equal(WheelDeltaMode.Pixel, px.WheelDeltaMode);
        Assert.True(px.PreciseWheel);
        Assert.Equal(-53, px.WheelDeltaY);
        Assert.Equal(ScrollPhase.None, px.ScrollPhase);
        Assert.Equal(ScrollPhase.None, px.MomentumPhase);

        // 像素模式但旧式 wheelDeltaY 是 120 的倍数：鼠标滚轮，不是精确设备
        var notch = PointerTranslator.TranslateWheel(Wheel(0, 100, DomProtocol.DeltaPixel, -120), Client);
        Assert.Equal(WheelDeltaMode.Pixel, notch.WheelDeltaMode);
        Assert.False(notch.PreciseWheel);

        var line = PointerTranslator.TranslateWheel(Wheel(0, 3, DomProtocol.DeltaLine), Client);
        Assert.Equal(WheelDeltaMode.Line, line.WheelDeltaMode);
        Assert.Equal(-3 * DomProtocol.WheelLineStep, line.WheelDeltaY);

        var page = PointerTranslator.TranslateWheel(Wheel(0, 1, DomProtocol.DeltaPage), Client);
        Assert.Equal(WheelDeltaMode.Page, page.WheelDeltaMode);
        Assert.Equal(-440, page.WheelDeltaY);
    }
}
