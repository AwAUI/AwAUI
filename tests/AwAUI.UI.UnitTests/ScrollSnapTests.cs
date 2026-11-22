using System.Numerics;
using AwAUI.Headless;
using AwAUI.Platform;
using static AwAUI.UI.UnitTests.ScrollSupport;

namespace AwAUI.UI.UnitTests;

/// <summary>固定行高的纵向滚动内容（可选 class，用来附加 scroll-snap 样式）。</summary>
internal sealed partial class SnapVerticalApp : Component
{
    public int Rows { get; set; } = 6;
    public string Class { get; set; } = "scroller";

    protected override Node Build()
    {
        var rows = new NodeList();
        for (int i = 0; i < Rows; i++)
            rows.Add(new Div { Key = i, Id = "row-" + i, Class = "row", Children = ["Row " + i] });
        return new Div { Id = "scroller", Class = Class, Children = rows };
    }
}

/// <summary>横向排列的吸附区域。</summary>
internal sealed partial class SnapHorizontalApp : Component
{
    protected override Node Build()
    {
        var items = new NodeList();
        for (int i = 0; i < 4; i++)
            items.Add(new Div { Key = i, Id = "item-" + i, Class = "hitem", Children = ["Item " + i] });
        return new Div
        {
            Id = "scroller",
            Class = "hscroller",
            Children = [new Div { Key = "track", Class = "track", Children = items }],
        };
    }
}

/// <summary>滚动容器里有一个粘性子元素（纯滚动快路径的保守检查）。</summary>
internal sealed partial class SnapStickyApp : Component
{
    protected override Node Build()
    {
        var rows = new NodeList();
        rows.Add(new Div { Key = "sticky", Id = "sticky", Style = "position:sticky;top:0;height:12px;background-color:#0ea5e9" });
        for (int i = 0; i < 20; i++)
            rows.Add(new Div { Key = i, Id = "row-" + i, Class = "row", Children = ["Row " + i] });
        return new Div { Id = "scroller", Class = "scroller", Children = rows };
    }
}

/// <summary>记录嵌套滚动钩子的调用，并按需消费一部分位移。</summary>
internal sealed class RecordingConnection : NestedScrollConnection
{
    public Vector2 PreConsume;

    public readonly List<(Vector2 Available, ScrollSource Source)> PreScrollCalls = new();
    public readonly List<(Vector2 Consumed, Vector2 Available, ScrollSource Source)> PostScrollCalls = new();
    public readonly List<Vector2> PreFlingCalls = new();
    public readonly List<(Vector2 Consumed, Vector2 Available)> PostFlingCalls = new();

    public override Vector2 OnPreScroll(Vector2 available, ScrollSource source)
    {
        PreScrollCalls.Add((available, source));
        return PreConsume;
    }

    public override Vector2 OnPostScroll(Vector2 consumed, Vector2 available, ScrollSource source)
    {
        PostScrollCalls.Add((consumed, available, source));
        return default;
    }

    public override Vector2 OnPreFling(Vector2 velocity)
    {
        PreFlingCalls.Add(velocity);
        return default;
    }

    public override Vector2 OnPostFling(Vector2 consumed, Vector2 available)
    {
        PostFlingCalls.Add((consumed, available));
        return default;
    }
}

/// <summary>scroll-snap-* 与 NestedScrollConnection（design/21「滚动物理」「嵌套滚动」）。</summary>
public class ScrollSnapTests
{
    const string BaseCss = """
        :root { font-size: 13px; background-color: #f8fafc; }
        .scroller { position: absolute; left: 20px; top: 30px; width: 200px; height: 150px; overflow: auto;
                    background-color: #ffffff; border: 2px solid #94a3b8; }
        .row { height: 100px; border-bottom: 1px solid #e2e8f0; background-color: #eef2ff; }
        .hscroller { position: absolute; left: 20px; top: 30px; width: 200px; height: 150px; overflow-x: auto; overflow-y: hidden;
                     background-color: #ffffff; border: 2px solid #94a3b8; scroll-snap-type: x mandatory; }
        .track { display: flex; width: 800px; }
        .hitem { width: 200px; height: 100px; flex: none; background-color: #eef2ff; border-right: 1px solid #e2e8f0;
                 scroll-snap-align: start; }
        """;

    static readonly Point Inside = new(100, 100);

    static string Css(string snap) => BaseCss + "\n.scroller { " + snap + " }\n";

    [Theory]
    [InlineData("start", 1.3f, 100)]
    [InlineData("center", 1.8f, 177)]
    [InlineData("end", 1.6f, 154)]
    public void Wheel_snaps_to_the_nearest_start_center_or_end_point(string align, float notches, double expected)
    {
        var css = Css($"scroll-snap-type: y mandatory;") + $"\n.row {{ scroll-snap-align: {align}; }}\n";
        var (host, window, clock) = Create(new SnapVerticalApp(), css: css);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        window.WheelNotches(Inside, notches);
        Settle(host, clock);
        Assert.Equal(expected, c.Offset.Y, 3);
        Assert.False(c.IsScrolling);
    }

    [Fact]
    public void Mandatory_always_snaps_but_proximity_only_within_half_the_viewport()
    {
        // 行高 200：相邻吸附点之间的一半距离是 100，超过 proximity 的 0.5 × 146 = 73
        const string rowsCss = ".row { height: 200px; }";

        var proximityCss = Css("scroll-snap-type: y proximity;") + rowsCss + "\n.row { scroll-snap-align: start; }\n";
        var (proximityHost, proximityWindow, proximityClock) = Create(new SnapVerticalApp { Rows = 4 }, css: proximityCss);
        var proximity = proximityHost.Root.RootElement.ById("scroller").ScrollController;
        proximityWindow.WheelNotches(Inside, 1.2f); // 目标 120：最近的吸附点 0（120）与 200（80），80 > 73
        Settle(proximityHost, proximityClock);
        Assert.Equal(120, proximity.Offset.Y, 3);

        var mandatoryCss = Css("scroll-snap-type: y mandatory;") + rowsCss + "\n.row { scroll-snap-align: start; }\n";
        var (mandatoryHost, mandatoryWindow, mandatoryClock) = Create(new SnapVerticalApp { Rows = 4 }, css: mandatoryCss);
        var mandatory = mandatoryHost.Root.RootElement.ById("scroller").ScrollController;
        mandatoryWindow.WheelNotches(Inside, 1.2f);
        Settle(mandatoryHost, mandatoryClock);
        Assert.Equal(200, mandatory.Offset.Y, 3);
    }

    [Fact]
    public void Snap_stop_always_stops_a_fling_at_the_first_crossed_snap_point()
    {
        static double Fling(bool always)
        {
            var css = Css("scroll-snap-type: y mandatory;")
                      + "\n.row { scroll-snap-align: start; scroll-snap-stop: " + (always ? "always" : "normal") + "; }\n";
            var (host, window, clock) = Create(new SnapVerticalApp(), css: css);
            host.Pipeline.TouchScrollPhysics = new ClampingPhysics();
            var c = host.Root.RootElement.ById("scroller").ScrollController;
            double t = 0;
            var p = Inside;
            Pointer(window, RawPointerEventType.Down, p, PointerType.Touch, 11, PointerButtons.Left, PointerButtons.Left, t);
            for (int i = 0; i < 8; i++)
            {
                t += 10;
                p += new Point(0, -20);
                Pointer(window, RawPointerEventType.Move, p, PointerType.Touch, 11, buttons: PointerButtons.Left, ms: t);
            }
            clock.Advance(TimeSpan.FromMilliseconds(t));
            Pointer(window, RawPointerEventType.Up, p, PointerType.Touch, 11, PointerButtons.Left, ms: t + 1);
            Settle(host, clock);
            return c.Offset.Y;
        }

        // 拖动结束于约 140；强惯性原本会冲过 200 / 300 / 400（最后钳制到 454）。always 截在第一个越过的点 200。
        Assert.Equal(200, Fling(always: true), 3);
        Assert.True(Fling(always: false) > 300, "without snap-stop the fling should travel well past the first snap point");
    }

    [Fact]
    public void Horizontal_wheel_snaps_and_none_does_not_snap()
    {
        var (host, window, clock) = Create(new SnapHorizontalApp(), css: BaseCss);
        var c = host.Root.RootElement.ById("scroller").ScrollController;
        Assert.True(c.MaxOffsetX > 0);
        window.WheelNotches(Inside, 1.3f, modifiers: KeyModifiers.Shift); // 130 px 横向
        Settle(host, clock);
        Assert.Equal(200, c.Offset.X, 3);

        // scroll-snap-type: none（没有该声明）：滚到哪停在哪
        var (plainHost, plainWindow, plainClock) = Create(new SnapVerticalApp(), css: BaseCss);
        var plain = plainHost.Root.RootElement.ById("scroller").ScrollController;
        plainWindow.WheelNotches(Inside, 1.3f);
        Settle(plainHost, plainClock);
        Assert.Equal(130, plain.Offset.Y, 3);
    }

    [Fact]
    public void Nested_connection_pre_and_post_scroll_deltas_for_wheel_and_touch()
    {
        // 滚轮：pre 先消费 20，剩余 80 交给容器；post 看到容器实际消费的 80
        var wheel = new RecordingConnection { PreConsume = new Vector2(0, 20) };
        var (wheelHost, wheelWindow, wheelClock) = Create(new SnapVerticalApp(), css: BaseCss);
        var wheelController = wheelHost.Root.RootElement.ById("scroller").ScrollController;
        wheelController.NestedConnection = wheel;
        wheelWindow.WheelNotches(Inside, 1);
        Settle(wheelHost, wheelClock);
        Assert.Equal(80, wheelController.Offset.Y, 3);
        var wheelPre = Assert.Single(wheel.PreScrollCalls);
        Assert.Equal(ScrollSource.Wheel, wheelPre.Source);
        Assert.Equal(100, wheelPre.Available.Y, 2);
        var wheelPost = Assert.Single(wheel.PostScrollCalls);
        Assert.Equal(ScrollSource.Wheel, wheelPost.Source);
        Assert.Equal(80, wheelPost.Consumed.Y, 2);
        Assert.Equal(100, wheelPost.Available.Y, 2);

        // 触摸拖动：pre / post 每个 move 都调用，post.consumed 之和等于容器偏移；松手后有 fling 钩子
        var touch = new RecordingConnection();
        var (host, window, clock) = Create(new SnapVerticalApp { Rows = 20 });
        var controller = host.Root.RootElement.ById("scroller").ScrollController;
        controller.NestedConnection = touch;
        double t = 0;
        var p = Inside;
        Pointer(window, RawPointerEventType.Down, p, PointerType.Touch, 12, PointerButtons.Left, PointerButtons.Left, t);
        for (int i = 0; i < 8; i++)
        {
            t += 16;
            p += new Point(0, -10);
            Pointer(window, RawPointerEventType.Move, p, PointerType.Touch, 12, buttons: PointerButtons.Left, ms: t);
        }
        double dragged = controller.Offset.Y;
        clock.Advance(TimeSpan.FromMilliseconds(t));
        Pointer(window, RawPointerEventType.Up, p, PointerType.Touch, 12, PointerButtons.Left, ms: t + 1);
        Settle(host, clock);
        Assert.NotEmpty(touch.PreScrollCalls);
        Assert.All(touch.PreScrollCalls, call => Assert.Equal(ScrollSource.Touch, call.Source));
        Assert.NotEmpty(touch.PostScrollCalls);
        Assert.All(touch.PostScrollCalls, call => Assert.Equal(ScrollSource.Touch, call.Source));
        Assert.Equal(dragged, touch.PostScrollCalls.Sum(call => call.Consumed.Y), 2);
        Assert.True(controller.Offset.Y > dragged, "the fling should keep scrolling after the drag");
        Assert.Single(touch.PreFlingCalls);
        Assert.Single(touch.PostFlingCalls);
    }

    [Fact]
    public void Touch_chaining_stops_at_a_contained_inner_scroller_and_invalid_hook_results_are_ignored()
    {
        var invalid = new RecordingConnection { PreConsume = new Vector2(float.NaN, float.NaN) };
        var (host, window, clock) = Create(new ListApp { Nested = true }, smooth: false);
        var root = host.Root.RootElement;
        var outer = root.ById("scroller").ScrollController;
        var inner = root.ById("inner").ScrollController;
        invalid.PreConsume = new Vector2(float.NaN, float.NaN);
        inner.NestedConnection = invalid;
        var over = root.ById("inner").BoundingRect.Center;
        window.WheelNotches(over, 1);
        host.RunFrame();
        // 非有限返回值被忽略：像没有联结一样滚动
        Assert.Equal(100, inner.Offset.Y, 3);
        Assert.Equal(0, outer.Offset.Y, 3);

        // contain：内层滚到头也不传给外层（与没有联结时一致）
        inner.OverscrollBehaviorY = OverscrollBehavior.Contain;
        for (int i = 0; i < 2; i++)
        {
            window.WheelNotches(over, 1);
            host.RunFrame();
        }
        Assert.Equal(inner.MaxOffset.Y, inner.Offset.Y, 3);
        Assert.Equal(0, outer.Offset.Y, 3);
        _ = clock;
    }

    [Fact]
    public void Sticky_descendant_disables_the_scroll_shift_fast_path()
    {
        var (host, window, clock) = Create(new SnapStickyApp(), smooth: false);
        _ = clock;
        for (int i = 0; i < 4; i++)
        {
            window.TrackpadScroll(Inside, 3, 0, ScrollPhase.Changed);
            host.RunFrame();
            Assert.Null(host.Pipeline.LastScrollShift);
        }
        Assert.True(host.LastDamage!.Value.Height >= 146);
    }
}
