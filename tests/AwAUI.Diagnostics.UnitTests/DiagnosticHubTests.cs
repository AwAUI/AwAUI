using System.Runtime.CompilerServices;

namespace AwAUI.Diagnostics.UnitTests;

// DiagnosticHub 是进程级静态状态：每个测试使用各自的自定义类别与事件类型，避免并行测试互相干扰。
public class DiagnosticHubTests
{
    readonly struct EventA { public EventA(int v) => Value = v; public int Value { get; } }
    readonly struct EventB { public EventB(string s) => Text = s; public string Text { get; } }
    readonly struct EventC { }

    [Fact]
    public void Feature_switch_defaults_to_enabled()
    {
        Assert.True(DiagnosticsFeature.IsEnabled);
        Assert.Equal("AwAUI.Diagnostics.Enabled", DiagnosticsFeature.SwitchName);
    }

    [Fact]
    public void Enable_is_reference_counted()
    {
        var cat = DiagnosticCategory.Custom + 1;
        Assert.False(DiagnosticHub.IsEnabled(cat));
        var t1 = DiagnosticHub.Enable(cat);
        var t2 = DiagnosticHub.Enable(cat);
        Assert.True(DiagnosticHub.IsEnabled(cat));
        Assert.NotEqual(0, DiagnosticHub.EnabledMask & (1L << (int)cat));
        t1.Dispose();
        t1.Dispose(); // 重复释放无效
        Assert.True(DiagnosticHub.IsEnabled(cat));
        t2.Dispose();
        Assert.False(DiagnosticHub.IsEnabled(cat));
    }

    [Fact]
    public void Write_delivers_typed_events_only_when_enabled()
    {
        var cat = DiagnosticCategory.Custom + 2;
        var seen = new List<int>();
        using var sub = DiagnosticHub.Subscribe((in EventA e) => seen.Add(e.Value));
        Assert.True(DiagnosticHub.HasSubscribers<EventA>());

        DiagnosticHub.Write(cat, new EventA(1));          // 未启用：丢弃
        using (DiagnosticHub.Enable(cat))
        {
            if (DiagnosticHub.IsEnabled(cat)) DiagnosticHub.Write(cat, new EventA(2));
            DiagnosticHub.Write(cat, new EventA(3));
        }
        DiagnosticHub.Write(cat, new EventA(4));
        Assert.Equal(new[] { 2, 3 }, seen);
    }

    [Fact]
    public void Subscriber_exceptions_are_swallowed_and_unsubscribe_works()
    {
        var cat = DiagnosticCategory.Custom + 3;
        var seen = new List<string>();
        var bad = DiagnosticHub.Subscribe((in EventB e) => throw new InvalidOperationException());
        var good = DiagnosticHub.Subscribe((in EventB e) => seen.Add(e.Text));
        using (DiagnosticHub.Enable(cat))
        {
            DiagnosticHub.Write(cat, new EventB("x"));
            good.Dispose();
            DiagnosticHub.Write(cat, new EventB("y"));
        }
        bad.Dispose();
        Assert.Equal(new[] { "x" }, seen);
        Assert.False(DiagnosticHub.HasSubscribers<EventB>());
    }

    [Fact]
    public void Invalid_category_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticHub.Enable((DiagnosticCategory)64));
        Assert.False(DiagnosticHub.HasSubscribers<EventC>());
    }

    [Fact]
    public void Out_of_range_categories_never_alias_valid_bits()
    {
        var cat = DiagnosticCategory.Custom + 21;
        using (DiagnosticHub.Enable(cat))
        {
            Assert.True(DiagnosticHub.IsEnabled(cat));
            // 以前 (int)category & 63 会让 Custom+21+64 读到 Custom+21 的位
            Assert.False(DiagnosticHub.IsEnabled(cat + 64));
            Assert.False(DiagnosticHub.IsEnabled((DiagnosticCategory)(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticHub.Enable(cat + 64));
            Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticHub.Enable((DiagnosticCategory)(-1)));
        }
    }
}

public class ObjectRegistryTests
{
    [Fact]
    public void Ids_are_stable_and_unique()
    {
        var reg = new ObjectRegistry();
        object a = new(), b = new();
        long ia = reg.GetOrAddId(a);
        long ib = reg.GetOrAddId(b);
        Assert.Equal(1, ia);
        Assert.Equal(2, ib);
        Assert.Equal(ia, reg.GetOrAddId(a));
        Assert.True(reg.TryGetId(b, out long got) && got == ib);
        Assert.False(reg.TryGetId(new object(), out _));
        Assert.True(reg.TryGetObject(ia, out object? back) && ReferenceEquals(back, a));
        Assert.True(reg.TryGetObject<object>(ib, out var typed) && ReferenceEquals(typed, b));
        Assert.False(reg.TryGetObject<string>(ib, out _));
    }

    [Fact]
    public void Removed_objects_get_new_ids_and_old_ids_never_resolve()
    {
        var reg = new ObjectRegistry();
        var a = new object();
        long first = reg.GetOrAddId(a);
        Assert.True(reg.Remove(a));
        Assert.False(reg.Remove(a));
        Assert.False(reg.TryGetObject(first, out _));
        long second = reg.GetOrAddId(a);
        Assert.True(second > first);
        Assert.False(reg.TryGetObject(first, out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long RegisterTemporary(ObjectRegistry reg) => reg.GetOrAddId(new object());

    // 在独立方法中取回对象，避免未优化代码里的临时变量延长对象寿命
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool IsAlive(ObjectRegistry reg, long id) => reg.TryGetObject(id, out _);

    [Fact]
    public void Holds_only_weak_references_and_does_not_reuse_ids()
    {
        var reg = new ObjectRegistry();
        long id = RegisterTemporary(reg);
        for (int i = 0; i < 3 && IsAlive(reg, id); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(IsAlive(reg, id));
        Assert.True(reg.GetOrAddId(new object()) > id);
    }

    [Fact]
    public void Purge_removes_dead_entries()
    {
        var reg = new ObjectRegistry();
        for (int i = 0; i < 10; i++) RegisterTemporary(reg);
        var alive = new object();
        reg.GetOrAddId(alive);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        reg.Purge();
        Assert.True(reg.Count >= 1);
        Assert.True(reg.Count < 11);
        GC.KeepAlive(alive);
    }
}
