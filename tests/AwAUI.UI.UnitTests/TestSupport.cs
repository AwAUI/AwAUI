using System.Collections.Concurrent;
using AwAUI.Platform;
using AwAUI.Threading;

namespace AwAUI.UI.UnitTests;

static class TestUtil
{
    /// <summary>按树序查找全部匹配的元素（不含根）。</summary>
    public static List<Element> FindAll(this Element root, Func<Element, bool> predicate)
    {
        var result = new List<Element>();
        var stack = new Stack<Element>();
        for (int i = root.Children.Count - 1; i >= 0; i--) stack.Push(root.Children[i]);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (predicate(e)) result.Add(e);
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
        return result;
    }

    public static Element ByClass(this Element root, string cls) => root.FindAll(e => e.HasClass(cls)).Single();

    public static List<Element> AllByClass(this Element root, string cls) => root.FindAll(e => e.HasClass(cls));

    public static Element ByTag(this Element root, string tag) => root.FindAll(e => e.Tag == tag).Single();

    public static Element ById(this Element root, string id) => root.FindAll(e => e.Id == id).Single();

    /// <summary>子元素的紧凑描述：tag#id 或文本，用于断言结构。</summary>
    public static string Shape(this Element e) =>
        string.Join(",", e.Children.Select(c => c is TextNodeElement t ? "'" + t.Text + "'" : c.Tag + (c.Id is null ? "" : "#" + c.Id)));

    public static RawKeyEvent KeyDown(PhysicalKey key, KeyModifiers mods = KeyModifiers.None) =>
        new(RawKeyEventType.Down, key, mods, false, 0, TimeSpan.Zero);

    public static RawKeyEvent KeyUp(PhysicalKey key, KeyModifiers mods = KeyModifiers.None) =>
        new(RawKeyEventType.Up, key, mods, false, 0, TimeSpan.Zero);

    public static RawPointerEvent Pointer(RawPointerEventType type, long id = 1, PointerType device = PointerType.Mouse,
        PointerButtons changed = PointerButtons.None, PointerButtons buttons = PointerButtons.None) =>
        new(type, device, id, new Point(1, 1), changed, buttons, KeyModifiers.None, TimeSpan.Zero);

    public static void Press(this UIRoot root, PhysicalKey key, KeyModifiers mods = KeyModifiers.None)
    {
        root.Input.DispatchKey(KeyDown(key, mods));
        root.Input.DispatchKey(KeyUp(key, mods));
    }
}

/// <summary>记录元素树变更通知。</summary>
sealed class RecordingObserver : ElementTreeObserver
{
    public readonly List<string> Events = new();

    public int Count(string prefix) => Events.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));

    public void Clear() => Events.Clear();

    static string N(Element e) => e is TextNodeElement t ? "'" + t.Text + "'" : e.Id ?? e.Tag;

    public override void OnChildInserted(Element parent, Element child) => Events.Add("insert " + N(child));
    public override void OnChildRemoved(Element parent, Element child) => Events.Add("remove " + N(child));
    public override void OnChildMoved(Element parent, Element child) => Events.Add("move " + N(child));
    public override void OnClassesChanged(Element element) => Events.Add("class " + N(element));
    public override void OnIdChanged(Element element, string? oldId) => Events.Add("id " + N(element));
    public override void OnAttributeChanged(Element element, string name, string? oldValue) => Events.Add("attr " + name);
    public override void OnTextChanged(Element element) => Events.Add("text " + N(element));
    public override void OnPseudoStatesChanged(Element element, PseudoStates oldStates) => Events.Add("state " + N(element));
    public override void OnPropertyChanged(Element element, string name) => Events.Add("prop " + name);
    public override void OnInlineStyleChanged(Element element) => Events.Add("style " + N(element));
}

/// <summary>单线程测试调度器：Post 入队，Pump 在拥有线程上执行。</summary>
sealed class TestDispatcher : IDispatcher
{
    readonly int _thread = Environment.CurrentManagedThreadId;
    readonly ConcurrentQueue<Action> _queue = new();

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _thread;

    public void Post(Action action) => _queue.Enqueue(action);

    public int Pending => _queue.Count;

    public int Pump()
    {
        int n = 0;
        while (_queue.TryDequeue(out var a))
        {
            a();
            n++;
        }
        return n;
    }
}

/// <summary>根据给定函数构建的通用测试组件（Build 次数计数）。</summary>
partial class Lambda : Component
{
    public Func<Node> Render { get; set; } = () => new Fragment();

    public int Builds;

    protected override Node Build()
    {
        Builds++;
        return Render();
    }
}
