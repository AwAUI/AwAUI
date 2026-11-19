using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 网格端到端：CSS（grid-template / grid-area / repeat / span / order / place-*）→ 级联 → <see cref="LayoutMapper"/> → 布局 → 绘制。
/// 默认样式表设 box-sizing: border-box；TestTextEngine 每字符 1em。
/// </summary>
public class GridPipelineTests
{
    const string DashboardCss = """
        #dash { display: grid; width: 600px; height: 400px; padding: 12px; gap: 12px; background-color: #e2e8f0;
                grid-template: "head head head" 48px "nav main side" 1fr "nav foot foot" 60px / 140px 1fr 160px; }
        .head { grid-area: head; background-color: #1e293b; }
        .nav { grid-area: nav; background-color: #334155; }
        .main { grid-area: main; background-color: #ffffff; padding: 8px; display: grid; gap: 8px;
                grid-template-columns: repeat(auto-fill, minmax(70px, 1fr)); grid-auto-rows: 50px; }
        .side { grid-area: side; background-color: #cbd5e1; display: grid; place-items: center; }
        .foot { grid-area: foot; background-color: #94a3b8; }
        .card { background-color: #6366f1; border-radius: 6px; }
        .wide { grid-column: span 2; background-color: #f59e0b; }
        .tall { grid-row: span 2; background-color: #10b981; }
        .first { order: -1; background-color: #ef4444; }
        .badge { width: 40px; height: 40px; border-radius: 20px; background-color: #ec4899; }
        """;

    static Div Dashboard() => new()
    {
        Id = "dash", Children =
        [
            new Div { Id = "head", Class = "head" },
            new Div { Id = "nav", Class = "nav" },
            new Div
            {
                Id = "main", Class = "main", Children =
                [
                    new Div { Id = "c1", Class = "card" },
                    new Div { Id = "c2", Class = "card wide" },
                    new Div { Id = "c3", Class = "card tall" },
                    new Div { Id = "c4", Class = "card" },
                    new Div { Id = "c5", Class = "card first" },
                    new Div { Id = "c6", Class = "card" },
                ],
            },
            new Div { Id = "side", Class = "side", Children = [new Div { Id = "badge", Class = "badge" }] },
            new Div { Id = "foot", Class = "foot" },
        ],
    };

    [Fact]
    public void Dashboard_areas_fr_tracks_and_gaps()
    {
        var h = new PipelineHarness(Dashboard(), DashboardCss, width: 640, height: 440);
        // 内容盒 576×376：列 140 / 1fr / 160、两个 12 的间距 → 1fr = 252；行 48 / 1fr / 60 → 1fr = 244
        Assert.Equal(new Rect(0, 0, 600, 400), h.Box("dash"));
        Assert.Equal(new Rect(12, 12, 576, 48), h.Box("head"));
        Assert.Equal(new Rect(12, 72, 140, 316), h.Box("nav"));
        Assert.Equal(new Rect(164, 72, 252, 244), h.Box("main"));
        Assert.Equal(new Rect(428, 72, 160, 244), h.Box("side"));
        Assert.Equal(new Rect(164, 328, 424, 60), h.Box("foot"));
    }

    [Fact]
    public void Nested_auto_fill_grid_with_spans_order_and_place_items()
    {
        var h = new PipelineHarness(Dashboard(), DashboardCss, width: 640, height: 440);
        // main 内容盒 236 宽：auto-fill 的 minmax(70px, 1fr) 按 70 计 → floor((236 + 8) / 78) = 3 列，各 (236 - 16) / 3
        float col = (236f - 16) / 3, x0 = 164 + 8, y0 = 72 + 8;
        // order: -1 的 c5 最先放：第 0 行第 0 列；c1 → 第 0 行第 1 列；c2（span 2）第 0 行放不下 → 第 1 行 0–2；
        // c3（span 2 行）→ 第 1 行第 2 列、跨第 1、2 行；c4 → 第 2 行第 0 列；c6 → 第 2 行第 1 列
        Near(new Rect(x0, y0, col, 50), h.Box("c5"));
        Near(new Rect(x0 + col + 8, y0, col, 50), h.Box("c1"));
        Near(new Rect(x0, y0 + 58, 2 * col + 8, 50), h.Box("c2"));
        Near(new Rect(x0 + 2 * (col + 8), y0 + 58, col, 108), h.Box("c3"));
        Near(new Rect(x0, y0 + 116, col, 50), h.Box("c4"));
        Near(new Rect(x0 + col + 8, y0 + 116, col, 50), h.Box("c6"));
        // place-items: center → 徽标在 side 中居中
        Near(new Rect(428 + 60, 72 + 102, 40, 40), h.Box("badge"));
    }

    /// <summary>边框盒按设备像素对齐（1x 时取整）：分数轨道宽度允许 1px 误差。</summary>
    static void Near(Rect expected, Rect actual) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1 &&
                    Math.Abs(expected.Width - actual.Width) <= 1 && Math.Abs(expected.Height - actual.Height) <= 1,
            $"expected {expected}, actual {actual}");

    [Fact]
    public void Changing_template_relayouts_incrementally()
    {
        var root = new Div
        {
            Id = "g", Class = "g", Children = [new Div { Id = "a" }, new Div { Id = "b" }],
        };
        var h = new PipelineHarness(root, ".g { display: grid; grid-template-columns: 100px 1fr; width: 300px; grid-auto-rows: 20px; } .g.flip { grid-template-columns: 1fr 100px; }");
        Assert.Equal(new Rect(100, 0, 200, 20), h.Box("b"));
        h.El("g").SetClasses("g flip");
        h.Frame();
        Assert.Equal(new Rect(0, 0, 200, 20), h.Box("a"));
        Assert.Equal(new Rect(200, 0, 100, 20), h.Box("b"));
    }

    [Fact]
    public void Inline_grid_shrinks_to_its_tracks_and_text_sizes_rows()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div
                {
                    Id = "g", Class = "g", Children = [new Text("abc") { Id = "t1" }, new Text("abcdef") { Id = "t2" }],
                },
            ],
        }, ".g { display: inline-grid; grid-template-columns: auto auto; column-gap: 4px; } text { font-size: 10px; }");
        // max-content：30 + 4 + 60
        Assert.Equal(94, h.Box("g").Width);
        Assert.Equal(new Rect(34, 0, 60, 10), h.Box("t2"));
    }

    [Fact]
    public void Logical_margin_and_padding_affect_layout()
    {
        var h = new PipelineHarness(new Div { Id = "p", Children = [new Div { Id = "c" }] },
            "#p { padding-inline: 10px 20px; width: 200px; } #c { margin-inline-start: 5px; margin-block: 7px; height: 10px; }");
        // border-box：内容宽 200 - 30 = 170；子元素 x = 10 + 5，宽 165
        Assert.Equal(new Rect(15, 7, 165, 10), h.Box("c"));
    }

    [Fact]
    public void Grid_lines_from_css_names_and_negative_indices()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "g", Children = [new Div { Id = "a" }, new Div { Id = "b" }],
        }, "#g { display: grid; width: 300px; grid-template-columns: [left] 50px [mid] 1fr [right]; grid-auto-rows: 10px; }"
           + "#a { grid-column: mid / right; } #b { grid-column: 1 / -1; grid-row: 2; }");
        Assert.Equal(new Rect(50, 0, 250, 10), h.Box("a"));
        Assert.Equal(new Rect(0, 10, 300, 10), h.Box("b"));
    }

    /// <summary>
    /// 仪表盘概览图：设置 AWAUI_GRID_SHEET=路径 时写出 PNG（2x）。不设置时只检查几处像素颜色。
    /// </summary>
    [Fact]
    public void Dashboard_render()
    {
        var h = new PipelineHarness(Dashboard(), DashboardCss + " text { font-size: 12px; color: #ffffff; }", width: 624, height: 424, scale: 2);
        using var bmp = h.Render();
        ColorAssert.Near(Color.FromRgb(0x1e, 0x29, 0x3b), bmp.GetPixel(100 * 2, 30 * 2));   // head
        ColorAssert.Near(Color.FromRgb(0x33, 0x41, 0x55), bmp.GetPixel(50 * 2, 200 * 2));   // nav
        ColorAssert.Near(Color.FromRgb(0xef, 0x44, 0x44), bmp.GetPixel(200 * 2, 100 * 2));  // c5（order -1）
        ColorAssert.Near(Color.FromRgb(0xec, 0x48, 0x99), bmp.GetPixel(508 * 2, 194 * 2));  // badge 中心
        ColorAssert.Near(Color.FromRgb(0xe2, 0xe8, 0xf0), bmp.GetPixel(158 * 2, 200 * 2));  // 间距
        var path = Environment.GetEnvironmentVariable("AWAUI_GRID_SHEET");
        if (!string.IsNullOrEmpty(path)) File.WriteAllBytes(path, PngCodec.Encode(bmp));
    }
}
