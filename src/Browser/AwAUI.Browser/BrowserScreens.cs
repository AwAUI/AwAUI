using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Browser.Interop;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>
/// Web 上的 <see cref="Screens"/>：浏览器只暴露一个显示器（<c>window.screen</c>）。
/// 边界与工作区来自 <c>screen.width/height/avail*</c>（CSS 像素，即逻辑像素），缩放来自 <c>devicePixelRatio</c>，
/// 名称取自 <c>screen.orientation.type</c>。分辨率 / DPR / 方向变化经 JSON 胶水的 <c>matchMedia</c> 与
/// <c>orientationchange</c> 监听，触发 <see cref="Screens.Changed"/>。
/// </summary>
internal sealed class BrowserScreens : Screens
{
    Screen[] _all;
    Screen _primary;

    internal BrowserScreens()
    {
        try
        {
            BrowserInterop.WatchScreens();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 旧版胶水没有这个入口：显示器仍然可读，只是不会再收到变化通知。
        }
        var screen = ReadScreen();
        _all = new[] { screen };
        _primary = screen;
    }

    public override IReadOnlyList<Screen> All => _all;

    public override Screen Primary => _primary;

    /// <summary>JS 端报告显示器信息可能变化（分辨率媒体查询、旋转、窗口尺寸）。只在内容真的变化时触发事件。</summary>
    internal void Refresh()
    {
        var screen = ReadScreen();
        if (_all.Length == 1 && Same(_primary, screen)) return;
        _all = new[] { screen };
        _primary = screen;
        OnChanged();
    }

    static bool Same(Screen a, Screen b) =>
        a.Name == b.Name && a.Bounds == b.Bounds && a.WorkingArea == b.WorkingArea && a.Scaling == b.Scaling && a.IsPrimary == b.IsPrimary;

    /// <summary>
    /// 解析 JS 的 <c>screensInfo()</c>：<c>orientation|width|height|availWidth|availHeight|availLeft|availTop|dpr</c>。
    /// 取不到信息时返回一个 0 尺寸的主显示器（浏览器总是至少有一个逻辑显示器）。
    /// </summary>
    static Screen ReadScreen()
    {
        string raw;
        try
        {
            raw = BrowserInterop.ScreensInfo() ?? string.Empty;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            raw = string.Empty;
        }

        var parts = raw.Split('|');
        if (parts.Length >= 8)
        {
            var values = new double[7];
            bool ok = true;
            for (int i = 0; i < values.Length; i++)
            {
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                double width = values[0], height = values[1];
                double availWidth = values[2], availHeight = values[3];
                double availLeft = values[4], availTop = values[5];
                double scaling = values[6];
                // screen.width / availWidth 是 CSS 像素；0 表示宿主环境没有报告布局（例如被内嵌的 worker 页面）
                if (width > 0 && height > 0 && availWidth > 0 && availHeight > 0)
                {
                    string name = string.IsNullOrEmpty(parts[0]) ? "Browser" : parts[0];
                    return new Screen(
                        name,
                        new Rect(0, 0, (float)width, (float)height),
                        new Rect((float)availLeft, (float)availTop, (float)availWidth, (float)availHeight),
                        scaling,
                        isPrimary: true);
                }
            }
        }
        return new Screen("Browser", Rect.Empty, Rect.Empty, 1, isPrimary: true);
    }
}
