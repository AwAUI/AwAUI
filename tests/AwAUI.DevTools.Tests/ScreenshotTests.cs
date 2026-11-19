using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 面板与应用并排的截图（2x）：总会渲染并检查尺寸；设置环境变量 <c>AWAUI_DEVTOOLS_PNG</c> 时写到该路径（人工查看用）。
/// </summary>
public class ScreenshotTests
{
    [Fact]
    public void Renders_the_panel_next_to_the_app()
    {
        using var h = new Harness(scale: 2);
        h.Open();
        var count = h.AppById("count");
        h.ClickPanel(h.PanelRow(count.UniqueId)!);
        h.Settle();
        using var composite = Compose(h.AppWindow.Framebuffer!, h.PanelWindow.Framebuffer!);
        Assert.Equal(h.AppWindow.Framebuffer!.Width + h.PanelWindow.Framebuffer!.Width + 24, composite.Width);
        if (Environment.GetEnvironmentVariable("AWAUI_DEVTOOLS_PNG") is { Length: > 0 } path)
        {
            PngCodec.Save(composite, path);
            string stem = Path.ChangeExtension(path, null);
            // 其他状态：编辑一个值、计算值页、事件页（只在人工查看时写出）
            var rule = h.Model.Styles!.Sections[0].Blocks.First(b => b.Selector == ".count");
            h.Model.BeginEdit(rule, rule.Decls.FindIndex(d => d.Name == "color"));
            h.Settle();
            PngCodec.Save(h.PanelWindow.Framebuffer!, stem + "-editing.png");
            h.Model.CancelEdit();
            h.ClickPanel(h.PanelById("dt-tab-computed")!);
            PngCodec.Save(h.PanelWindow.Framebuffer!, stem + "-computed.png");
            h.ClickApp(h.AppById("increment"));
            h.ClickPanel(h.PanelById("dt-tab-events")!);
            PngCodec.Save(h.PanelWindow.Framebuffer!, stem + "-events.png");
        }
    }

    /// <summary>把应用帧与面板帧左右拼在一张图上（中间 24 设备像素的灰色间隔，顶端对齐）。</summary>
    internal static Bitmap Compose(Bitmap app, Bitmap panel)
    {
        int gap = 24;
        var size = new PixelSize(app.Width + gap + panel.Width, Math.Max(app.Height, panel.Height));
        var result = new Bitmap(size, app.Format);
        result.Clear(new Color(0.85f, 0.87f, 0.9f));
        Blit(app, result, 0);
        Blit(panel, result, app.Width + gap);
        return result;
    }

    static void Blit(Bitmap src, Bitmap dst, int x)
    {
        int bpp = 4;
        for (int y = 0; y < src.Height; y++)
            src.GetRow(y).Slice(0, src.Width * bpp).CopyTo(dst.GetRow(y).Slice(x * bpp, src.Width * bpp));
    }
}
