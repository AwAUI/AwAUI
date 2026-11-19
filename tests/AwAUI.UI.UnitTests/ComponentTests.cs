namespace AwAUI.UI.UnitTests;

partial class Logged : Component
{
    public string Name { get; set; } = "";

    public List<string> Log { get; set; } = new();

    public NodeList? Children { get; set; }

    public object? Dep { get; set; }

    protected override Node Build()
    {
        Log.Add("build " + Name);
        var name = Name;
        var log = Log;
        UseEffect(() =>
        {
            log.Add("effect " + name + " " + Dep);
            return () => log.Add("cleanup " + name);
        }, Dep);
        UseEffect(() =>
        {
            log.Add("once " + name);
            return null;
        });
        return new Div { Id = Name, Children = Children ?? new NodeList() };
    }

    protected override void OnMount() => Log.Add("mount " + Name);

    protected override void OnUnmount() => Log.Add("unmount " + Name + (Root!.RootElement.IsInclusiveAncestorOf(GetRootElements()[0]) ? " attached" : " detached"));
}

partial class Theme : Component
{
    public int Builds;

    protected override Node Build()
    {
        Builds++;
        return new Text(Use<string>());
    }
}

partial class Thrower : Component
{
    public State<bool>? Fail { get; set; }

    protected override Node Build()
    {
        if (Fail?.Value ?? true) throw new InvalidOperationException("boom");
        return new Div { Id = "ok", OnClick = _ => throw new FormatException("click boom") };
    }
}

partial class SelfWriter : Component
{
    readonly State<int> _n = new(0);

    protected override Node Build()
    {
        int v = _n.Value;
        _n.Value = v + 1; // AW1005：Build 中写入自己读取的 State
        return new Text(v.ToString());
    }
}

partial class ConvergingWriter : Component
{
    readonly State<int> _n = new(0);

    public int Builds;

    protected override Node Build()
    {
        Builds++;
        int v = _n.Value;
        if (v < 2) _n.Value = v + 1;
        return new Text(v.ToString());
    }
}

public class ComponentTests
{
    [Fact]
    public void Mount_and_effect_order_is_children_first_after_commit()
    {
        var log = new List<string>();
        var root = new UIRoot();
        root.Mount(new Logged { Name = "p", Log = log, Children = [new Logged { Name = "c1", Log = log }, new Logged { Name = "c2", Log = log }] });
        Assert.Equal(new[]
        {
            "build p", "build c1", "build c2",
            "mount c1", "effect c1 ", "once c1",
            "mount c2", "effect c2 ", "once c2",
            "mount p", "effect p ", "once p",
        }, log.ToArray());
    }

    [Fact]
    public void Effects_rerun_only_when_deps_change_and_cleanup_first()
    {
        var log = new List<string>();
        var dep = new State<int>(1);
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Logged { Name = "x", Log = log, Dep = dep.Value } });
        log.Clear();

        dep.Value = 1; // 不变
        root.RunFrame();
        Assert.Empty(log);

        dep.Value = 2;
        root.RunFrame();
        Assert.Equal(new[] { "build x", "cleanup x", "effect x 2" }, log.ToArray());
    }

    [Fact]
    public void Unmount_runs_children_first_cleanup_before_on_unmount_while_still_attached()
    {
        var log = new List<string>();
        var show = new State<bool>(true);
        var root = new UIRoot();
        root.Mount(new Lambda
        {
            Render = () => show.Value
                ? new Logged { Name = "p", Log = log, Children = [new Logged { Name = "c", Log = log }] }
                : new Div { Id = "empty" },
        });
        var pElement = root.RootElement.ById("p");
        log.Clear();
        show.Value = false;
        root.RunFrame();
        Assert.Equal(new[] { "cleanup c", "unmount c attached", "cleanup p", "unmount p attached" }, log.ToArray());
        Assert.False(pElement.IsAttached);
        Assert.Equal("div#empty", root.RootElement.Shape());
    }

    [Fact]
    public void Root_unmount_unmounts_everything()
    {
        var log = new List<string>();
        var root = new UIRoot();
        var top = new Logged { Name = "t", Log = log };
        root.Mount(top);
        Assert.True(top.IsMounted);
        log.Clear();
        root.Unmount();
        Assert.False(top.IsMounted);
        Assert.Equal(0, root.RootElement.ChildCount);
        Assert.Equal(new[] { "cleanup t", "unmount t attached" }, log.ToArray());
    }

    [Fact]
    public void Context_is_provided_and_consumers_rebuild_on_change()
    {
        var theme = new State<string>("light");
        var consumer = new Theme();
        var other = new Lambda { Render = () => new Div() };
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => Context.Provide(theme.Value, new Div { Children = [consumer, other] }) });
        Assert.Equal("light", root.RootElement.TextContent);
        theme.Value = "dark";
        root.RunFrame();
        Assert.Equal("dark", root.RootElement.TextContent);
        Assert.Equal(2, consumer.Builds);
        Assert.Equal(1, other.Builds);
    }

    [Fact]
    public void Missing_context_is_an_error()
    {
        var root = new UIRoot();
        var ex = Assert.Throws<InvalidOperationException>(() => root.Mount(new Theme()));
        Assert.Contains("String", ex.Message);
    }

    [Fact]
    public void Error_boundary_catches_build_errors_and_resets()
    {
        var fail = new State<bool>(false);
        var root = new UIRoot();
        var errors = new List<Exception>();
        var boundary = new ErrorBoundary
        {
            Children = [new Thrower { Fail = fail }],
            Fallback = ex => new Text("出错：" + ex.Message) { Id = "fallback" },
            OnError = errors.Add,
        };
        root.Mount(boundary);
        Assert.Equal("div#ok", root.RootElement.Shape());

        fail.Value = true;
        root.RunFrame(); // 同一帧内切换到后备界面
        Assert.Equal("text#fallback", root.RootElement.Shape());
        Assert.Equal("出错：boom", root.RootElement.TextContent);
        Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(boundary.Error);

        fail.Value = false;
        boundary.Reset();
        root.RunFrame();
        Assert.Equal("div#ok", root.RootElement.Shape());
        Assert.Null(boundary.Error);
    }

    [Fact]
    public void Error_boundary_catches_event_handler_errors()
    {
        var root = new UIRoot();
        var boundary = new ErrorBoundary { Children = [new Thrower { Fail = new State<bool>(false) }], Fallback = ex => new Text(ex.Message) };
        root.Mount(boundary);
        root.Input.Click(root.RootElement.ById("ok"));
        root.RunFrame();
        Assert.Equal("click boom", root.RootElement.TextContent);
    }

    [Fact]
    public void Unhandled_errors_surface_via_event_or_rethrow()
    {
        var root = new UIRoot();
        Assert.Throws<InvalidOperationException>(() => root.Mount(new Thrower()));

        var root2 = new UIRoot();
        var seen = new List<UIError>();
        root2.UnhandledError += seen.Add;
        root2.Mount(new Thrower());
        Assert.Single(seen);
        Assert.Equal(ErrorPhase.Build, seen[0].Phase);
        Assert.IsType<Thrower>(seen[0].Component);
    }

    [Fact]
    public void Writing_state_in_build_reports_AW1005_and_loop_is_stopped()
    {
        var root = new UIRoot();
        var diags = new List<UIDiagnostic>();
        var errors = new List<UIError>();
        root.Diagnostic += diags.Add;
        root.UnhandledError += errors.Add;
        root.Mount(new SelfWriter());
        Assert.Contains(diags, d => d.Code == "AW1005" && d.Severity == DiagnosticSeverity.Warning);
        int frames = root.RunUntilIdle(1000);
        Assert.True(frames < 1000);
        Assert.Contains(diags, d => d.Code == "AW1005" && d.Severity == DiagnosticSeverity.Error);
        Assert.Single(errors);
        Assert.False(root.NeedsFrame);
    }

    [Fact]
    public void Build_writes_that_converge_settle_in_following_frames()
    {
        var root = new UIRoot();
        var c = new ConvergingWriter();
        root.Mount(c);
        root.RunUntilIdle();
        Assert.Equal("2", root.RootElement.TextContent);
        Assert.Equal(3, c.Builds); // 每帧最多一次
    }

    [Fact]
    public void Init_prop_change_remounts_component()
    {
        var step = new State<int>(1);
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Counter { Step = step.Value } });
        var counter = root.RootElement.ByClass("counter");
        root.Input.Click(counter.ByTag("button"));
        root.RunFrame();
        Assert.Equal("当前计数：1", counter.ByTag("text").TextContent);

        step.Value = 5; // init Prop 无法在实例上更新：重新挂载，State 重置
        root.RunFrame();
        var counter2 = root.RootElement.ByClass("counter");
        Assert.NotSame(counter, counter2);
        Assert.Equal("当前计数：0", counter2.ByTag("text").TextContent);
        root.Input.Click(counter2.ByTag("button"));
        root.RunFrame();
        Assert.Equal("当前计数：5", counter2.ByTag("text").TextContent);
    }

    [Fact]
    public void Reusing_a_mounted_component_instance_is_reported()
    {
        var shared = new Lambda { Render = () => new Div() };
        var root = new UIRoot();
        var errors = new List<UIError>();
        root.UnhandledError += errors.Add;
        root.Mount(new Lambda { Render = () => new Fragment { Children = [shared, new Div { Children = [shared] }] } });
        Assert.Single(errors);
    }

    [Fact]
    public void Props_change_adopts_into_live_instance()
    {
        var label = new State<string>("a");
        var root = new UIRoot();
        Reader? live = null;
        root.Mount(new Lambda { Render = () => new Reader { Label = label.Value } });
        live = (Reader)root.RootElement.Children[0].Owner!;
        label.Value = "b";
        root.RunFrame();
        Assert.Same(live, root.RootElement.Children[0].Owner);
        Assert.Equal("b", live.Label);
        Assert.Equal("b:0", root.RootElement.TextContent);
        Assert.Equal(2, live.Builds);
    }

    [Fact]
    public void Use_effect_outside_build_throws()
    {
        var c = new UseEffectOutside();
        Assert.Throws<InvalidOperationException>(() => c.Call());
    }
}

partial class UseEffectOutside : Component
{
    public void Call() => UseEffect(() => null);

    protected override Node Build() => new Div();
}
