using System;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>一次 PointerEvent 采样（JS 端已按 <see cref="DomProtocol"/> 编码）。</summary>
internal readonly struct DomPointerSample
{
    public DomPointerSample(int kind, int device, int pointerId, double offsetX, double offsetY, double rectWidth, double rectHeight,
        int button, int buttons, double pressure, int modifiers, double timeMs)
    {
        Kind = kind; Device = device; PointerId = pointerId; OffsetX = offsetX; OffsetY = offsetY;
        RectWidth = rectWidth; RectHeight = rectHeight; Button = button; Buttons = buttons; Pressure = pressure;
        Modifiers = modifiers; TimeMs = timeMs;
    }

    public int Kind { get; }
    public int Device { get; }
    public int PointerId { get; }

    /// <summary>相对 canvas 边框盒左上角的 CSS 像素（clientX − rect.left）。</summary>
    public double OffsetX { get; }
    public double OffsetY { get; }

    /// <summary>canvas 的 getBoundingClientRect() 尺寸（含 CSS 变换后的实际显示尺寸）。</summary>
    public double RectWidth { get; }
    public double RectHeight { get; }

    /// <summary>PointerEvent.button：-1 无变化，0 主键，1 中键，2 次键，3 后退，4 前进，5 笔的橡皮擦。</summary>
    public int Button { get; }

    /// <summary>PointerEvent.buttons 位掩码：1 主键，2 次键，4 中键，8 后退，16 前进，32 橡皮擦。</summary>
    public int Buttons { get; }
    public double Pressure { get; }
    public int Modifiers { get; }
    public double TimeMs { get; }
}

/// <summary>一次 WheelEvent（JS 端编码）。</summary>
internal readonly struct DomWheelSample
{
    public DomWheelSample(double offsetX, double offsetY, double rectWidth, double rectHeight, double deltaX, double deltaY,
        int deltaMode, double legacyWheelDeltaY, int buttons, int modifiers, double timeMs)
    {
        OffsetX = offsetX; OffsetY = offsetY; RectWidth = rectWidth; RectHeight = rectHeight; DeltaX = deltaX; DeltaY = deltaY;
        DeltaMode = deltaMode; LegacyWheelDeltaY = legacyWheelDeltaY; Buttons = buttons; Modifiers = modifiers; TimeMs = timeMs;
    }

    public double OffsetX { get; }
    public double OffsetY { get; }
    public double RectWidth { get; }
    public double RectHeight { get; }

    /// <summary>WheelEvent.deltaX / deltaY（DOM 约定：正值 = 向右 / 向下滚动）。</summary>
    public double DeltaX { get; }
    public double DeltaY { get; }
    public int DeltaMode { get; }

    /// <summary>非标准的 WheelEvent.wheelDeltaY（Chromium/WebKit：鼠标滚轮每格 ±120）；不存在时为 NaN。</summary>
    public double LegacyWheelDeltaY { get; }
    public int Buttons { get; }
    public int Modifiers { get; }
    public double TimeMs { get; }
}

/// <summary>DOM 指针 / 滚轮事件 → <see cref="RawPointerEvent"/>。</summary>
internal static class PointerTranslator
{
    /// <summary>
    /// 翻译一次指针采样。<paramref name="clientSize"/> 是 canvas 的逻辑尺寸（CSS 内容盒）：
    /// 偏移按「逻辑尺寸 / 显示尺寸」缩放，所以 canvas 被 CSS transform 缩放时坐标仍然正确。
    /// 未知事件种类返回 false。
    /// </summary>
    public static bool TryTranslate(in DomPointerSample s, Size clientSize, out RawPointerEvent e)
    {
        var buttons = ToButtons(s.Buttons);
        var changed = ToButton(s.Button);
        RawPointerEventType type;
        switch (s.Kind)
        {
            case DomProtocol.PointerDown: type = RawPointerEventType.Down; break;
            case DomProtocol.PointerUp: type = RawPointerEventType.Up; break;
            case DomProtocol.PointerEnter: type = RawPointerEventType.Enter; changed = PointerButtons.None; break;
            case DomProtocol.PointerLeave: type = RawPointerEventType.Leave; changed = PointerButtons.None; break;
            case DomProtocol.PointerCancel: type = RawPointerEventType.Cancel; changed = PointerButtons.None; break;
            case DomProtocol.PointerMove:
                // W3C「和弦按键」：已有按键按住时再按 / 松开另一个键，浏览器派发的是 pointermove 且 button ≠ -1
                if (changed != PointerButtons.None)
                    type = (buttons & changed) != 0 ? RawPointerEventType.Down : RawPointerEventType.Up;
                else
                    type = RawPointerEventType.Move;
                break;
            default:
                e = default;
                return false;
        }
        // 抬起时 buttons 里可能仍残留该键（某些浏览器对 pointerup 的 buttons 更新滞后），以事件语义为准
        if (type == RawPointerEventType.Up) buttons &= ~changed;
        else if (type == RawPointerEventType.Down) buttons |= changed;

        var device = ToDevice(s.Device);
        var position = ToLogical(s.OffsetX, s.OffsetY, s.RectWidth, s.RectHeight, clientSize);
        float pressure = (float)DomProtocol.Sanitize(s.Pressure, 0, 1, buttons != PointerButtons.None ? 0.5 : 0);
        e = new RawPointerEvent(type, device, s.PointerId, position, changed, buttons, DomKeyMap.ToModifiers(s.Modifiers),
            DomProtocol.ToTimeSpan(s.TimeMs), pressure);
        return true;
    }

    /// <summary>
    /// 翻译滚轮事件。<see cref="RawPointerEvent"/> 的滚动量约定与其他平台一致：正 Y = 内容向下移动（向上滚动），
    /// 正 X = 向左滚动，即与 DOM 的 deltaX/deltaY 符号相反。按行（deltaMode=1）每行 40 逻辑像素（Firefox 每格 3 行 = 120），按页取视口尺寸
    /// （<see cref="WheelDeltaMode.Page"/>，框架再按滚动容器的页尺寸换算）。
    /// </summary>
    public static RawPointerEvent TranslateWheel(in DomWheelSample s, Size clientSize)
    {
        double dx = DomProtocol.Sanitize(s.DeltaX, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        double dy = DomProtocol.Sanitize(s.DeltaY, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        switch (s.DeltaMode)
        {
            case DomProtocol.DeltaLine:
                dx *= DomProtocol.WheelLineStep;
                dy *= DomProtocol.WheelLineStep;
                break;
            case DomProtocol.DeltaPage:
                dx *= clientSize.Width > 0 ? clientSize.Width : 800;
                dy *= clientSize.Height > 0 ? clientSize.Height : 600;
                break;
        }
        float fx = (float)DomProtocol.Sanitize(-dx, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        float fy = (float)DomProtocol.Sanitize(-dy, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        var position = ToLogical(s.OffsetX, s.OffsetY, s.RectWidth, s.RectHeight, clientSize);
        var mode = s.DeltaMode switch
        {
            DomProtocol.DeltaLine => WheelDeltaMode.Line,
            DomProtocol.DeltaPage => WheelDeltaMode.Page,
            _ => WheelDeltaMode.Pixel,
        };
        // 浏览器不给手势 / 惯性相位（惯性由浏览器自己生成为普通的 wheel 事件），相位一律为 None
        return RawPointerEvent.CreateWheel(position, fx, fy, mode, IsPreciseWheel(s.DeltaMode, s.LegacyWheelDeltaY), ScrollPhase.None,
            ScrollPhase.None, ToButtons(s.Buttons), DomKeyMap.ToModifiers(s.Modifiers), DomProtocol.ToTimeSpan(s.TimeMs));
    }

    /// <summary>
    /// 浏览器不区分触控板与滚轮。启发式：按像素报告、且（若有）旧式 wheelDeltaY 不是 120 的非零整数倍时视为精确设备。
    /// 误判的代价只是上层是否再做平滑。
    /// </summary>
    public static bool IsPreciseWheel(int deltaMode, double legacyWheelDeltaY)
    {
        if (deltaMode != DomProtocol.DeltaPixel) return false;
        if (!DomProtocol.IsFinite(legacyWheelDeltaY) || legacyWheelDeltaY == 0) return true;
        return Math.Abs(Math.IEEERemainder(legacyWheelDeltaY, 120)) > 1e-6;
    }

    /// <summary>CSS 偏移 → 逻辑坐标。显示尺寸无效（0 / NaN）时按 1:1 处理。</summary>
    public static Point ToLogical(double offsetX, double offsetY, double rectWidth, double rectHeight, Size clientSize)
    {
        double x = DomProtocol.Sanitize(offsetX, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        double y = DomProtocol.Sanitize(offsetY, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        if (DomProtocol.IsFinite(rectWidth) && rectWidth > 1e-3 && clientSize.Width > 0) x *= clientSize.Width / rectWidth;
        if (DomProtocol.IsFinite(rectHeight) && rectHeight > 1e-3 && clientSize.Height > 0) y *= clientSize.Height / rectHeight;
        x = DomProtocol.Sanitize(x, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        y = DomProtocol.Sanitize(y, -DomProtocol.MaxCoordinate, DomProtocol.MaxCoordinate, 0);
        return new Point((float)x, (float)y);
    }

    public static PointerType ToDevice(int device) => device switch
    {
        DomProtocol.DeviceTouch => PointerType.Touch,
        DomProtocol.DevicePen => PointerType.Pen,
        _ => PointerType.Mouse,
    };

    /// <summary>PointerEvent.button → 变化的按键；-1 与橡皮擦（5）等无对应成员的值为 None。</summary>
    public static PointerButtons ToButton(int button) => button switch
    {
        0 => PointerButtons.Left,
        1 => PointerButtons.Middle,
        2 => PointerButtons.Right,
        3 => PointerButtons.X1,
        4 => PointerButtons.X2,
        _ => PointerButtons.None,
    };

    /// <summary>PointerEvent.buttons 位掩码 → <see cref="PointerButtons"/>（注意 DOM 的位序是 主、次、中）。</summary>
    public static PointerButtons ToButtons(int buttons)
    {
        var b = PointerButtons.None;
        if ((buttons & 1) != 0) b |= PointerButtons.Left;
        if ((buttons & 2) != 0) b |= PointerButtons.Right;
        if ((buttons & 4) != 0) b |= PointerButtons.Middle;
        if ((buttons & 8) != 0) b |= PointerButtons.X1;
        if ((buttons & 16) != 0) b |= PointerButtons.X2;
        return b;
    }
}
