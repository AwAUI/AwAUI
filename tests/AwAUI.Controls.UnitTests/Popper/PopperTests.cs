using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests.Popper;

/// <summary>移植自 Floating UI 的单元测试（computeCoordsFromPlacement、computePosition、各中间件），外加 Radix Popper 层面的组合。</summary>
public class PopperTests
{
    static readonly Rect Reference = new(0, 0, 100, 100);
    static readonly Size Floating = new(50, 50);
    static readonly PopperEnvironment Env = new(new Rect(0, 0, 1000, 1000));

    [Theory]
    [InlineData("bottom", 25, 100)]
    [InlineData("bottom-start", 0, 100)]
    [InlineData("bottom-end", 50, 100)]
    [InlineData("top", 25, -50)]
    [InlineData("top-start", 0, -50)]
    [InlineData("top-end", 50, -50)]
    [InlineData("right", 100, 25)]
    [InlineData("right-start", 100, 0)]
    [InlineData("right-end", 100, 50)]
    [InlineData("left", -50, 25)]
    [InlineData("left-start", -50, 0)]
    [InlineData("left-end", -50, 50)]
    public void ComputeCoordsFromPlacement_matches_floating_ui(string placement, float x, float y)
    {
        var p = PopperEngine.ComputeCoordsFromPlacement(new ElementRects(Reference, Floating), Placement.Parse(placement), rtl: false);
        Assert.Equal(new Point(x, y), p);
    }

    [Theory]
    [InlineData("bottom-start", 50, 100)]
    [InlineData("bottom-end", 0, 100)]
    [InlineData("top-start", 50, -50)]
    [InlineData("right-start", 100, 0)]   // 左 / 右边不受 RTL 影响
    public void Rtl_mirrors_start_and_end_on_vertical_sides(string placement, float x, float y)
    {
        var p = PopperEngine.ComputeCoordsFromPlacement(new ElementRects(Reference, Floating), Placement.Parse(placement), rtl: true);
        Assert.Equal(new Point(x, y), p);
    }

    [Theory]
    [InlineData(false, -50f)] // LTR：inline-start 在左
    [InlineData(true, 100f)]  // RTL：inline-start 在右
    public void Logical_inline_side_resolves_against_the_direction(bool rtl, float x)
    {
        var env = new PopperEnvironment(new Rect(0, 0, 1000, 1000)) { Rtl = rtl };
        var start = PopperEngine.ComputePosition(Reference, Floating, env, new Placement(Side.InlineStart));
        Assert.Equal(x, start.X);
        Assert.Equal(rtl ? Side.Right : Side.Left, start.Placement.Side);

        var end = PopperEngine.ComputePosition(Reference, Floating, env, new Placement(Side.InlineEnd));
        Assert.Equal(x == -50f ? 100f : -50f, end.X);
        Assert.Equal(rtl ? Side.Left : Side.Right, end.Placement.Side);

        // 直接调用基本坐标函数时同样解析逻辑边
        var p = PopperEngine.ComputeCoordsFromPlacement(new ElementRects(Reference, Floating), Placement.Parse("inline-start"), rtl);
        Assert.Equal(x, p.X);
    }

    [Fact]
    public void ComputePosition_without_middleware()
    {
        var r = PopperEngine.ComputePosition(Reference, Floating, Env, Placement.Bottom);
        Assert.Equal(25, r.X);
        Assert.Equal(100, r.Y);
        Assert.Equal(Placement.Bottom, r.Placement);
        Assert.Equal(0, r.ResetCount);
    }

    [Fact]
    public void Offset_number_and_axes()
    {
        var r = PopperEngine.ComputePosition(Reference, Floating, Env, Placement.Bottom, new[] { new OffsetMiddleware(10) });
        Assert.Equal((25f, 110f), (r.X, r.Y));
        r = PopperEngine.ComputePosition(Reference, Floating, Env, Placement.Bottom, new[] { new OffsetMiddleware(10) { CrossAxis = 5 } });
        Assert.Equal((30f, 110f), (r.X, r.Y));
        // alignmentAxis 覆盖 crossAxis，并在 end 对齐时取反
        r = PopperEngine.ComputePosition(Reference, Floating, Env, Placement.TopEnd, new[] { new OffsetMiddleware(10) { CrossAxis = 99, AlignmentAxis = 5 } });
        Assert.Equal((45f, -60f), (r.X, r.Y));
        // 基本方位忽略 alignmentAxis
        r = PopperEngine.ComputePosition(Reference, Floating, Env, Placement.Left, new[] { new OffsetMiddleware(10) { AlignmentAxis = 5 } });
        Assert.Equal((-60f, 25f), (r.X, r.Y));
    }

    [Fact]
    public void DetectOverflow_reports_each_side()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 200, 200));
        var r = PopperEngine.ComputePosition(new Rect(180, 180, 10, 10), new Size(50, 50), env, Placement.Bottom, new[] { new Probe() });
        var o = Probe.Last;
        // 浮层在 (160, 190)，50×50：右溢出 10，下溢出 40
        Assert.Equal(new SideValues(-190, 10, 40, -160), o);
        Assert.Equal(160, r.X);
    }

    sealed class Probe : PopperMiddleware
    {
        public static SideValues Last;

        public Probe() : base("probe") { }

        public override PopperMiddlewareResult Compute(PopperState state)
        {
            Last = state.DetectOverflow(new DetectOverflowOptions());
            return PopperMiddlewareResult.None;
        }
    }

    [Fact]
    public void Flip_moves_to_opposite_side_when_main_axis_overflows()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 500, 500));
        var r = PopperEngine.ComputePosition(new Rect(100, 460, 100, 30), new Size(80, 100), env, Placement.Bottom, new[] { new FlipMiddleware() });
        Assert.Equal(Placement.Top, r.Placement);
        Assert.Equal(360, r.Y);
        Assert.Equal(1, r.Data.Flip!.Index);
    }

    [Fact]
    public void Flip_keeps_initial_placement_when_nothing_fits_better()
    {
        // 上下都放不下：bestFit 选总溢出最少的一边
        var env = new PopperEnvironment(new Rect(0, 0, 500, 300));
        var r = PopperEngine.ComputePosition(new Rect(100, 120, 100, 40), new Size(80, 200), env, Placement.Bottom, new[] { new FlipMiddleware() });
        Assert.Equal(Placement.Bottom, r.Placement);   // 下溢出 60 < 上溢出 80
        r = PopperEngine.ComputePosition(new Rect(100, 140, 100, 40), new Size(80, 200), env, Placement.Bottom, new[] { new FlipMiddleware() });
        Assert.Equal(Placement.Top, r.Placement);      // 下溢出 80 > 上溢出 60
    }

    [Fact]
    public void Shift_clamps_into_boundary_with_padding()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 500, 500));
        var r = PopperEngine.ComputePosition(new Rect(0, 100, 20, 20), new Size(100, 50), env, Placement.Bottom,
            new[] { new ShiftMiddleware { Overflow = new DetectOverflowOptions { Padding = 5 } } });
        Assert.Equal(5, r.X);
        Assert.Equal(45, r.Data.Shift!.X);
        Assert.True(r.Data.Shift.EnabledX);
        Assert.False(r.Data.Shift.EnabledY);
    }

    [Fact]
    public void LimitShift_keeps_floating_attached_to_reference()
    {
        // 锚点在视口左边之外：shift 会把浮层推到 x = 0，limitShift 限制它不超过锚点右缘
        var env = new PopperEnvironment(new Rect(0, 0, 500, 500));
        var anchor = new Rect(-200, 100, 50, 20);
        var free = PopperEngine.ComputePosition(anchor, new Size(100, 50), env, Placement.Bottom, new[] { new ShiftMiddleware() });
        Assert.Equal(0, free.X);
        var limited = PopperEngine.ComputePosition(anchor, new Size(100, 50), env, Placement.Bottom, new[] { new ShiftMiddleware { Limiter = new LimitShift() } });
        Assert.Equal(-150, limited.X);   // 浮层左缘不超过锚点右缘
    }

    [Fact]
    public void Arrow_centers_on_reference_and_reports_center_offset()
    {
        var r = PopperEngine.ComputePosition(Reference, new Size(200, 50), Env, Placement.Bottom, new PopperMiddleware[] { new ArrowMiddleware(new Size(10, 5)) });
        // 浮层 x = -50，箭头中心对准锚点中心 50 → 箭头左缘在浮层内 95
        Assert.Equal(95, r.Data.Arrow!.X);
        Assert.Null(r.Data.Arrow.Y);
        Assert.Equal(0, r.Data.Arrow.CenterOffset);
    }

    [Fact]
    public void Arrow_alignment_offset_shifts_floating_when_arrow_cannot_reach_reference()
    {
        // 小锚点 + start 对齐 + 箭头内边距大于锚点：浮层被推动，使箭头仍落在锚点上
        var r = PopperEngine.ComputePosition(new Rect(100, 100, 10, 10), new Size(200, 50), Env, Placement.BottomStart,
            new PopperMiddleware[] { new ArrowMiddleware(new Size(10, 5)) { Padding = 20 } });
        Assert.NotNull(r.Data.Arrow!.AlignmentOffset);
        Assert.Equal(1, r.ResetCount);
        Assert.Equal(80, r.X);
    }

    [Fact]
    public void Hide_detects_reference_scrolled_out_of_its_clipping_ancestor()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 1000, 1000)) { ReferenceClippingRect = new Rect(0, 0, 300, 300) };
        var hidden = PopperEngine.ComputePosition(new Rect(50, 320, 40, 20), Floating, env, Placement.Bottom, new[] { new HideMiddleware() });
        Assert.True(hidden.Data.Hide!.ReferenceHidden);
        var visible = PopperEngine.ComputePosition(new Rect(50, 290, 40, 20), Floating, env, Placement.Bottom, new[] { new HideMiddleware() });
        Assert.False(visible.Data.Hide!.ReferenceHidden);
        var escaped = PopperEngine.ComputePosition(new Rect(50, 280, 40, 20), Floating, env, Placement.Bottom, new[] { new HideMiddleware(HideStrategy.Escaped) });
        Assert.True(escaped.Data.Hide!.Escaped);
    }

    [Fact]
    public void AutoPlacement_picks_side_with_most_space()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 1000, 400));
        var r = PopperEngine.ComputePosition(new Rect(50, 150, 40, 40), new Size(200, 100), env, Placement.Top, new[] { new AutoPlacementMiddleware() });
        Assert.Equal(Placement.Right, r.Placement);
    }

    [Fact]
    public void Size_reports_available_space_and_resets_with_new_rects()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 500, 500));
        int calls = 0;
        var r = PopperEngine.ComputePosition(new Rect(100, 300, 100, 20), new Size(100, 400), env, Placement.Bottom, new[]
        {
            new SizeMiddleware
            {
                Apply = a =>
                {
                    calls++;
                    return new Size(a.State.Rects.Floating.Width, Math.Min(400, a.AvailableHeight));
                },
            },
        });
        Assert.Equal(180, r.Data.Size!.AvailableHeight);
        Assert.Equal(180, r.Rects.Floating.Height);
        Assert.Equal(2, calls);   // 一次重置后尺寸稳定
        Assert.Equal(1, r.ResetCount);
    }

    sealed class AlwaysReset : PopperMiddleware
    {
        public int Calls;

        public AlwaysReset() : base("evil") { }

        public override PopperMiddlewareResult Compute(PopperState state)
        {
            Calls++;
            return new PopperMiddlewareResult { ResetPlacement = state.Placement.Opposite };
        }
    }

    [Fact]
    public void Reset_loop_is_bounded()
    {
        var evil = new AlwaysReset();
        var r = PopperEngine.ComputePosition(Reference, Floating, Env, Placement.Bottom, new PopperMiddleware[] { evil, new OffsetMiddleware(1) });
        Assert.Equal(PopperEngine.MaxResets, r.ResetCount);
        Assert.Equal(PopperEngine.MaxResets + 1, evil.Calls);
    }

    [Fact]
    public void Placement_parse_and_format_round_trip()
    {
        foreach (var p in Placement.All)
            Assert.Equal(p, Placement.Parse(p.ToString()));
        Assert.Equal(12, Placement.All.Count);
        Assert.False(Placement.TryParse("bottom-middle", out _));
        Assert.False(Placement.TryParse("", out _));
        Assert.False(Placement.TryParse(null, out _));
        Assert.Equal("top-end", Placement.BottomStart.Opposite.OppositeAlignment.ToString());
    }

    // ------------------------------------------------------------ Radix Popper

    [Fact]
    public void PopperLayout_default_is_bottom_center_with_css_variables()
    {
        var r = PopperLayout.Compute(new Rect(100, 100, 80, 30), new Size(120, 60), new PopperEnvironment(new Rect(0, 0, 800, 600)));
        Assert.Equal(80, r.X);
        Assert.Equal(130, r.Y);
        Assert.Equal("bottom", r.DataSide);
        Assert.Equal("center", r.DataAlign);
        Assert.True(r.CannotCenterArrow);   // 没有箭头
        var vars = r.CssVariables.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("80px", vars["--aw-popper-anchor-width"]);
        Assert.Equal("30px", vars["--aw-popper-anchor-height"]);
        Assert.Equal("800px", vars["--aw-popper-available-width"]);   // shift 启用了 x 轴：可用宽度 = 整个裁剪宽度
        Assert.Equal("470px", vars["--aw-popper-available-height"]);
        Assert.Equal("50% 0px", vars["--aw-popper-transform-origin"]);
    }

    [Fact]
    public void PopperLayout_side_offset_includes_arrow_height_and_arrow_is_centered()
    {
        var opts = new PopperOptions { Side = Side.Top, SideOffset = 4, ArrowSize = new Size(10, 5) };
        var r = PopperLayout.Compute(new Rect(100, 300, 80, 30), new Size(120, 60), new PopperEnvironment(new Rect(0, 0, 800, 600)), opts);
        Assert.Equal(300 - 60 - 9, r.Y);
        Assert.Equal("top", r.DataSide);
        Assert.False(r.CannotCenterArrow);
        Assert.Equal(55, r.ArrowX);   // 浮层 x = 80，锚点中心 140 → 箭头左缘 140 - 5 - 80
        Assert.Equal(Side.Bottom, r.ArrowBaseSide);
        Assert.Equal("60px 65px", r.TransformOrigin);   // 箭头中心 x，浮层高度 + 箭头高度
    }

    [Fact]
    public void PopperLayout_flips_when_avoiding_collisions_and_not_otherwise()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 800, 600));
        var anchor = new Rect(100, 560, 80, 30);
        Assert.Equal("top", PopperLayout.Compute(anchor, new Size(120, 60), env).DataSide);
        Assert.Equal("bottom", PopperLayout.Compute(anchor, new Size(120, 60), env, new PopperOptions { AvoidCollisions = false }).DataSide);
    }

    [Fact]
    public void PopperLayout_sticky_always_detaches_from_anchor()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 800, 600));
        var anchor = new Rect(-300, 100, 80, 30);
        Assert.Equal(-220, PopperLayout.Compute(anchor, new Size(120, 60), env).X);   // partial：贴住锚点右缘
        Assert.Equal(0, PopperLayout.Compute(anchor, new Size(120, 60), env, new PopperOptions { Sticky = PopperSticky.Always }).X);
    }

    [Fact]
    public void PopperLayout_rtl_start_alignment_uses_right_edge()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 800, 600)) { Rtl = true };
        var r = PopperLayout.Compute(new Rect(300, 100, 80, 30), new Size(120, 60), env, new PopperOptions { Align = PopperAlign.Start });
        Assert.Equal(260, r.X);   // 浮层右缘对齐锚点右缘
        Assert.Equal("start", r.DataAlign);
    }

    [Fact]
    public void PopperLayout_hide_when_detached()
    {
        var env = new PopperEnvironment(new Rect(0, 0, 800, 600)) { ReferenceClippingRect = new Rect(0, 0, 400, 200) };
        var opts = new PopperOptions { HideWhenDetached = true };
        Assert.True(PopperLayout.Compute(new Rect(100, 250, 80, 30), new Size(120, 60), env, opts).ReferenceHidden);
        Assert.False(PopperLayout.Compute(new Rect(100, 150, 80, 30), new Size(120, 60), env, opts).ReferenceHidden);
    }

    [Fact]
    public void PopperLayout_rounds_by_device_pixel_ratio()
    {
        var r = PopperLayout.Compute(new Rect(100.3f, 100, 81, 30), new Size(120, 60), new PopperEnvironment(new Rect(0, 0, 800, 600)),
            new PopperOptions { DevicePixelRatio = 2 });
        Assert.Equal(81f, r.X);   // 80.8 × 2 = 161.6 → 162 → 81
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-50f)]
    public void PopperLayout_sanitizes_non_finite_input(float bad)
    {
        var r = PopperLayout.Compute(new Rect(bad, 100, bad, 30), new Size(bad, 60), new PopperEnvironment(new Rect(0, 0, 800, 600)),
            new PopperOptions { SideOffset = bad, AlignOffset = bad, ArrowSize = new Size(10, 5) });
        Assert.False(float.IsNaN(r.X) || float.IsInfinity(r.X));
        Assert.False(float.IsNaN(r.Y) || float.IsInfinity(r.Y));
    }
}
