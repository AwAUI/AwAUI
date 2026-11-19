namespace AwAUI.UI.UnitTests;

partial class Reader : Component
{
    public State<int>? Source { get; set; }

    public string Label { get; set; } = "";

    public int Builds;

    protected override Node Build()
    {
        Builds++;
        return new Text(Label + ":" + (Source?.Value ?? 0));
    }
}

partial class Parent2 : Component
{
    public State<int> Mine { get; set; } = new(0);

    public State<int> ChildSource { get; set; } = new(0);

    public Reader? Child;

    public int Builds;

    protected override Node Build()
    {
        Builds++;
        var child = new Reader { Source = ChildSource, Label = "child" };
        return new Div { Children = [new Text("parent:" + Mine.Value), child] };
    }

    protected override void OnMount() => Child = (Reader?)Root!.RootElement.FindAll(e => e.Owner is Reader).Single().Owner;
}

public class ReactiveTests
{
    [Fact]
    public void State_ignores_equal_writes_and_supports_lazy_init()
    {
        int inits = 0;
        var s = new State<string>(() => { inits++; return "x"; });
        Assert.Equal(0, inits);
        Assert.Equal("x", s.Value);
        Assert.Equal("x", s.Value);
        Assert.Equal(1, inits);

        int runs = 0;
        using var effect = Reactive.Effect(() => { _ = s.Value; runs++; });
        Assert.Equal(1, runs);
        s.Value = "x";
        Assert.Equal(1, runs);
        s.Value = "y";
        Assert.Equal(2, runs);
        s.Update(v => v + "!");
        Assert.Equal("y!", s.Peek());
        Assert.Equal(3, runs);
    }

    [Fact]
    public void Only_the_reading_component_rebuilds()
    {
        var parent = new Parent2();
        var root = new UIRoot();
        root.Mount(parent);
        var child = parent.Child!;
        Assert.Equal(1, parent.Builds);
        Assert.Equal(1, child.Builds);

        // 子组件读的 State 变化：只有子组件重建
        parent.ChildSource.Value = 5;
        root.RunFrame();
        Assert.Equal(1, parent.Builds);
        Assert.Equal(2, child.Builds);
        Assert.Contains("child:5", root.RootElement.TextContent);

        // 父组件读的 State 变化：父组件重建，子组件 Props 相等（同一 State、同一 Label）→ 跳过
        parent.Mine.Value = 1;
        root.RunFrame();
        Assert.Equal(2, parent.Builds);
        Assert.Equal(2, child.Builds);
        Assert.Contains("parent:1", root.RootElement.TextContent);

        // 同一帧内两者都脏：各自最多重建一次
        parent.Mine.Value = 2;
        parent.ChildSource.Value = 6;
        parent.ChildSource.Value = 7;
        root.RunFrame();
        Assert.Equal(3, parent.Builds);
        Assert.Equal(3, child.Builds);
        Assert.Equal("parent:2child:7", root.RootElement.TextContent);
    }

    [Fact]
    public void Dependencies_are_re_tracked_each_build()
    {
        var useA = new State<bool>(true);
        var a = new State<int>(1);
        var b = new State<int>(10);
        var comp = new Lambda { Render = () => new Text((useA.Value ? a.Value : b.Value).ToString()) };
        var root = new UIRoot();
        root.Mount(comp);
        b.Value = 11;
        Assert.False(root.NeedsFrame); // 未读过 b
        useA.Value = false;
        root.RunFrame();
        Assert.Equal("11", root.RootElement.TextContent);
        a.Value = 2;
        Assert.False(root.NeedsFrame); // 已不再读 a
        b.Value = 12;
        Assert.True(root.NeedsFrame);
        root.RunFrame();
        Assert.Equal("12", root.RootElement.TextContent);
        Assert.Equal(3, comp.Builds);
    }

    [Fact]
    public void Computed_is_lazy_cached_and_skips_downstream_when_unchanged()
    {
        var n = new State<int>(1);
        int computes = 0;
        var parity = new Computed<string>(() => { computes++; return n.Value % 2 == 0 ? "even" : "odd"; });
        Assert.Equal(0, computes);
        Assert.Equal("odd", parity.Value);
        Assert.Equal("odd", parity.Value);
        Assert.Equal(1, computes);

        var comp = new Lambda { Render = () => new Text(parity.Value) };
        var root = new UIRoot();
        root.Mount(comp);
        Assert.Equal(1, comp.Builds);

        n.Value = 3; // 奇偶不变：Computed 重算一次，但结果相等 → 组件不重建
        root.RunFrame();
        Assert.Equal(2, computes);
        Assert.Equal(1, comp.Builds);

        n.Value = 4;
        root.RunFrame();
        Assert.Equal(3, computes);
        Assert.Equal(2, comp.Builds);
        Assert.Equal("even", root.RootElement.TextContent);
    }

    [Fact]
    public void Computed_is_glitch_free()
    {
        var a = new State<int>(1);
        var b = new Computed<int>(() => a.Value * 2);
        var c = new Computed<int>(() => a.Value + b.Value);
        var seen = new List<(int, int, int)>();
        using var e = Reactive.Effect(() => seen.Add((a.Value, b.Value, c.Value)));
        a.Value = 2;
        a.Value = 3;
        Assert.Equal(new[] { (1, 2, 3), (2, 4, 6), (3, 6, 9) }, seen.ToArray());
    }

    [Fact]
    public void Diamond_dependency_runs_effect_once_per_write()
    {
        var a = new State<int>(0);
        var left = new Computed<int>(() => a.Value + 1);
        var right = new Computed<int>(() => a.Value + 2);
        int runs = 0;
        using var e = Reactive.Effect(() => { _ = left.Value + right.Value; runs++; });
        a.Value = 1;
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Unobserved_computed_tracks_without_subscribing()
    {
        var a = new State<int>(1);
        int computes = 0;
        var c = new Computed<int>(() => { computes++; return a.Value * 10; });
        Assert.Equal(10, c.Value);
        Assert.False(a.HasSubscribers); // 无订阅者的 Computed 不挂在源上，不会泄漏
        Assert.Equal(10, c.Value);
        Assert.Equal(1, computes);
        a.Value = 2;
        Assert.Equal(20, c.Value);
        Assert.Equal(2, computes);

        using (Reactive.Effect(() => _ = c.Value))
        {
            Assert.True(a.HasSubscribers);
        }
        Assert.False(a.HasSubscribers);
    }

    [Fact]
    public void Computed_caches_exceptions_until_dependencies_change()
    {
        var a = new State<int>(0);
        int computes = 0;
        var c = new Computed<int>(() => { computes++; return 10 / a.Value; });
        Assert.Throws<DivideByZeroException>(() => c.Value);
        Assert.Throws<DivideByZeroException>(() => c.Value);
        Assert.Equal(1, computes);
        a.Value = 5;
        Assert.Equal(2, c.Value);
    }

    [Fact]
    public void Writing_state_inside_computed_throws()
    {
        var a = new State<int>(0);
        var c = new Computed<int>(() => { a.Value = 1; return 0; });
        Assert.Throws<InvalidOperationException>(() => c.Value);
    }

    [Fact]
    public void Batch_coalesces_effect_runs()
    {
        var a = new State<int>(0);
        var b = new State<int>(0);
        var seen = new List<int>();
        using var e = Reactive.Effect(() => seen.Add(a.Value + b.Value));
        Reactive.Batch(() =>
        {
            a.Value = 1;
            b.Value = 2;
            a.Value = 3;
        });
        Assert.Equal(new[] { 0, 5 }, seen.ToArray());
    }

    [Fact]
    public void Effect_cleanup_runs_before_rerun_and_on_dispose()
    {
        var a = new State<int>(0);
        var log = new List<string>();
        var e = Reactive.Effect(() =>
        {
            int v = a.Value;
            log.Add("run " + v);
            return () => log.Add("cleanup " + v);
        });
        a.Value = 1;
        e.Dispose();
        a.Value = 2;
        Assert.Equal(new[] { "run 0", "cleanup 0", "run 1", "cleanup 1" }, log.ToArray());
    }

    [Fact]
    public void Effect_loop_is_detected()
    {
        var a = new State<int>(0);
        Assert.Throws<InvalidOperationException>(() => Reactive.Effect(() => a.Value = a.Value + 1));
    }

    [Fact]
    public void Untrack_reads_do_not_subscribe()
    {
        var a = new State<int>(0);
        int runs = 0;
        using var e = Reactive.Effect(() => { Reactive.Untrack(() => a.Value); runs++; });
        a.Value = 1;
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Cross_thread_writes_are_marshalled_to_the_ui_dispatcher()
    {
        var dispatcher = new TestDispatcher();
        var root = new UIRoot(dispatcher);
        try
        {
            var s = new State<int>(0);
            var comp = new Lambda { Render = () => new Text("v=" + s.Value) };
            root.Mount(comp);

            RunOnOtherThread(() => s.Value = 42);
            RunOnOtherThread(() => s.Update(v => v + 1));
            Assert.Equal(0, s.Peek()); // 尚未在 UI 线程执行
            Assert.Equal(2, dispatcher.Pending);
            Assert.False(root.NeedsFrame);

            dispatcher.Pump();
            Assert.Equal(43, s.Peek());
            Assert.True(root.NeedsFrame);
            root.RunFrame();
            Assert.Equal("v=43", root.RootElement.TextContent);

            // 同线程写入直接生效
            s.Value = 1;
            Assert.Equal(1, s.Peek());
        }
        finally
        {
            Reactive.CurrentThreadDispatcher = null;
        }
    }

    static void RunOnOtherThread(Action action)
    {
        var t = new Thread(() => action());
        t.Start();
        t.Join();
    }

    [Fact]
    public void Frame_requested_is_raised_once_until_frame_runs()
    {
        var s = new State<int>(0);
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Text(s.Value.ToString()) });
        int requests = 0;
        root.FrameRequested += _ => requests++;
        s.Value = 1;
        s.Value = 2;
        Assert.Equal(1, requests);
        root.RunFrame();
        s.Value = 3;
        Assert.Equal(2, requests);
    }
}
