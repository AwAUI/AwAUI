using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 组件库 UI 接入所需的公开接口（design/23 第 1 层）：覆盖层与 Portal、元素几何与文档顺序、运行时样式 / 属性覆盖、
/// 布局完成回调（同一帧内定位）、焦点接口、根级预览输入、指针屏蔽与滚动锁定。
/// </summary>
public partial class PortalOverlayTests
{
    sealed partial class Reader : Component
    {
        public List<string> Seen { get; set; } = new();

        protected override Node Build()
        {
            var v = Use<string>();
            Seen.Add(v);
            return new Div { Id = "reader", Children = { v } };
        }
    }

    [Fact]
    public void Portal_children_live_in_the_overlay_but_keep_component_context()
    {
        var value = new State<string>("a");
        var seen = new List<string>();
        var showPortal = new State<bool>(true);
        var app = new Lambda
        {
            Render = () => Context.Provide(value.Value, new Div
            {
                Id = "main",
                Children = { showPortal.Value ? new Portal { Children = { new Reader { Seen = seen } } } : null },
            }),
        };
        var root = new UIRoot();
        root.Mount(app);
        var main = root.RootElement.ById("main");
        var reader = root.RootElement.ById("reader");
        Assert.Empty(main.Children); // Portal 不向父宿主贡献元素
        Assert.True(root.Overlay.IsAttached);
        Assert.Same(root.Overlay.Element, root.RootElement.LastChild);
        Assert.Equal(OverlayRoot.ElementTag, root.Overlay.Element.Tag);
        Assert.Single(root.Overlay.Layers);
        Assert.Equal(OverlayRoot.LayerTag, reader.Parent!.Tag);
        Assert.Equal("a", reader.TextContent);
        // 组件树中的父级不变：上下文变化照常传到 Portal 中的组件
        value.Value = "b";
        root.RunUntilIdle();
        Assert.Equal("b", root.RootElement.ById("reader").TextContent);
        // 卸载：层与覆盖层根随之离开
        showPortal.Value = false;
        root.RunUntilIdle();
        Assert.False(root.Overlay.IsAttached);
        Assert.Empty(root.Overlay.Layers);
        Assert.False(reader.IsAttached);
        Assert.Equal(1, root.RootElement.ChildCount);
    }

    [Fact]
    public void Layers_stack_in_open_order_and_by_z_order()
    {
        var showA = new State<bool>(false);
        var showB = new State<bool>(false);
        var showToast = new State<bool>(false);
        var app = new Lambda
        {
            Render = () => new Div
            {
                Children =
                {
                    showToast.Value ? new Portal { ZOrder = OverlayZOrder.Toast, Children = { new Div { Id = "toast" } } } : null,
                    showA.Value
                        ? new Portal
                        {
                            Children =
                            {
                                new Div { Id = "a" },
                                // 嵌套的 Portal 叠在外层之上
                                showB.Value ? new Portal { Children = { new Div { Id = "b" } } } : null,
                            },
                        }
                        : null,
                },
            },
        };
        var root = new UIRoot();
        root.Mount(app);
        showToast.Value = true;
        root.RunUntilIdle();
        showA.Value = true;
        root.RunUntilIdle();
        showB.Value = true;
        root.RunUntilIdle();
        string Order() => string.Join(",", root.Overlay.Layers.Select(l => l.FindAll(e => e.Id is not null).Single().Id));
        Assert.Equal("a,b,toast", Order());
        Assert.Equal(DocumentPosition.Following, root.RootElement.ById("a").CompareDocumentPosition(root.RootElement.ById("b")));
        // 同时打开外层与嵌套层：外层先建立
        showA.Value = false;
        root.RunUntilIdle();
        showA.Value = true;
        root.RunUntilIdle();
        Assert.Equal("a,b,toast", Order());
        Assert.Same(root.Overlay.Layers[1], root.Overlay.LayerOf(root.RootElement.ById("b")));
    }

    [Fact]
    public void Compare_document_position_follows_tree_order()
    {
        var app = new Lambda
        {
            Render = () => new Div
            {
                Id = "p",
                Children = { new Div { Id = "x", Children = { new Div { Id = "x1" } } }, new Div { Id = "y" } },
            },
        };
        var root = new UIRoot();
        root.Mount(app);
        Element E(string id) => root.RootElement.ById(id);
        Assert.Equal(DocumentPosition.None, E("x").CompareDocumentPosition(E("x")));
        Assert.Equal(DocumentPosition.Following, E("x").CompareDocumentPosition(E("y")));
        Assert.Equal(DocumentPosition.Preceding, E("y").CompareDocumentPosition(E("x1")));
        Assert.Equal(DocumentPosition.Contains | DocumentPosition.Preceding, E("x1").CompareDocumentPosition(E("p")));
        Assert.Equal(DocumentPosition.ContainedBy | DocumentPosition.Following, E("p").CompareDocumentPosition(E("x1")));
        Assert.True((E("x").CompareDocumentPosition(new Element("div")) & DocumentPosition.Disconnected) != 0);
        var list = new List<Element> { E("y"), E("x1"), E("p"), E("x") };
        list.Sort(Element.CompareTreeOrder);
        Assert.Equal(new[] { "p", "x", "x1", "y" }, list.Select(e => e.Id));
    }

    [Fact]
    public void Clipping_rect_intersects_overflow_ancestors_and_ignores_them_for_fixed()
    {
        const string css = """
            .clip { position: absolute; left: 50px; top: 40px; width: 200px; height: 100px; overflow: hidden; border: 5px solid black; }
            .inner { width: 400px; height: 400px; }
            .fixed { position: fixed; left: 10px; top: 10px; width: 10px; height: 10px; }
            """;
        var h = new PipelineHarness(new Div
        {
            Class = "clip",
            Children = { new Div { Id = "inner", Class = "inner", Children = { new Div { Id = "fixed", Class = "fixed" } } } },
        }, css, 400, 300);
        // 内边距盒：边框内侧（默认样式表 box-sizing: border-box）
        Assert.Equal(new Rect(55, 45, 190, 90), h.El("inner").GetClippingRect());
        Assert.Equal(new Rect(0, 0, 400, 300), h.El("fixed").GetClippingRect());
        Assert.Equal(new Size(400, 300), h.Root.ViewportSize);
        Assert.Same(h.El("inner").Parent!.ScrollController, h.El("inner").GetScrollContainer());
        Assert.Same(h.Root.RootElement.ScrollController, h.El("inner").Parent!.GetScrollContainer());
        Assert.Equal(h.El("inner").BoundingRect, h.El("inner").GetBoundingClientRect());
    }

    [Fact]
    public void Style_and_attribute_overrides_survive_rebuilds_and_restyle_in_the_same_frame()
    {
        const string css = ".box { position: absolute; left: 0; top: 0; width: 10px; height: 10px; } .box[data-side=top] { width: 30px; }";
        var color = new State<string>("red");
        var app = new Lambda { Render = () => new Div { Id = "box", Class = "box", Style = "background-color: " + color.Value } };
        var h = new PipelineHarness(app, css, 400, 300);
        var box = h.El("box");
        box.SetStyleOverride("left", "40px");
        box.SetAttributeOverride("data-side", "top");
        h.Frame();
        Assert.Equal(new Rect(40, 0, 30, 10), box.BoundingRect);
        Assert.Equal("top", box.GetAttribute("data-side"));
        Assert.Contains(box.Attributes, kv => kv.Key == "data-side" && kv.Value == "top");
        // 组件重建改写节点样式：覆盖保留，且仍在后面（胜出）
        color.Value = "blue";
        h.Frame();
        Assert.Equal("background-color: blue; left: 40px;", box.InlineStyle);
        Assert.Equal(new Rect(40, 0, 30, 10), box.BoundingRect);
        box.SetStyleOverride("left", null);
        box.SetAttributeOverride("data-side", null);
        h.Frame();
        Assert.Equal(new Rect(0, 0, 10, 10), box.BoundingRect);
        Assert.Equal("background-color: blue", box.InlineStyle);
        Assert.Throws<ArgumentException>(() => box.SetStyleOverride("left", "1px; color: red"));
        Assert.Throws<ArgumentException>(() => box.SetStyleOverride("le ft", "1px"));
    }

    [Fact]
    public void Layout_completed_runs_after_layout_and_its_style_changes_are_painted_in_the_same_frame()
    {
        const string css = ".anchor { position: absolute; left: 100px; top: 50px; width: 20px; height: 20px; } .float { position: fixed; left: 0; top: 0; width: 10px; height: 10px; background-color: red; }";
        var h = new PipelineHarness(new Div { Children = { new Div { Id = "anchor", Class = "anchor" }, new Div { Id = "float", Class = "float" } } }, css, 400, 300);
        int calls = 0;
        h.Root.LayoutCompleted += root =>
        {
            calls++;
            var a = root.RootElement.ById("anchor").GetBoundingClientRect();
            var f = root.RootElement.ById("float");
            f.SetStyleOverride("left", a.Right + "px");
            f.SetStyleOverride("top", a.Bottom + "px");
        };
        h.Root.RunFrame();
        Assert.Equal(new Rect(120, 70, 10, 10), h.Box("float"));
        Assert.True(calls >= 1);
        // 命中测试表也是本帧的结果
        Assert.Same(h.El("float"), h.Pipeline.HitTest(new Point(125, 75)));
        int before = calls;
        bool ran = false;
        h.Root.RequestAfterLayout(() => ran = true);
        Assert.True(h.Root.NeedsFrame);
        h.Root.RunFrame();
        Assert.True(ran);
        Assert.True(calls > before);
    }

    [Fact]
    public void Layout_completed_without_pipeline_runs_at_end_of_frame()
    {
        var root = new UIRoot();
        int calls = 0;
        bool once = false;
        root.LayoutCompleted += _ => calls++;
        root.Mount(new Div());
        Assert.Equal(1, calls);
        root.RequestAfterLayout(() => once = true);
        root.RunFrame();
        Assert.True(once);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Focus_changed_reports_origin_and_removed_elements()
    {
        var show = new State<bool>(true);
        var app = new Lambda
        {
            Render = () => new Div
            {
                Children = { new Button { Id = "a" }, show.Value ? new Button { Id = "b" } : null },
            },
        };
        var root = new UIRoot();
        root.Mount(app);
        var log = new List<FocusChangedEventArgs>();
        root.FocusManager.FocusChanged += log.Add;
        var a = root.RootElement.ById("a");
        var b = root.RootElement.ById("b");
        a.Focus();
        root.Press(PhysicalKey.Tab);
        Assert.Equal(2, log.Count);
        Assert.Same(a, log[0].NewFocus);
        Assert.Equal(FocusOrigin.Programmatic, log[0].Origin);
        Assert.Same(a, log[1].OldFocus);
        Assert.Same(b, log[1].NewFocus);
        Assert.Equal(FocusOrigin.Keyboard, log[1].Origin);
        show.Value = false;
        root.RunUntilIdle();
        Assert.Equal(3, log.Count);
        Assert.True(log[2].ElementRemoved);
        Assert.Same(b, log[2].OldFocus);
        Assert.Null(log[2].NewFocus);
    }

    [Fact]
    public void Tab_order_in_scope_skips_hidden_elements()
    {
        const string css = ".hidden { display: none; } .invisible { visibility: hidden; }";
        var h = new PipelineHarness(new Div
        {
            Children =
            {
                new Button { Id = "outside" },
                new Div
                {
                    Id = "scope",
                    Children =
                    {
                        new Button { Id = "one" },
                        new Div { Class = "hidden", Children = { new Button { Id = "gone" } } },
                        new Button { Id = "ghost", Class = "invisible" },
                        new Div { Id = "neg", TabIndex = -1 },
                        new Button { Id = "two", TabIndex = 2 },
                        new Button { Id = "three" },
                    },
                },
            },
        }, css);
        var order = h.Root.FocusManager.GetTabOrder(h.El("scope"));
        Assert.Equal(new[] { "two", "one", "three" }, order.Select(e => e.Id));
        Assert.False(h.El("gone").IsRendered);
        Assert.True(h.El("ghost").IsRendered);
        Assert.False(h.El("ghost").IsVisible);
        Assert.True(h.El("one").IsVisible);
        // 全局 Tab 顺序同样跳过不渲染 / 不可见的元素
        Assert.DoesNotContain(h.Root.FocusManager.GetTabOrder(), e => e.Id is "gone" or "ghost");
    }

    [Fact]
    public void Focus_options_select_text_and_prevent_scroll()
    {
        const string css = ".scroller { height: 100px; overflow: auto; } .spacer { height: 500px; }";
        var h = new PipelineHarness(new Div
        {
            Id = "scroller",
            Class = "scroller",
            Children = { new Div { Class = "spacer" }, new Input { Id = "field", Value = "hello" } },
        }, css);
        var field = (InputElement)h.El("field");
        Assert.True(field.Focus(new FocusOptions { SelectText = true, PreventScroll = true }));
        h.Frame();
        Assert.Equal(0, field.SelectionStart);
        Assert.Equal(5, field.SelectionEnd);
        Assert.Equal(0, h.El("scroller").ScrollController.Offset.Y);
        field.Blur();
        field.Focus();
        h.Frame();
        Assert.True(h.El("scroller").ScrollController.Offset.Y > 0);
    }

    [Fact]
    public void Preview_events_run_before_element_handlers_and_can_stop_them()
    {
        var log = new List<string>();
        var h = new PipelineHarness(new Button
        {
            Id = "btn",
            Style = "width: 50px; height: 20px",
            OnPointerDown = _ => log.Add("down"),
            OnKeyDown = _ => log.Add("key"),
        });
        h.Root.Input.PreviewPointerDown += e => log.Add("preview-down " + e.Target?.Id);
        h.Root.Input.PreviewKeyDown += e =>
        {
            log.Add("preview-key " + e.Key);
            if (e.Key == "Escape") e.StopPropagation();
        };
        h.Click(10, 10);
        Assert.Equal(new[] { "preview-down btn", "down" }, log);
        log.Clear();
        h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyA, KeyModifiers.None, false, 0, TimeSpan.Zero, "a"));
        h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.Escape, KeyModifiers.None, false, 0, TimeSpan.Zero, "Escape"));
        Assert.Equal(new[] { "preview-key a", "key", "preview-key Escape" }, log);
    }

    [Fact]
    public void Pointer_blocking_redirects_hits_outside_the_allowed_subtree_to_the_root()
    {
        int clicks = 0;
        var h = new PipelineHarness(new Div
        {
            Children =
            {
                new Button { Id = "below", Style = "width: 50px; height: 20px", OnClick = _ => clicks++ },
                new Div { Id = "modal", Style = "width: 50px; height: 20px", TabIndex = -1, Children = { new Button { Id = "inside", Style = "width: 50px; height: 20px" } } },
            },
        });
        Element? previewTarget = null;
        h.Root.Input.PreviewPointerDown += e => previewTarget = e.Target;
        var token = h.Root.DisableOutsidePointerEvents(h.El("modal"));
        Assert.True(h.Root.IsPointerBlocked(h.El("below")));
        Assert.False(h.Root.IsPointerBlocked(h.El("inside")));
        h.Click(10, 10);
        Assert.Equal(0, clicks);
        Assert.Same(h.Root.RootElement, previewTarget);
        Assert.Equal(PseudoStates.None, h.El("below").States & PseudoStates.Hover);
        Assert.Null(h.Root.FocusManager.FocusedElement);
        h.Click(10, 30);
        Assert.Same(h.El("inside"), h.Root.FocusManager.FocusedElement);
        token.Dispose();
        h.Click(10, 10);
        Assert.Equal(1, clicks);
    }
}
