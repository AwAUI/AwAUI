using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

public class PointerTests
{
    static readonly Size Client = new(640, 440);

    static DomPointerSample Sample(int kind, double x = 10, double y = 20, int button = -1, int buttons = 0, int device = DomProtocol.DeviceMouse,
        double rectW = 640, double rectH = 440, double pressure = double.NaN, int mods = 0, double time = 1000, int id = 1) =>
        new(kind, device, id, x, y, rectW, rectH, button, buttons, pressure, mods, time);

    static RawPointerEvent Translate(in DomPointerSample s)
    {
        Assert.True(PointerTranslator.TryTranslate(s, Client, out var e));
        return e;
    }

    [Fact]
    public void DownMoveUp()
    {
        var down = Translate(Sample(DomProtocol.PointerDown, button: 0, buttons: 1, pressure: 0.5));
        Assert.Equal(RawPointerEventType.Down, down.Type);
        Assert.Equal(PointerButtons.Left, down.ChangedButton);
        Assert.Equal(PointerButtons.Left, down.Buttons);
        Assert.Equal(new Point(10, 20), down.Position);
        Assert.Equal(TimeSpan.FromTicks(1000 * TimeSpan.TicksPerMillisecond), down.Timestamp);

        var move = Translate(Sample(DomProtocol.PointerMove, 11.5, 21.25, buttons: 1));
        Assert.Equal(RawPointerEventType.Move, move.Type);
        Assert.Equal(PointerButtons.None, move.ChangedButton);
        Assert.Equal(PointerButtons.Left, move.Buttons);
        Assert.Equal(new Point(11.5f, 21.25f), move.Position);

        var up = Translate(Sample(DomProtocol.PointerUp, button: 0, buttons: 0));
        Assert.Equal(RawPointerEventType.Up, up.Type);
        Assert.Equal(PointerButtons.Left, up.ChangedButton);
        Assert.Equal(PointerButtons.None, up.Buttons);
    }

    [Fact]
    public void UpClearsReleasedButtonEvenIfBrowserStillReportsIt()
    {
        var up = Translate(Sample(DomProtocol.PointerUp, button: 2, buttons: 2 | 1));
        Assert.Equal(PointerButtons.Right, up.ChangedButton);
        Assert.Equal(PointerButtons.Left, up.Buttons);
    }

    [Fact]
    public void ChordedButtonsArriveAsPointerMove()
    {
        // 左键按住时按下右键：W3C 规定派发 pointermove，button=2，buttons=3
        var down = Translate(Sample(DomProtocol.PointerMove, button: 2, buttons: 3));
        Assert.Equal(RawPointerEventType.Down, down.Type);
        Assert.Equal(PointerButtons.Right, down.ChangedButton);
        Assert.Equal(PointerButtons.Left | PointerButtons.Right, down.Buttons);

        // 再松开右键：pointermove，button=2，buttons=1
        var up = Translate(Sample(DomProtocol.PointerMove, button: 2, buttons: 1));
        Assert.Equal(RawPointerEventType.Up, up.Type);
        Assert.Equal(PointerButtons.Right, up.ChangedButton);
        Assert.Equal(PointerButtons.Left, up.Buttons);
    }

    [Theory]
    [InlineData(-1, PointerButtons.None)]
    [InlineData(0, PointerButtons.Left)]
    [InlineData(1, PointerButtons.Middle)]
    [InlineData(2, PointerButtons.Right)]
    [InlineData(3, PointerButtons.X1)]
    [InlineData(4, PointerButtons.X2)]
    [InlineData(5, PointerButtons.None)] // 笔的橡皮擦
    [InlineData(99, PointerButtons.None)]
    public void ChangedButton(int button, PointerButtons expected) => Assert.Equal(expected, PointerTranslator.ToButton(button));

    [Fact]
    public void ButtonMaskUsesDomBitOrder()
    {
        Assert.Equal(PointerButtons.Left, PointerTranslator.ToButtons(1));
        Assert.Equal(PointerButtons.Right, PointerTranslator.ToButtons(2));
        Assert.Equal(PointerButtons.Middle, PointerTranslator.ToButtons(4));
        Assert.Equal(PointerButtons.X1 | PointerButtons.X2, PointerTranslator.ToButtons(8 | 16));
        Assert.Equal(PointerButtons.None, PointerTranslator.ToButtons(32)); // 橡皮擦
    }

    [Theory]
    [InlineData(DomProtocol.PointerEnter, RawPointerEventType.Enter)]
    [InlineData(DomProtocol.PointerLeave, RawPointerEventType.Leave)]
    [InlineData(DomProtocol.PointerCancel, RawPointerEventType.Cancel)]
    public void EnterLeaveCancelHaveNoChangedButton(int kind, RawPointerEventType expected)
    {
        var e = Translate(Sample(kind, button: 0, buttons: 1));
        Assert.Equal(expected, e.Type);
        Assert.Equal(PointerButtons.None, e.ChangedButton);
    }

    [Fact]
    public void UnknownKindIsRejected() => Assert.False(PointerTranslator.TryTranslate(Sample(42), Client, out _));

    [Theory]
    [InlineData(DomProtocol.DeviceMouse, PointerType.Mouse)]
    [InlineData(DomProtocol.DeviceTouch, PointerType.Touch)]
    [InlineData(DomProtocol.DevicePen, PointerType.Pen)]
    [InlineData(7, PointerType.Mouse)]
    public void Devices(int device, PointerType expected) => Assert.Equal(expected, Translate(Sample(DomProtocol.PointerMove, device: device)).Device);

    [Fact]
    public void TouchKeepsPointerIdAndPressure()
    {
        var e = Translate(Sample(DomProtocol.PointerDown, button: 0, buttons: 1, device: DomProtocol.DeviceTouch, pressure: 0.8, id: 17));
        Assert.Equal(17, e.PointerId);
        Assert.Equal(0.8f, e.Pressure, 5);
    }

    [Fact]
    public void PressureDefaultsAndClamps()
    {
        Assert.Equal(0f, Translate(Sample(DomProtocol.PointerMove)).Pressure);
        Assert.Equal(0.5f, Translate(Sample(DomProtocol.PointerDown, button: 0, buttons: 1)).Pressure);
        Assert.Equal(1f, Translate(Sample(DomProtocol.PointerMove, buttons: 1, pressure: 7)).Pressure);
        Assert.Equal(0f, Translate(Sample(DomProtocol.PointerMove, buttons: 1, pressure: -1)).Pressure);
    }

    [Fact]
    public void CssTransformScaleIsUndone()
    {
        // canvas 逻辑 640×440，被 CSS transform: scale(0.5) 显示为 320×220
        var e = Translate(Sample(DomProtocol.PointerMove, 160, 110, rectW: 320, rectH: 220));
        Assert.Equal(new Point(320, 220), e.Position);
    }

    [Fact]
    public void CapturedPointerOutsideCanvasKeepsNegativeCoordinates()
    {
        var e = Translate(Sample(DomProtocol.PointerMove, -40, 500, buttons: 1));
        Assert.Equal(new Point(-40, 500), e.Position);
    }

    [Fact]
    public void ModifiersAndTime()
    {
        var e = Translate(Sample(DomProtocol.PointerDown, button: 0, buttons: 1, mods: DomProtocol.ModShift | DomProtocol.ModMeta, time: 1234.5678));
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Meta, e.Modifiers);
        Assert.Equal(12_345_678, e.Timestamp.Ticks);
    }

    [Fact]
    public void NonFiniteInputsAreSanitized()
    {
        var e = Translate(new DomPointerSample(DomProtocol.PointerMove, 0, 1, double.NaN, double.PositiveInfinity, double.NaN, 0, -1, -1,
            double.NaN, 0, double.NegativeInfinity));
        Assert.Equal(0, e.Position.X);
        Assert.Equal(0, e.Position.Y);
        Assert.Equal(TimeSpan.Zero, e.Timestamp);
        var huge = Translate(Sample(DomProtocol.PointerMove, 1e300, -1e300, rectW: 1e-300, rectH: 1e-300));
        Assert.Equal(DomProtocol.MaxCoordinate, huge.Position.X);
        Assert.Equal(-DomProtocol.MaxCoordinate, huge.Position.Y);
    }

    static DomWheelSample Wheel(double dx, double dy, int mode = DomProtocol.DeltaPixel, double legacy = double.NaN) =>
        new(100, 50, 640, 440, dx, dy, mode, legacy, 0, DomProtocol.ModControl, 5);

    [Fact]
    public void WheelSignMatchesOtherPlatforms()
    {
        // DOM：deltaY>0 = 向下滚动。平台约定（Win32/X11/macOS）：向上滚动为正，向右滚动为负
        var e = PointerTranslator.TranslateWheel(Wheel(30, 120), Client);
        Assert.Equal(RawPointerEventType.Wheel, e.Type);
        Assert.Equal(-30f, e.WheelDeltaX);
        Assert.Equal(-120f, e.WheelDeltaY);
        Assert.Equal(new Point(100, 50), e.Position);
        Assert.Equal(KeyModifiers.Control, e.Modifiers);
    }

    [Fact]
    public void WheelLineAndPageModes()
    {
        var line = PointerTranslator.TranslateWheel(Wheel(0, 3, DomProtocol.DeltaLine), Client);
        Assert.Equal(-3 * DomProtocol.WheelLineStep, line.WheelDeltaY);
        Assert.False(line.PreciseWheel);
        var page = PointerTranslator.TranslateWheel(Wheel(1, -1, DomProtocol.DeltaPage), Client);
        Assert.Equal(-640f, page.WheelDeltaX);
        Assert.Equal(440f, page.WheelDeltaY);
    }

    [Theory]
    [InlineData(DomProtocol.DeltaPixel, double.NaN, true)]     // Firefox / Safari 触控板：没有 wheelDeltaY
    [InlineData(DomProtocol.DeltaPixel, -120.0, false)]        // Chromium 鼠标滚轮：每格 120
    [InlineData(DomProtocol.DeltaPixel, 240.0, false)]
    [InlineData(DomProtocol.DeltaPixel, -7.0, true)]           // 触控板细粒度
    [InlineData(DomProtocol.DeltaPixel, 0.0, true)]
    [InlineData(DomProtocol.DeltaLine, double.NaN, false)]      // Firefox 鼠标滚轮
    public void PreciseWheelHeuristic(int mode, double legacy, bool expected) =>
        Assert.Equal(expected, PointerTranslator.IsPreciseWheel(mode, legacy));

    [Fact]
    public void WheelNonFiniteDeltasAreZero()
    {
        var e = PointerTranslator.TranslateWheel(Wheel(double.NaN, double.PositiveInfinity, DomProtocol.DeltaPage), new Size(0, 0));
        Assert.Equal(0f, e.WheelDeltaX);
        Assert.Equal(0f, e.WheelDeltaY);
        var big = PointerTranslator.TranslateWheel(Wheel(0, 1e9, DomProtocol.DeltaPage), Client);
        Assert.Equal(-DomProtocol.MaxCoordinate, big.WheelDeltaY);
    }

    [Theory]
    [InlineData(0.0, 0L)]
    [InlineData(-5.0, 0L)]
    [InlineData(double.NaN, 0L)]
    [InlineData(16.6667, 166_667L)]
    public void Timestamps(double ms, long ticks) => Assert.Equal(ticks, DomProtocol.ToTimeSpan(ms).Ticks);

    [Fact]
    public void HugeTimestampDoesNotOverflow()
    {
        Assert.True(DomProtocol.ToTimeSpan(double.MaxValue) > TimeSpan.Zero);
        Assert.True(DomProtocol.ToTimeSpan(double.PositiveInfinity) == TimeSpan.Zero);
    }
}
