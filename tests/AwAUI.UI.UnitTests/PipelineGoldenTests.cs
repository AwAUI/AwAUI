using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 端到端黄金图像：元素树 + CSS → 帧管线 → 软件后端。文本用 TestTextEngine 的方块字体，结果与平台字体无关。
/// 期望图片在本项目的 Golden 目录；失败时实际输出与差异图写到测试输出目录下的 GoldenOutput。
/// 设置环境变量 AWAUI_UPDATE_GOLDEN=1 重新生成（须人工审核后再提交）。
/// </summary>
public class PipelineGoldenTests
{
    const string CardCss = """
        root { background-color: #e9eef5; padding: 16px; }
        .card { display: flex; flex-direction: column; gap: 10px; width: 220px; padding: 14px; background-color: #ffffff;
                border: 1px solid #d0d7e2; border-radius: 12px; box-shadow: 0 8px 20px -6px rgba(20, 30, 60, 0.35); }
        .header { height: 36px; border-radius: 8px; background-image: linear-gradient(to right, #4f46e5, #06b6d4); }
        .row { display: flex; align-items: center; gap: 8px; }
        .avatar { width: 28px; height: 28px; border-radius: 50%; overflow: hidden; background-color: #f59e0b; }
        .avatar > div { width: 14px; height: 28px; background-color: #b45309; }
        .name { font-size: 10px; color: #1f2937; flex: 1; }
        .muted { opacity: 0.4; }
        button { font-size: 10px; padding: 4px 10px; border-radius: 6px; background-color: #4f46e5; color: #ffffff; border-color: #4338ca; }
        """;

    static Node Card() => new Div
    {
        Class = "card", Children =
        [
            new Div { Class = "header" },
            new Div
            {
                Class = "row", Children =
                [
                    new Div { Class = "avatar", Children = [new Div()] },
                    new Text("Ada Lovelace") { Class = "name" },
                    new Button { Id = "b", Children = ["OK"] },
                ],
            },
            new Text("faded text") { Class = "name muted" },
        ],
    };

    [Fact]
    public void Card_scene_at_2x()
    {
        var h = new PipelineHarness(Card(), CardCss, width: 280, height: 180, scale: 2);
        // 键盘聚焦按钮：focus-visible 轮廓
        h.Root.Press(Platform.PhysicalKey.Tab);
        h.Frame();
        using var bmp = h.Render();
        AssertMatches("ui-card@2x", bmp);
    }

    [Fact]
    public void Borders_gradients_and_shadows()
    {
        const string css = """
            root { background-color: #ffffff; padding: 12px; }
            .grid { display: flex; flex-wrap: wrap; gap: 12px; }
            .grid > div { width: 70px; height: 50px; }
            .sides { border-style: solid; border-width: 4px 8px 4px 8px; border-color: #ef4444 #22c55e #3b82f6 #eab308; border-radius: 10px; }
            .dashed { border: 3px dashed #6366f1; border-radius: 8px; }
            .radial { background-image: radial-gradient(circle at 30% 30%, #fde68a, #f97316 60%, #7c2d12); border-radius: 25px / 12px; }
            .inset { background-color: #f1f5f9; box-shadow: inset 0 3px 6px rgba(0, 0, 0, 0.35); border-radius: 8px; }
            .stripes { background-image: repeating-linear-gradient(45deg, #0ea5e9 0px, #0ea5e9 6px, #e0f2fe 6px, #e0f2fe 12px); }
            .corner { background-image: linear-gradient(to bottom right, #111827, #9ca3af); border-radius: 0 16px 0 16px; }
            """;
        var h = new PipelineHarness(new Div
        {
            Class = "grid", Children =
            [
                new Div { Class = "sides" }, new Div { Class = "dashed" }, new Div { Class = "radial" },
                new Div { Class = "inset" }, new Div { Class = "stripes" }, new Div { Class = "corner" },
            ],
        }, css, width: 280, height: 150, scale: 1);
        using var bmp = h.Render();
        AssertMatches("ui-borders", bmp);
    }

    static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.Combine(Path.GetDirectoryName(file)!, "Golden");

    static void AssertMatches(string name, Bitmap actual, int tolerance = 2)
    {
        var dir = GoldenDir();
        var path = Path.Combine(dir, name + ".png");
        var outDir = Path.Combine(AppContext.BaseDirectory, "GoldenOutput");
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            PngCodec.Save(actual, path);
            return;
        }
        Directory.CreateDirectory(outDir);
        if (!File.Exists(path))
        {
            PngCodec.Save(actual, Path.Combine(outDir, name + ".actual.png"));
            Assert.Fail($"Golden image '{name}.png' is missing. Review {outDir} and run with AWAUI_UPDATE_GOLDEN=1.");
        }
        using var expected = PngCodec.Load(path);
        Assert.Equal(expected.Size, actual.Size);
        var e = expected.ToRgbaUnpremultiplied();
        var a = actual.ToRgbaUnpremultiplied();
        int bad = 0, maxDiff = 0;
        for (int i = 0; i < e.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(e[i + c] - a[i + c]));
            maxDiff = Math.Max(maxDiff, d);
            if (d > tolerance) bad++;
        }
        if (bad > 0)
        {
            PngCodec.Save(actual, Path.Combine(outDir, name + ".actual.png"));
            Assert.Fail($"'{name}': {bad} pixels differ (max channel diff {maxDiff}). See {outDir}.");
        }
    }
}
