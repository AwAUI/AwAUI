using System.Diagnostics;
using System.Threading;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.Graphics;
using AwAUI.Platform;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// <c>Overlay.setShowOptions({ touches: true })</c> 的触摸点叠加：按下的触摸 / 笔绘制圆 + 十字准星，
/// 抬起后消失；鼠标也由输入管理器记录但不绘制。以应用帧缓冲的像素断言（叠加层合成在应用帧之上）。
/// </summary>
public class OverlayTouchesTests
{
    static JsonObject Call(Harness h, string method, JsonObject? p = null)
    {
        DevToolsResponse? response = null;
        h.P.Client.Call(method, p, null, r => response = r);
        var sw = Stopwatch.StartNew();
        while (response is null)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"No response to {method}.");
            h.Frames();
            Thread.Sleep(1);
        }
        Assert.False(response.IsError, method + " failed: " + response);
        return response.ResultObject;
    }

    static void Pointer(Harness h, RawPointerEventType type, PointerType device, Point p, PointerButtons buttons) =>
        h.AppWindow.SendPointer(new RawPointerEvent(type, device, 7, p, buttons, buttons, KeyModifiers.None, TimeSpan.Zero));

    static Color[] Capture(Harness h, Rect r)
    {
        var fb = h.AppWindow.Framebuffer!;
        double s = h.AppWindow.RenderScale;
        int x0 = Math.Max(0, (int)(r.X * s)), y0 = Math.Max(0, (int)(r.Y * s));
        int x1 = Math.Min(fb.Width, (int)Math.Ceiling(r.Right * s));
        int y1 = Math.Min(fb.Height, (int)Math.Ceiling(r.Bottom * s));
        var pixels = new List<Color>(Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0));
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
                pixels.Add(fb.GetPixel(px, py));
        return pixels.ToArray();
    }

    /// <summary>触摸点描边色（#ff5722e6）被叠加在应用背景上：红明显高于绿 / 蓝。</summary>
    static bool IsTouchPixel(Color c) => c.A > 0.5f && c.R > c.G + 0.2f && c.R > c.B + 0.2f && c.R > 0.6f;

    [Fact]
    public void Touch_and_pen_points_are_painted_and_mouse_is_not()
    {
        using var h = new Harness(appSize: new Size(320, 220));
        var p = new Point(285, 185);
        var region = new Rect(p.X - 20, p.Y - 20, 40, 40);

        Call(h, "Overlay.enable");
        Call(h, "Overlay.setShowOptions", new JsonObject { ["touches"] = true });
        h.Frames(3);
        var before = Capture(h, region);

        // 触摸按下：圆 + 十字准星出现
        Pointer(h, RawPointerEventType.Down, PointerType.Touch, p, PointerButtons.Left);
        h.Frames(2);
        var down = Capture(h, region);
        Assert.False(before.SequenceEqual(down), "Touch point did not paint anything.");
        Assert.Contains(down, IsTouchPixel);

        // 抬起：消失并恢复应用原始像素
        Pointer(h, RawPointerEventType.Up, PointerType.Touch, p, PointerButtons.None);
        h.Frames(2);
        var up = Capture(h, region);
        Assert.True(before.SequenceEqual(up), "Touch point pixels did not disappear after pointer up.");

        // 笔：与触摸一样绘制
        Pointer(h, RawPointerEventType.Down, PointerType.Pen, p, PointerButtons.Left);
        h.Frames(2);
        Assert.Contains(Capture(h, region), IsTouchPixel);
        Pointer(h, RawPointerEventType.Up, PointerType.Pen, p, PointerButtons.None);
        h.Frames(2);

        // 鼠标按下：不绘制触摸点
        Pointer(h, RawPointerEventType.Down, PointerType.Mouse, p, PointerButtons.Left);
        h.Frames(2);
        Assert.DoesNotContain(Capture(h, region), IsTouchPixel);
        Pointer(h, RawPointerEventType.Up, PointerType.Mouse, p, PointerButtons.None);
        h.Frames(2);

        // 关闭选项后不再绘制（即使仍有触摸按下）
        Call(h, "Overlay.setShowOptions", new JsonObject());
        Pointer(h, RawPointerEventType.Down, PointerType.Touch, p, PointerButtons.Left);
        h.Frames(2);
        Assert.DoesNotContain(Capture(h, region), IsTouchPixel);
        Pointer(h, RawPointerEventType.Up, PointerType.Touch, p, PointerButtons.None);
        h.Frames(2);
    }
}
