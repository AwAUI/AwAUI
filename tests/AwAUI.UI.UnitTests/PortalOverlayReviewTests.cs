namespace AwAUI.UI.UnitTests;

/// <summary>第九批复查：布局完成回调的轮数、覆盖层的边界情况。</summary>
public partial class PortalOverlayReviewTests
{
    [Fact]
    public void Layout_completed_runs_once_per_frame_when_handlers_change_nothing()
    {
        var h = new PipelineHarness(new Div { Id = "a", Style = "width: 10px; height: 10px" });
        int calls = 0;
        h.Root.LayoutCompleted += _ => calls++;
        h.El("a").SetStyleOverride("width", "20px");
        h.Root.RunFrame();
        // 处理器没有改变任何样式：不再重复触发、不再重算样式与布局
        Assert.Equal(1, calls);
        Assert.Equal(20, h.Box("a").Width);
    }

    [Fact]
    public void Layout_completed_reruns_only_while_handlers_keep_changing_styles()
    {
        var h = new PipelineHarness(new Div { Children = { new Div { Id = "a", Style = "width: 10px; height: 10px" }, new Div { Id = "b", Style = "width: 10px; height: 10px" } } });
        var seen = new List<float>();
        h.Root.LayoutCompleted += root =>
        {
            // 第一轮把 a 加宽；第二轮看到新几何、让 b 跟上；第三轮没有变化
            var a = root.RootElement.ById("a");
            var b = root.RootElement.ById("b");
            seen.Add(a.BoundingRect.Width);
            a.SetStyleOverride("width", "30px");
            b.SetStyleOverride("width", a.BoundingRect.Width + "px");
        };
        h.Root.RunFrame();
        Assert.Equal(new float[] { 10, 30, 30 }, seen);
        Assert.Equal(30, h.Box("b").Width);
        seen.Clear();
        h.El("a").SetStyleOverride("height", "12px");
        h.Root.RunFrame();
        Assert.Equal(new float[] { 30 }, seen);
    }

    sealed partial class Thrower : Component
    {
        protected override Node Build() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Error_while_mounting_portal_children_does_not_leak_an_overlay_layer()
    {
        var show = new State<bool>(false);
        var app = new Lambda
        {
            Render = () => new Div
            {
                Children =
                {
                    new ErrorBoundary
                    {
                        Fallback = _ => new Div { Id = "fallback" },
                        Children = new NodeList { show.Value ? new Portal { Children = { new Div { Id = "ok" }, new Thrower() } } : null },
                    },
                },
            },
        };
        var root = new UIRoot();
        root.Mount(app);
        show.Value = true;
        root.RunUntilIdle();
        Assert.NotNull(root.RootElement.ById("fallback"));
        Assert.Empty(root.Overlay.Layers);
        Assert.False(root.Overlay.IsAttached);
    }

    [Fact]
    public void Keyed_portals_keep_their_elements_and_layer_order_when_siblings_reorder()
    {
        var swapped = new State<bool>(false);
        var app = new Lambda
        {
            Render = () =>
            {
                var a = new Portal { Key = "a", Children = { new Div { Id = "a" } } };
                var b = new Portal { Key = "b", Children = { new Div { Id = "b" } } };
                return new Div { Children = { swapped.Value ? b : a, new Div { Id = "mid" }, swapped.Value ? a : b } };
            },
        };
        var root = new UIRoot();
        root.Mount(app);
        var ea = root.RootElement.ById("a");
        var eb = root.RootElement.ById("b");
        var layers = root.Overlay.Layers.ToArray();
        swapped.Value = true;
        root.RunUntilIdle();
        Assert.Same(ea, root.RootElement.ById("a"));
        Assert.Same(eb, root.RootElement.ById("b"));
        // 层按打开顺序叠放，移动组件树中的位置不改变它们
        Assert.Equal(layers, root.Overlay.Layers.ToArray());
        Assert.Single(root.RootElement.ById("mid").Parent!.Children);
    }

    [Fact]
    public void Unmounting_an_outer_portal_removes_nested_layers_and_releases_focus()
    {
        var show = new State<bool>(true);
        var app = new Lambda
        {
            Render = () => new Div
            {
                Children =
                {
                    new Button { Id = "keep" },
                    show.Value
                        ? new Portal { Children = { new Div { Children = { new Portal { Children = { new Button { Id = "deep" } } } } } } }
                        : null,
                },
            },
        };
        var root = new UIRoot();
        root.Mount(app);
        Assert.Equal(2, root.Overlay.Layers.Count);
        var deep = root.RootElement.ById("deep");
        Assert.True(deep.Focus());
        var log = new List<FocusChangedEventArgs>();
        root.FocusManager.FocusChanged += log.Add;
        show.Value = false;
        root.RunUntilIdle();
        Assert.Empty(root.Overlay.Layers);
        Assert.False(root.Overlay.IsAttached);
        Assert.False(deep.IsAttached);
        Assert.Null(root.FocusManager.FocusedElement);
        var removed = Assert.Single(log);
        Assert.True(removed.ElementRemoved);
        Assert.Same(deep, removed.OldFocus);
    }

    [Fact]
    public void After_layout_callback_requested_inside_a_round_runs_in_the_same_frame()
    {
        var h = new PipelineHarness(new Div { Id = "a", Style = "width: 10px; height: 10px" });
        var log = new List<string>();
        h.Root.RequestAfterLayout(() =>
        {
            log.Add("first");
            h.Root.RequestAfterLayout(() => log.Add("second"));
        });
        h.Root.RunFrame();
        Assert.Equal(new[] { "first", "second" }, log);
        Assert.False(h.Root.NeedsFrame);
    }
}
