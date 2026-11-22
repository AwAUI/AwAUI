using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Json;
using AwAUI.Graphics;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// <c>Overlay.setShowOptions</c> 的实际绘制：标尺与布局网格轨道带出现在应用帧缓冲里，关闭选项后消失。
/// 触摸点需要输入管理器暴露活动指针位置（目前没有），因此不在这里断言。
/// </summary>
public class OverlayShowOptionsTests
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

    [Fact]
    public void Rulers_are_painted_and_disappear_when_disabled()
    {
        using var h = new Harness(appSize: new Size(320, 200));
        var vp = h.App.Window.ClientSize;

        var before = Capture(h, 0, 0, vp.Width, 16);
        Call(h, "Overlay.enable");
        Call(h, "Overlay.setShowOptions", new JsonObject { ["rulers"] = true });
        h.Frames(3);
        var on = Capture(h, 0, 0, vp.Width, 16);
        Assert.False(before.SequenceEqual(on), "Rulers did not paint anything in the top strip.");

        // 关闭选项后恢复应用原始像素
        Call(h, "Overlay.setShowOptions", new JsonObject());
        h.Frames(3);
        var off = Capture(h, 0, 0, vp.Width, 16);
        Assert.True(before.SequenceEqual(off), "Ruler pixels did not disappear after disabling the option.");
    }

    [Fact]
    public void Layout_grid_tracks_are_painted_for_a_flex_container()
    {
        using var h = new Harness(appSize: new Size(320, 220));
        var vp = h.App.Window.ClientSize;
        var root = h.App.Root.RootElement;   // .app { display: flex }

        var before = Capture(h, 16, 16, vp.Width - 16, vp.Height - 16);
        Call(h, "Overlay.enable");
        Call(h, "Overlay.setShowOptions", new JsonObject { ["layoutGrids"] = new JsonArray { root.UniqueId } });
        h.Frames(3);
        var on = Capture(h, 16, 16, vp.Width - 16, vp.Height - 16);
        Assert.False(before.SequenceEqual(on), "Layout grid track bands did not paint.");

        Call(h, "Overlay.setShowOptions", new JsonObject());
        h.Frames(3);
        var off = Capture(h, 16, 16, vp.Width - 16, vp.Height - 16);
        Assert.True(before.SequenceEqual(off), "Layout grid track bands did not disappear after disabling the option.");
    }

    static Color[] Capture(Harness h, float x, float y, float width, float height)
    {
        var fb = h.AppWindow.Framebuffer!;
        double s = h.AppWindow.RenderScale;
        int x0 = Math.Max(0, (int)(x * s)), y0 = Math.Max(0, (int)(y * s));
        int x1 = Math.Min(fb.Width, (int)Math.Ceiling((x + width) * s));
        int y1 = Math.Min(fb.Height, (int)Math.Ceiling((y + height) * s));
        var pixels = new List<Color>(Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0));
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
                pixels.Add(fb.GetPixel(px, py));
        return pixels.ToArray();
    }
}
