using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Graphics.UnitTests;

public class BrushTests
{
    static readonly GradientStop[] TwoStops = { new(0, Color.Black), new(1, Color.White) };

    [Fact]
    public void Simple_radial_gradient_has_its_focus_at_the_center()
    {
        var b = new RadialGradientBrush(new Point(10, 20), 30, TwoStops);
        Assert.Equal(new Point(10, 20), b.FocalPoint);
        Assert.Equal(0, b.FocalRadius);
        Assert.False(b.HasFocus);
        // 显式给出与圆心相同、半径为 0 的焦点也是简单形式
        Assert.False(new RadialGradientBrush(new Point(10, 20), 30, new Point(10, 20), 0, TwoStops).HasFocus);
        // NaN 圆心的简单形式仍是简单形式（按 float.Equals 比较）
        Assert.False(new RadialGradientBrush(new Point(float.NaN, 0), 30, TwoStops).HasFocus);
    }

    [Theory]
    [InlineData(5f, 20f, 0f, true)]
    [InlineData(10f, 20f, 4f, true)]
    [InlineData(10f, 20f, -3f, false)]
    [InlineData(10f, 20f, float.NaN, false)]
    public void Focal_point_and_radius(float fx, float fy, float fr, bool hasFocus)
    {
        var b = new RadialGradientBrush(new Point(10, 20), 30, new Point(fx, fy), fr, TwoStops, SpreadMode.Reflect);
        Assert.Equal(hasFocus, b.HasFocus);
        Assert.True(b.FocalRadius >= 0);   // 负值与 NaN 按 0
        Assert.Equal(SpreadMode.Reflect, b.Spread);
    }

    [Fact]
    public void Nan_stop_offsets_take_the_previous_offset()
    {
        var b = new LinearGradientBrush(default, new Point(1, 0), new[]
        {
            new GradientStop(float.NaN, Color.Black), new GradientStop(0.4f, Color.White),
            new GradientStop(float.NaN, Color.Black), new GradientStop(0.2f, Color.White), new GradientStop(1, Color.Black),
        });
        Assert.Equal(new[] { 0f, 0.4f, 0.4f, 0.4f, 1f }, b.Stops.Select(s => s.Offset).ToArray());
    }

    [Fact]
    public void Focal_gradient_does_not_change_the_command_encoding()
    {
        // 画刷是资源表中的对象引用（design/13），带焦点的径向渐变不改变命令的二进制编码，FormatVersion 不变
        byte[] Encode(Brush brush)
        {
            var dc = new DrawingContext();
            dc.FillRect(new Rect(0, 0, 10, 10), brush);
            var list = dc.Finish();
            var r = list.GetReader();
            Assert.True(r.MoveNext());
            Assert.Same(brush, r.GetBrush(r.Read<FillRectCmd>().Paint));
            return list.CommandBytes.ToArray();
        }
        var simple = Encode(new RadialGradientBrush(new Point(5, 5), 5, TwoStops));
        var focal = Encode(new RadialGradientBrush(new Point(5, 5), 5, new Point(3, 4), 1, TwoStops));
        Assert.Equal(simple, focal);
        // FormatVersion 在第九批因 PushLayer 的滤镜字段与 DrawBackdrop 升为 2（与画刷无关）
        Assert.Equal(2, DisplayList.FormatVersion);
    }
}
