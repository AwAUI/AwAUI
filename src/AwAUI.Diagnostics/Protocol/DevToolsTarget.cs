using System;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Json;
using AwAUI.Threading;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>调试目标（一个窗口，一棵元素树）。请求的 <c>target</c> 字段按 <see cref="Id"/> 引用它。</summary>
/// <remarks>
/// UI 层把检查接缝挂到目标上：<see cref="Tree"/>（元素树与盒模型）、<see cref="Styles"/>（CSS）、<see cref="Overlay"/>（高亮与拾取），
/// 并用 <see cref="Dispatcher"/> 指明它们所属的 UI 线程。没有挂接缝的目标上，对应方法返回 -32004。
/// </remarks>
public sealed class DevToolsTarget
{
    readonly object _gate = new();
    InspectableTree? _tree;
    StyleInspector? _styles;
    OverlayHost? _overlay;

    public DevToolsTarget(string id, string title, Size size, float scale = 1f)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? string.Empty;
        Size = size;
        Scale = scale;
    }

    public string Id { get; }
    public string Title { get; set; }
    public Size Size { get; set; }
    public float Scale { get; set; }

    /// <summary>宿主可挂接任意关联对象（例如窗口或元素树根）。</summary>
    public object? Tag { get; set; }

    /// <summary>登记了本目标的 Agent（<see cref="DevToolsAgent.AddTarget"/> 后设置，移除后为 null）。</summary>
    public DevToolsAgent? Agent { get; internal set; }

    /// <summary>
    /// 本目标的 UI 线程。检查接缝的所有调用都在它上面进行：方法处理器被投递过去执行（响应在处理器完成后发出），
    /// 来自其他线程的变更通知也被投递过去。为 null 时使用 <see cref="DevToolsAgentOptions.Dispatcher"/>。
    /// </summary>
    public IDispatcher? Dispatcher { get; set; }

    /// <summary>元素树接缝。替换它相当于文档整体更新（推送 <c>Tree.documentUpdated</c>）。一个接缝实例只能属于一个目标。</summary>
    public InspectableTree? Tree
    {
        get => _tree;
        set
        {
            lock (_gate)
            {
                if (ReferenceEquals(_tree, value)) return;
                if (value?.Owner != null) throw new InvalidOperationException("The tree is already attached to a target.");
                if (_tree != null) _tree.Owner = null;
                if (value != null) value.Owner = this;
                _tree = value;
            }
            Agent?.OnTargetSeamsChanged(this, tree: true);
        }
    }

    /// <summary>样式接缝。一个接缝实例只能属于一个目标。</summary>
    public StyleInspector? Styles
    {
        get => _styles;
        set
        {
            lock (_gate)
            {
                if (ReferenceEquals(_styles, value)) return;
                if (value?.Owner != null) throw new InvalidOperationException("The style inspector is already attached to a target.");
                if (_styles != null) _styles.Owner = null;
                if (value != null) value.Owner = this;
                _styles = value;
            }
            Agent?.OnTargetSeamsChanged(this, tree: false);
        }
    }

    /// <summary>叠加层接缝。一个接缝实例只能属于一个目标。</summary>
    public OverlayHost? Overlay
    {
        get => _overlay;
        set
        {
            lock (_gate)
            {
                if (ReferenceEquals(_overlay, value)) return;
                if (value?.Owner != null) throw new InvalidOperationException("The overlay host is already attached to a target.");
                if (_overlay != null) _overlay.Owner = null;
                if (value != null) value.Owner = this;
                _overlay = value;
            }
            Agent?.OnTargetSeamsChanged(this, tree: false);
        }
    }

    /// <summary>实际使用的 UI 调度器：<see cref="Dispatcher"/>，否则 Agent 的调度器。</summary>
    internal IDispatcher? EffectiveDispatcher => Dispatcher ?? Agent?.Options.Dispatcher;

    /// <summary>在 UI 线程上执行：已在其上（或没有调度器）时立即执行，否则投递。异常被吞掉（诊断代码不得把异常抛进宿主）。</summary>
    internal void RunOnUIThread(Action action)
    {
        var d = EffectiveDispatcher;
        if (d is null || d.CheckAccess()) SafeRun(action);
        else d.Post(() => SafeRun(action));
    }

    static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception) { /* 诊断代码不得把异常抛进宿主 */ }
    }

    /// <summary>TargetInfo = { id, title, size: { width, height }, scale }。</summary>
    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["title"] = Title,
        ["size"] = new JsonObject { ["width"] = (double)Size.Width, ["height"] = (double)Size.Height },
        ["scale"] = (double)Scale,
    };
}
