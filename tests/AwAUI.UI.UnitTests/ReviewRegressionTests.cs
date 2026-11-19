using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>记录生命周期的组件，Build 可按需抛出。</summary>
partial class LifecycleProbe : Component
{
    public State<bool>? Fail { get; set; }

    public List<string> Log { get; set; } = new();

    protected override Node Build()
    {
        if (Fail?.Value ?? false) throw new InvalidOperationException("probe");
        UseEffect(() =>
        {
            Log.Add("effect");
            return () => Log.Add("cleanup");
        });
        return new Div { Id = "probe" };
    }

    protected override void OnMount() => Log.Add("mount");

    protected override void OnUnmount() => Log.Add("unmount");
}

/// <summary>评审中发现的问题的回归测试。</summary>
public class ReviewRegressionTests
{
    static UIRoot MountStatic(Node node)
    {
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => node });
        return root;
    }

    // ------------------------------------------------------------ 组件生命周期

    [Fact]
    public void Remounted_component_instance_gets_on_mount_again()
    {
        // ErrorBoundary.Reset 会把同一个 Children 列表（其中是已卸载的存活实例）重新挂载
        var fail = new State<bool>(false);
        var log = new List<string>();
        var probe = new LifecycleProbe { Fail = fail, Log = log };
        var boundary = new ErrorBoundary { Children = [probe], Fallback = _ => new Text("fallback") { Id = "fb" } };
        var root = new UIRoot();
        root.Mount(boundary);
        Assert.Equal(new[] { "mount", "effect" }, log.ToArray());

        fail.Value = true;
        root.RunFrame();
        Assert.Equal("text#fb", root.RootElement.Shape());
        Assert.False(probe.IsMounted);

        fail.Value = false;
        boundary.Reset();
        root.RunFrame();
        Assert.Equal("div#probe", root.RootElement.Shape());
        Assert.True(probe.IsMounted);
        Assert.Equal(new[] { "mount", "effect", "cleanup", "unmount", "mount", "effect" }, log.ToArray());
    }

    [Fact]
    public void Conditionally_rendered_cached_instance_keeps_balanced_lifecycle()
    {
        var show = new State<bool>(true);
        var log = new List<string>();
        var cached = new LifecycleProbe { Log = log };
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => show.Value ? cached : new Div { Id = "none" } });
        show.Value = false;
        root.RunFrame();
        show.Value = true;
        root.RunFrame();
        Assert.Equal(new[] { "mount", "effect", "cleanup", "unmount", "mount", "effect" }, log.ToArray());
    }

    // ------------------------------------------------------------ 错误边界

    [Fact]
    public void Errors_thrown_by_fallback_content_escalate_to_outer_boundary()
    {
        var root = new UIRoot();
        var unhandled = new List<UIError>();
        root.UnhandledError += unhandled.Add;
        var inner = new ErrorBoundary
        {
            Children = [new Thrower()],
            Fallback = _ => new Thrower(), // 后备界面本身也出错
        };
        var outer = new ErrorBoundary
        {
            Children = [inner],
            Fallback = ex => new Text("outer:" + ex.Message) { Id = "outer-fallback" },
        };
        root.Mount(outer);
        Assert.Equal("text#outer-fallback", root.RootElement.Shape());
        Assert.Equal("outer:boom", root.RootElement.TextContent);
        Assert.Empty(unhandled);
    }

    [Fact]
    public void Errors_thrown_by_fallback_without_outer_boundary_are_unhandled()
    {
        var root = new UIRoot();
        var unhandled = new List<UIError>();
        root.UnhandledError += unhandled.Add;
        root.Mount(new ErrorBoundary { Children = [new Thrower()], Fallback = _ => new Thrower() });
        Assert.Single(unhandled);
        Assert.Equal("boom", unhandled[0].Exception.Message);
    }

    // ------------------------------------------------------------ 事件派发

    [Fact]
    public void Stop_propagation_in_target_capture_handler_skips_target_bubble_handler()
    {
        // DOM：目标上的捕获监听器与非捕获监听器分两轮调用，每轮开始前检查 stop propagation 标志
        var log = new List<string>();
        var root = MountStatic(new Div
        {
            Id = "t",
            OnClickCapture = e => { log.Add("capture"); e.StopPropagation(); },
            OnClick = _ => log.Add("bubble"),
        });
        root.Input.Click(root.RootElement.ById("t"));
        Assert.Equal(new[] { "capture" }, log.ToArray());
    }

    [Fact]
    public void Space_activation_is_cancelled_when_focus_leaves_the_button()
    {
        int clicks = 0;
        var root = MountStatic(new Div
        {
            Children = [new Button { Id = "a", OnClick = _ => clicks++ }, new Button { Id = "b" }],
        });
        var a = root.RootElement.ById("a");
        a.Focus();
        root.Input.DispatchKey(TestUtil.KeyDown(PhysicalKey.Space));
        Assert.True(a.States.HasFlag(PseudoStates.Active));

        root.Press(PhysicalKey.Tab); // 焦点移到 b：浏览器取消这次空格激活
        Assert.False(a.States.HasFlag(PseudoStates.Active));
        root.Input.DispatchKey(TestUtil.KeyUp(PhysicalKey.Space));

        a.Focus();
        root.Input.DispatchKey(TestUtil.KeyUp(PhysicalKey.Space)); // 没有对应的按下：不激活
        Assert.Equal(0, clicks);

        root.Input.DispatchKey(TestUtil.KeyDown(PhysicalKey.Space));
        root.Input.DispatchKey(TestUtil.KeyUp(PhysicalKey.Space));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Errors_from_focus_handlers_surface_from_programmatic_focus_calls()
    {
        var root = MountStatic(new Button { Id = "b", OnFocus = _ => throw new FormatException("focus boom") });
        var b = root.RootElement.ById("b");
        Assert.Throws<FormatException>(() => b.Focus());
        Assert.True(b.IsFocused);
        root.RunFrame(); // 错误已经报告过，不会在下一帧再次抛出
    }

    // ------------------------------------------------------------ 协调器

    [Fact]
    public void Duplicate_keys_are_reported_on_initial_mount()
    {
        var root = new UIRoot();
        var diags = new List<UIDiagnostic>();
        root.Diagnostic += diags.Add;
        root.Mount(new Lambda { Render = () => new Div { Children = [new Div { Key = "k" }, new Span { Key = "k" }, new Div { Key = "k" }] } });
        var dup = Assert.Single(diags, d => d.Code == "AW1008");
        Assert.Contains("k", dup.Message);

        // 大列表（哈希路径）
        diags.Clear();
        var root2 = new UIRoot();
        root2.Diagnostic += diags.Add;
        var many = new NodeList();
        for (int i = 0; i < 40; i++) many.Add(new Div { Key = i % 20 });
        root2.Mount(new Lambda { Render = () => new Div { Children = many } });
        Assert.Equal(20, diags.Count(d => d.Code == "AW1008"));
    }

    [Fact]
    public void Very_deep_trees_report_an_error_instead_of_overflowing_the_stack()
    {
        // 数据驱动的极深树：协调器不能因递归耗尽栈而让进程崩溃
        const int depth = 200_000;
        var leaf = new Div { Id = "leaf" };
        ElementNode top = leaf;
        for (int i = 0; i < depth; i++) top = new Div { Children = [top] };
        var errors = new List<UIError>();
        Exception? crash = null;
        var t = new Thread(() =>
        {
            try
            {
                var root = new UIRoot();
                root.UnhandledError += errors.Add;
                root.Mount(new Lambda { Render = () => top });
                root.Unmount();
                Assert.Equal(0, root.RootElement.ChildCount);
            }
            catch (Exception ex)
            {
                crash = ex;
            }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        Assert.Null(crash);
        var error = Assert.Single(errors);
        Assert.IsType<InsufficientExecutionStackException>(error.Exception);
    }

    [Fact]
    public void Node_list_add_range_of_itself_terminates()
    {
        var list = new NodeList { "a", new Div() };
        list.AddRange(list); // 枚举器读取实时的 Count：不能无限增长
        Assert.Equal(4, list.Count);
        Assert.Same(list[1], list[3]);
    }

    // ------------------------------------------------------------ 响应式

    [Fact]
    public void Repeated_reads_with_switching_dependencies_track_precisely()
    {
        // 同一 State 读取多次时走依赖收尾的慢路径（就地复用依赖表）：切换后的依赖必须精确
        var a = new State<int>(0);
        var b = new State<int>(0);
        var c = new State<int>(0);
        var useB = new State<bool>(true);
        int runs = 0;
        using var effect = Reactive.Effect(() =>
        {
            runs++;
            _ = a.Value;
            _ = a.Value;
            _ = useB.Value ? b.Value : c.Value;
            _ = a.Value;
        });
        b.Value = 1;
        Assert.Equal(2, runs);
        c.Value = 1;
        Assert.Equal(2, runs);
        useB.Value = false;
        Assert.Equal(3, runs);
        b.Value = 2;
        Assert.Equal(3, runs);
        c.Value = 2;
        Assert.Equal(4, runs);
        a.Value = 1;
        Assert.Equal(5, runs);
        effect.Dispose();
        a.Value = 2;
        c.Value = 3;
        Assert.Equal(5, runs);
    }

    // ------------------------------------------------------------ 元素

    [Fact]
    public void Attribute_lookup_is_ascii_case_insensitive_for_reflected_fields()
    {
        var root = MountStatic(new Image { Id = "img", Source = "a.png", AriaLabel = "pic", Attributes = { ["Data-X"] = "1" } });
        var img = root.RootElement.ById("img");
        Assert.Equal("img", img.GetAttribute("ID"));
        Assert.Equal("a.png", img.GetAttribute("Src"));
        Assert.Equal("pic", img.GetAttribute("aria-label"));
        Assert.Equal("1", img.GetAttribute("data-x"));
        Assert.Null(img.GetAttribute("alt"));
    }
}

/// <summary>修改进程级 <see cref="Reactive.DefaultDispatcher"/> 的测试不能与其他测试并行。</summary>
[CollectionDefinition(nameof(GlobalDispatcherCollection), DisableParallelization = true)]
public sealed class GlobalDispatcherCollection { }

[Collection(nameof(GlobalDispatcherCollection))]
public class GlobalDispatcherTests
{
    [Fact]
    public void State_created_before_a_dispatcher_exists_uses_the_default_dispatcher_set_later()
    {
        // 例如应用启动时先创建的全局 Store，之后宿主才设置 DefaultDispatcher。
        // 同一工作线程上之前的测试（UIHost / UIRoot(dispatcher)）可能登记了线程调度器：先清掉，否则 State 会记住那个调度器
        var previousThread = Reactive.CurrentThreadDispatcher;
        Reactive.CurrentThreadDispatcher = null;
        var store = new State<int>(0);
        var dispatcher = new TestDispatcher();
        try
        {
            Reactive.DefaultDispatcher = dispatcher;
            var t = new Thread(() => store.Value = 7);
            t.Start();
            t.Join();
            Assert.Equal(0, store.Peek());
            Assert.Equal(1, dispatcher.Pending);
            dispatcher.Pump();
            Assert.Equal(7, store.Peek());
        }
        finally
        {
            Reactive.DefaultDispatcher = null;
            Reactive.CurrentThreadDispatcher = previousThread;
        }
    }
}
