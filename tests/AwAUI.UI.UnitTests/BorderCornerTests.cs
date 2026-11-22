using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 分边颜色的圆角边框（Fluent 的 <c>.aw-button</c> 与 <c>.aw-textbox</c>）：每条边以经过圆角弧心的对角线
/// 分割边框环，四个角的弧段必须完整绘制。回归「默认按钮四角没有边框」——内顶点若取内边距盒的直角角点，
/// 圆角外侧的楔形不在任何梯形内，会露出页面背景。
/// </summary>
public class BorderCornerTests
{
    static Color C(string hex) => Color.Parse(hex);

    const string Box = "width:40px;height:20px;margin:10px;background:#ff0000;border-style:solid;";

    static Page Render(string style, float scale)
    {
        // 页面背景用红色：边框环没画到的角落会露出红色，便于区分「漏画」与抗锯齿。
        var h = new PipelineHarness(new Div { Id = "box", Style = style }, width: 60, height: 40, scale: scale);
        h.Frame();
        return new Page(h, h.Render(C("#ff0000")), h.El("box").BoundingRect);
    }

    sealed class Page(PipelineHarness harness, Bitmap pixels, Rect box)
    {
        public PipelineHarness Harness => harness;
        public Bitmap Pixels => pixels;
        public Rect Box => box;

        /// <summary>某个角上边框环中间（沿 45° 对角线、距角点 R − 边框宽/2）的设备像素。</summary>
        public Color Corner(bool left, bool top, float radius, float border)
        {
            float scale = Harness.Pipeline.Scale;
            float cx = (left ? Box.Left : Box.Right) + (left ? radius : -radius);
            float cy = (top ? Box.Top : Box.Bottom) + (top ? radius : -radius);
            float d = (radius - border / 2) / MathF.Sqrt(2);
            int x = (int)MathF.Round((cx + (left ? -d : d)) * scale);
            int y = (int)MathF.Round((cy + (top ? -d : d)) * scale);
            return Pixels.GetPixel(x, y);
        }

        public void AssertCornersPainted(float radius, float border) =>
            Assert.All(new[] { (true, true), (false, true), (true, false), (false, false) }, c =>
            {
                var px = Corner(c.Item1, c.Item2, radius, border);
                // 盒子没有背景色，边框直接画在红色页面上。四角必须被边框覆盖（允许两色之间的
                // 抗锯齿混合）；绿色 / 蓝色分量为 0 说明该处是页面背景，即圆角楔形没有被画到。
                Assert.True(px.G > 0.2f && px.B > 0.2f,
                    $"corner {c} shows the page background: {px}");
            });
    }

    [Fact]
    public void Per_side_border_colors_paint_all_four_rounded_corners()
    {
        // Fluent .aw-button：三边 #b3b3b3、底边 #707070，半径 4px。
        var page = Render(Box + "border-width:1px;border-color:#b3b3b3;border-bottom-color:#707070;border-radius:4px;", scale: 2);
        page.AssertCornersPainted(radius: 4, border: 1);
    }

    [Fact]
    public void Translucent_per_side_border_colors_paint_all_four_rounded_corners()
    {
        // 半透明边框不能被「先铺一层不透明底色」的做法跳过；几何修复对两者都成立。
        var page = Render(Box + "border-width:1px;border-color:#b3b3b380;border-bottom-color:#70707080;border-radius:4px;", scale: 2);
        page.AssertCornersPainted(radius: 4, border: 1);
    }

    [Fact]
    public void Different_border_widths_keep_the_corner_arc()
    {
        // Fluent .aw-textbox：1px 三边、底边 2px。
        var page = Render(Box + "border-width:1px 1px 2px;border-color:#b3b3b3;border-bottom-color:#707070;border-radius:4px;", scale: 2);
        page.AssertCornersPainted(radius: 4, border: 1);
    }

    [Fact]
    public void Corner_diagonal_is_fully_covered_at_higher_scale()
    {
        // 4x 时边框环有 4 个设备像素宽，对角线中点的像素应被边框完全覆盖（不是与背景混合）。
        var page = Render(Box + "border-width:1px;border-color:#b3b3b3;border-bottom-color:#707070;border-radius:4px;", scale: 4);
        Assert.All(new[] { (true, true), (false, true) }, c =>
        {
            var px = page.Corner(c.Item1, c.Item2, radius: 4, border: 1);
            Assert.True(px.G > 0.6f && Math.Abs(px.R - px.G) < 0.05f, $"corner {c}: {px}");
        });
    }
}
