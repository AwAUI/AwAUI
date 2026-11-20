using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.State;

/// <summary>受控 / 非受控状态与组件模型的集成：在真实的 <see cref="UIRoot"/> 中挂载组件、触发重建。</summary>
public partial class ControllableStateTests
{
    sealed class Disclosure : Component
    {
        public bool? Open { get; set; }
        public bool DefaultOpen { get; set; }
        public Action<bool>? OnOpenChange { get; set; }

        public readonly ControllableState<bool> State = new();
        public bool Rendered;
        public int Builds;

        public void ForceRebuild() => Invalidate();

        protected override Node Build()
        {
            Builds++;
            Rendered = State.Sync(Open, DefaultOpen, OnOpenChange);
            return new Div { Class = Rendered ? "open" : "closed" };
        }

        protected override bool PropsEqual(Component other)
        {
            var o = (Disclosure)other;
            return Open == o.Open && DefaultOpen == o.DefaultOpen && Equals(OnOpenChange, o.OnOpenChange);
        }

        protected override void AdoptProps(Component other)
        {
            var o = (Disclosure)other;
            Open = o.Open;
            DefaultOpen = o.DefaultOpen;
            OnOpenChange = o.OnOpenChange;
        }
    }

    /// <summary>父组件：<see cref="Controlled"/> 为 true 时把自己的 State 作为受控值传下去。</summary>
    sealed partial class Host : Component
    {
        public readonly State<bool> ParentOpen = new(false);
        public readonly State<bool> Controlled = new(true);
        public readonly List<bool> Changes = new();
        public Disclosure? Child;
        public bool Accept = true;

        protected override Node Build()
        {
            bool controlled = Controlled.Value;
            bool open = ParentOpen.Value;
            var d = new Disclosure
            {
                Open = controlled ? open : null,
                OnOpenChange = OnChange,
            };
            // 第一次 Build 的对象成为存活实例；之后的只是 Props 载体
            Child ??= d;
            return d;
        }

        void OnChange(bool v)
        {
            Changes.Add(v);
            if (Accept) ParentOpen.Value = v;
        }
    }

    [Fact]
    public void Uncontrolled_uses_default_then_internal_state()
    {
        var root = new UIRoot();
        var changes = new List<bool>();
        var d = new Disclosure { DefaultOpen = true, OnOpenChange = changes.Add };
        root.Mount(d);
        Assert.True(d.Rendered);
        Assert.False(d.State.IsControlled);
        d.State.Set(false);
        Assert.True(root.NeedsFrame);
        root.RunFrame();
        Assert.False(d.Rendered);
        Assert.Equal(new[] { false }, changes);
        // 相同的值不触发回调与重建
        int builds = d.Builds;
        d.State.Set(false);
        root.RunFrame();
        Assert.Equal(builds, d.Builds);
        Assert.Single(changes);
    }

    [Fact]
    public void Default_value_only_applies_initially()
    {
        var root = new UIRoot();
        var d = new Disclosure { DefaultOpen = false };
        root.Mount(d);
        Assert.False(d.Rendered);
        d.DefaultOpen = true;
        d.ForceRebuild();
        root.RunFrame();
        Assert.True(d.Builds >= 2);
        Assert.False(d.Rendered);
        Assert.False(d.State.Value);
    }

    [Fact]
    public void Controlled_value_comes_from_parent_and_set_only_calls_back()
    {
        var root = new UIRoot();
        var host = new Host();
        root.Mount(host);
        var child = FindChild(host);
        Assert.False(child.Rendered);
        Assert.True(child.State.IsControlled);

        // 父级拒绝变更：值不变
        host.Accept = false;
        child.State.Set(true);
        root.RunUntilIdle();
        Assert.False(child.Rendered);
        Assert.Equal(new[] { true }, host.Changes);

        // 父级接受：经由属性更新
        host.Accept = true;
        child.State.Set(true);
        root.RunUntilIdle();
        Assert.True(child.Rendered);
        Assert.Equal(new[] { true, true }, host.Changes);

        // 与受控值相同：不回调
        child.State.Set(true);
        Assert.Equal(2, host.Changes.Count);
    }

    [Fact]
    public void Switching_controlled_to_uncontrolled_is_flagged_and_keeps_last_value()
    {
        var root = new UIRoot();
        var host = new Host();
        root.Mount(host);
        var child = FindChild(host);
        host.ParentOpen.Value = true;
        root.RunUntilIdle();
        Assert.True(child.Rendered);
        host.Controlled.Value = false;
        root.RunUntilIdle();
        Assert.True(child.State.ModeSwitched);
        Assert.False(child.State.IsControlled);
        Assert.True(child.Rendered);   // 从最后的受控值继续
        child.State.Set(false);
        root.RunUntilIdle();
        Assert.False(child.Rendered);
    }

    [Fact]
    public void Nullable_reference_overload_treats_null_as_uncontrolled()
    {
        var s = new ControllableState<string>();
        Assert.Equal("a", s.Sync(null, "a", null));
        Assert.False(s.IsControlled);
        var c = new ControllableState<string>();
        Assert.Equal("x", c.Sync("x", "a", null));
        Assert.True(c.IsControlled);
    }

    [Fact]
    public void Set_before_first_sync_initializes()
    {
        var s = new ControllableState<int>();
        int called = 0;
        s.Set(3);
        Assert.Equal(3, s.Value);
        Assert.Equal(3, s.Sync(false, 0, 99, _ => called++));
        Assert.Equal(0, called);
    }

    static Disclosure FindChild(Host host) => host.Child!;
}
