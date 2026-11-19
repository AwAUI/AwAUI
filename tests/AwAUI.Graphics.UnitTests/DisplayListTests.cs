using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Graphics.UnitTests;

public class DisplayListTests
{
    [Fact]
    public void Records_commands_in_order_and_reads_payloads_back()
    {
        var dc = new DrawingContext();
        dc.Save();
        dc.Translate(10, 20);
        dc.FillRect(new Rect(0, 0, 5, 5), Color.Parse("#ff0000"));
        dc.Restore();
        var list = dc.Finish();

        var r = list.GetReader();
        Assert.True(r.MoveNext()); Assert.Equal(DrawOp.Save, r.Op);
        Assert.True(r.MoveNext()); Assert.Equal(DrawOp.Translate, r.Op);
        var t = r.Read<TranslateCmd>();
        Assert.Equal(10, t.Dx); Assert.Equal(20, t.Dy);
        Assert.True(r.MoveNext()); Assert.Equal(DrawOp.FillRect, r.Op);
        var f = r.Read<FillRectCmd>();
        Assert.Equal(new Rect(0, 0, 5, 5), f.Rect);
        Assert.True(f.Paint.IsSolid);
        Assert.Equal(Color.Parse("#ff0000"), f.Paint.Color);
        Assert.Equal(DrawFlags.AntiAlias, r.Flags);
        Assert.True(r.MoveNext()); Assert.Equal(DrawOp.Restore, r.Op);
        Assert.False(r.MoveNext());
        Assert.Equal(4, list.CommandCount);
    }

    [Fact]
    public void Bounds_are_conservative_and_follow_transform_and_clip()
    {
        var dc = new DrawingContext();
        dc.Translate(100, 0);
        dc.FillRect(new Rect(0, 0, 10, 10), Color.Black);
        dc.ClipRect(new Rect(0, 0, 5, 5));
        dc.FillRect(new Rect(0, 0, 50, 50), Color.Black);
        var list = dc.Finish();
        Assert.Equal(new Rect(100, 0, 10, 10), list.Bounds);
    }

    [Fact]
    public void Stroke_bounds_include_miter_outset()
    {
        var dc = new DrawingContext();
        dc.StrokeRect(new Rect(10, 10, 10, 10), Color.Black, new Stroke(2, join: StrokeJoin.Miter, miterLimit: 4));
        var b = dc.Finish().Bounds;
        Assert.True(b.Left <= 6 && b.Right >= 24);
    }

    [Fact]
    public void Unbalanced_save_and_layers_are_closed_automatically()
    {
        var dc = new DrawingContext();
        dc.Save();
        dc.PushLayer(new LayerOptions(0.5f));
        dc.FillRect(new Rect(0, 0, 1, 1), Color.Black);
        var list = dc.Finish();
        var ops = Ops(list);
        Assert.Equal(new[] { DrawOp.Save, DrawOp.PushLayer, DrawOp.FillRect, DrawOp.PopLayer, DrawOp.Restore }, ops);
    }

    [Fact]
    public void Mismatched_restore_throws()
    {
        var dc = new DrawingContext();
        dc.PushLayer(new LayerOptions(0.5f));
        Assert.Throws<InvalidOperationException>(() => dc.Restore());
    }

    [Fact]
    public void Invisible_and_degenerate_draws_are_dropped()
    {
        var dc = new DrawingContext();
        dc.FillRect(new Rect(0, 0, 0, 10), Color.Black);
        dc.FillRect(new Rect(0, 0, 10, 10), Color.Transparent);
        dc.FillRect(new Rect(float.NaN, 0, 10, 10), Color.Black);
        dc.StrokeRect(new Rect(0, 0, 10, 10), Color.Black, new Stroke(0));
        dc.Translate(0, 0);
        Assert.True(dc.Finish().IsEmpty);
    }

    [Fact]
    public void Resources_are_deduplicated_by_reference()
    {
        var path = VectorPath.FromEllipse(new Rect(0, 0, 10, 10));
        var stroke = new Stroke(1);
        var dc = new DrawingContext();
        dc.FillPath(path, Color.Black);
        dc.StrokePath(path, Color.Black, stroke);
        dc.StrokePath(path, Color.White, stroke);
        var list = dc.Finish();
        Assert.Equal(2, list.Resources.Count);
    }

    [Fact]
    public void Gradient_brushes_are_stored_as_resources_and_solid_colors_inline()
    {
        var brush = new LinearGradientBrush(new Point(0, 0), new Point(1, 0),
            new[] { new GradientStop(0, Color.Black), new GradientStop(1, Color.White) });
        var dc = new DrawingContext();
        dc.FillRect(new Rect(0, 0, 1, 1), brush);
        var list = dc.Finish();
        var r = list.GetReader();
        r.MoveNext();
        var cmd = r.Read<FillRectCmd>();
        Assert.False(cmd.Paint.IsSolid);
        Assert.Same(brush, r.GetBrush(cmd.Paint));
    }

    [Fact]
    public void Nested_display_list_bounds_are_transformed()
    {
        var inner = new DrawingContext();
        inner.FillRect(new Rect(0, 0, 10, 10), Color.Black);
        var child = inner.Finish();

        var dc = new DrawingContext();
        dc.Transform(Matrix3x2.CreateScale(2));
        dc.DrawDisplayList(child);
        Assert.Equal(new Rect(0, 0, 20, 20), dc.Finish().Bounds);
    }

    [Fact]
    public void Identical_recordings_have_identical_content_hash()
    {
        static DisplayList Make()
        {
            var dc = new DrawingContext();
            dc.FillRoundRect(new RoundRect(new Rect(0, 0, 10, 10), 3), Color.Parse("#336699"));
            return dc.Finish();
        }
        Assert.Equal(Make().ContentHash, Make().ContentHash);
    }

    [Fact]
    public void Every_payload_is_four_byte_aligned()
    {
        unsafe
        {
            Assert.Equal(0, sizeof(PaintData) % 4);
            Assert.Equal(0, sizeof(FillRectCmd) % 4);
            Assert.Equal(0, sizeof(FillRoundRectCmd) % 4);
            Assert.Equal(0, sizeof(PushLayerCmd) % 4);
            Assert.Equal(0, sizeof(BoxShadowCmd) % 4);
            Assert.Equal(0, sizeof(AnnotationCmd) % 4);
        }
    }

    static DrawOp[] Ops(DisplayList list)
    {
        var result = new List<DrawOp>();
        var r = list.GetReader();
        while (r.MoveNext()) result.Add(r.Op);
        return result.ToArray();
    }
}
