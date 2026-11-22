using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 滚动锚定（design/09「滚动」）：内容在视口内锚点上方插入 / 删除时保持锚点的视口位置；用户输入与程序滚动不被纠正；
/// 锚点被移除时重新选择；嵌套容器各自独立；开关关闭后不调整。
/// </summary>
public class ScrollAnchorTests
{
    const string Css =
        ".scroller{width:200px;height:100px;overflow-y:auto;overflow-x:hidden;}" +
        ".row{height:20px;}" +
        ".inner{width:150px;height:100px;overflow-y:auto;overflow-x:hidden;}" +
        ".tall{height:800px;}";

    /// <summary>按 Key 维护的行列表；改列表并推进 <see cref="State{T}"/> 触发重建。</summary>
    sealed class RowList
    {
        public readonly List<string> Keys = new();
        readonly State<int> _rev = new(0);

        public RowList(int count)
        {
            for (int i = 0; i < count; i++) Keys.Add("r" + i);
        }

        public void Insert(int index, string key)
        {
            Keys.Insert(index, key);
            _rev.Value++;
        }

        public void Remove(string key)
        {
            Keys.Remove(key);
            _rev.Value++;
        }

        public NodeList Build(string cls = "row")
        {
            _ = _rev.Value;
            var list = new NodeList();
            foreach (var k in Keys)
                list.Add(new Div { Key = k, Id = "row-" + k, Class = cls });
            return list;
        }
    }

    static Node Scroller(RowList rows) => new Lambda
    {
        Render = () => new Div { Id = "scroller", Class = "scroller", Children = rows.Build() },
    };

    static RawPointerEvent Wheel(float deltaY) =>
        RawPointerEvent.CreateWheel(new Point(100, 50), 0, deltaY, WheelDeltaMode.Pixel, false,
            ScrollPhase.None, ScrollPhase.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero);

    static (PipelineHarness Harness, ScrollController Controller, RowList Rows) Setup(int count = 50, double offset = 200)
    {
        var rows = new RowList(count);
        var h = new PipelineHarness(Scroller(rows), Css);
        var c = h.El("scroller").ScrollController!;
        c.JumpTo(0, offset);
        h.Frame();
        return (h, c, rows);
    }

    [Fact]
    public void Inserting_content_above_the_anchor_increases_the_offset_and_keeps_it_in_place()
    {
        var (h, c, rows) = Setup();
        var viewport = h.Box("scroller");
        double anchorTop = h.Box("row-r10").Top;
        Assert.Equal(viewport.Top, anchorTop, 0.5);
        Assert.Equal(200, c.OffsetY, 0.5);

        rows.Insert(0, "new");
        h.Frame();

        Assert.Equal(220, c.OffsetY, 0.5);
        // 锚点的边框盒在视口内的位置不变
        Assert.Equal(anchorTop, h.Box("row-r10").Top, 0.5);
    }

    [Fact]
    public void Removing_content_above_the_anchor_decreases_the_offset()
    {
        var (h, c, rows) = Setup();
        double anchorTop = h.Box("row-r10").Top;

        rows.Remove("r0");
        h.Frame();

        Assert.Equal(180, c.OffsetY, 0.5);
        Assert.Equal(anchorTop, h.Box("row-r10").Top, 0.5);
    }

    [Fact]
    public void Programmatic_jump_in_the_same_frame_is_not_corrected()
    {
        var (h, c, rows) = Setup();
        rows.Insert(0, "new");

        // 内容插入与 ScrollTo 在同一帧：以程序偏移为准，不叠加锚定校正
        c.ScrollTo(0, 200, ScrollAnimation.Instant);
        h.Frame();

        Assert.Equal(200, c.OffsetY, 0.5);
    }

    [Fact]
    public void User_scroll_in_the_same_frame_is_not_corrected()
    {
        var (h, c, rows) = Setup();
        h.Pipeline.SmoothScrolling = false;
        rows.Insert(0, "new");

        h.Root.Input.DispatchPointer(Wheel(-20)); // 向下滚 20
        h.Frame();

        // 只有用户滚动生效；没有把插入的 20 再加到偏移上
        Assert.Equal(220, c.OffsetY, 0.5);
    }

    [Fact]
    public void Removing_the_anchor_reselects_without_jumping_to_zero()
    {
        var (h, c, rows) = Setup();
        Assert.Equal(200, c.OffsetY, 0.5);

        rows.Remove("r10");
        h.Frame();

        Assert.Equal(200, c.OffsetY, 0.5);
        // 新的第一个可见元素仍在视口顶部
        Assert.Equal(h.Box("scroller").Top, h.Box("row-r11").Top, 0.5);
    }

    [Fact]
    public void Nested_containers_are_anchored_independently()
    {
        var rows = new RowList(50);
        var extra = new State<int>(0);
        var h = new PipelineHarness(new Lambda
        {
            Render = () =>
            {
                bool withExtra = extra.Value > 0;
                var children = new NodeList();
                children.Add(new Div { Key = "top", Id = "top", Class = "row" });
                if (withExtra) children.Add(new Div { Key = "extra", Id = "extra", Class = "row" });
                children.Add(new Div { Key = "inner", Id = "inner", Class = "inner", Children = rows.Build() });
                children.Add(new Div { Key = "tail", Id = "tail", Class = "tall" });
                return new Div { Id = "outer", Class = "scroller", Children = children };
            },
        }, Css);
        var outer = h.El("outer").ScrollController!;
        var inner = h.El("inner").ScrollController!;
        outer.JumpTo(0, 20);
        inner.JumpTo(0, 50);
        h.Frame();
        Assert.Equal(20, outer.OffsetY, 0.5);
        Assert.Equal(50, inner.OffsetY, 0.5);

        // 外层在锚点（内层滚动容器）上方插入内容：只校正外层，内层偏移不变
        extra.Value = 1;
        h.Frame();

        Assert.Equal(40, outer.OffsetY, 0.5);
        Assert.Equal(50, inner.OffsetY, 0.5);
    }

    [Fact]
    public void Disabling_scroll_anchoring_leaves_the_offset_unchanged()
    {
        var (h, c, rows) = Setup();
        c.ScrollAnchoring = false;

        rows.Insert(0, "new");
        h.Frame();

        Assert.Equal(200, c.OffsetY, 0.5);
    }

    [Fact]
    public void Inserting_content_below_the_anchor_does_not_change_the_offset()
    {
        var (h, c, rows) = Setup();
        rows.Insert(rows.Keys.Count, "tail");
        h.Frame();

        Assert.Equal(200, c.OffsetY, 0.5);
    }
}
