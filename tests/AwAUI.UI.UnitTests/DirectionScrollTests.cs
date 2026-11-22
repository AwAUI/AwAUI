using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 横向滚动的方向语义：LTR 的 <see cref="ScrollController.OffsetX"/> 从内容左边缘量起，RTL 从右边缘量起、向左增大。
/// 几何上的平移、滚轮方向与方向切换都要跟随容器使用的 <c>direction</c>。
/// </summary>
public class DirectionScrollTests
{
    static Div Screen(bool rtl)
    {
        var content = new Div { Id = "content", Style = "width:1000px;height:100px;" };
        var scroll = new Div
        {
            Id = "scroll",
            TabIndex = 0,
            Style = "width:200px;height:100px;overflow-x:auto;overflow-y:hidden;",
            Children = [content],
        };
        if (rtl) scroll.Attributes["dir"] = "rtl";
        return scroll;
    }

    static RawPointerEvent Wheel(float deltaX) =>
        RawPointerEvent.CreateWheel(new Point(100, 50), deltaX, 0, WheelDeltaMode.Pixel, true,
            ScrollPhase.None, ScrollPhase.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero);

    [Fact]
    public void Rtl_content_starts_at_the_right_edge_and_scrolls_toward_the_inline_end()
    {
        var h = new PipelineHarness(Screen(rtl: true));
        var viewport = h.El("scroll");
        var c = viewport.ScrollController!;
        var vp = viewport.BoundingRect;

        Assert.Equal(0, c.OffsetX, 0.01);
        Assert.Equal(vp.Right, h.Box("content").Right, 0.5);
        Assert.Equal(800, c.MaxOffsetX, 0.5);

        // 滚轮正值 = 向左滚动；RTL 下就是向行内末尾（左）滚动，偏移增大
        h.Pipeline.SmoothScrolling = false;
        h.Root.Input.DispatchPointer(Wheel(+60));
        h.Frame();
        Assert.Equal(60, c.OffsetX, 0.5);
        Assert.Equal(vp.Right + 60, h.Box("content").Right, 0.5);

        // 到逻辑末尾：内容的左边缘与视口左边缘对齐
        c.JumpTo(c.MaxOffsetX, 0);
        h.Frame();
        Assert.Equal(vp.Left, h.Box("content").Left, 0.5);
    }

    [Fact]
    public void Ltr_content_starts_at_the_left_edge_and_uses_the_same_public_offsets()
    {
        var h = new PipelineHarness(Screen(rtl: false));
        var viewport = h.El("scroll");
        var c = viewport.ScrollController!;
        var vp = viewport.BoundingRect;

        Assert.Equal(vp.Left, h.Box("content").Left, 0.5);

        h.Pipeline.SmoothScrolling = false;
        h.Root.Input.DispatchPointer(Wheel(-60));
        h.Frame();
        Assert.Equal(60, c.OffsetX, 0.5);

        c.JumpTo(c.MaxOffsetX, 0);
        h.Frame();
        Assert.Equal(vp.Right, h.Box("content").Right, 0.5);
    }

    [Fact]
    public void Arrow_keys_follow_the_inline_direction()
    {
        var h = new PipelineHarness(Screen(rtl: true));
        var viewport = h.El("scroll");
        var c = viewport.ScrollController!;
        Assert.True(viewport.Focus());
        h.Pipeline.SmoothScrolling = false;

        static void Key(PipelineHarness h, PhysicalKey key)
        {
            h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, key, KeyModifiers.None, false, 0, TimeSpan.Zero));
            h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Up, key, KeyModifiers.None, false, 0, TimeSpan.Zero));
            h.Frame();
        }

        // RTL 起点在右：ArrowLeft（物理向左）向行内末尾滚动，ArrowRight 在起点无处可去
        Key(h, PhysicalKey.ArrowRight);
        Assert.Equal(0, c.OffsetX, 0.01);
        Key(h, PhysicalKey.ArrowLeft);
        Assert.Equal(40, c.OffsetX, 0.5);
    }

    [Fact]
    public void Changing_the_direction_reapplies_the_same_logical_offset_from_the_new_edge()
    {
        var h = new PipelineHarness(Screen(rtl: false));
        var viewport = h.El("scroll");
        var c = viewport.ScrollController!;
        var vp = viewport.BoundingRect;
        c.JumpTo(100, 0);
        h.Frame();
        Assert.Equal(vp.Left - 100, h.Box("content").Left, 0.5);

        // 同一个逻辑偏移切换到 RTL 后从右边缘量起
        viewport.SetAttributeOverride("dir", "rtl");
        h.Frame();
        Assert.Equal(100, c.OffsetX, 0.01);
        Assert.Equal(vp.Right + 100, h.Box("content").Right, 0.5);
    }
}
