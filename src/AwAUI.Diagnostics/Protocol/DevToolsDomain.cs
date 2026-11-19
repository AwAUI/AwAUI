using System;
using System.Collections.Generic;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// 协议 Domain 的基类（design/06 ②、design/20）。子类在 <see cref="RegisterMethods"/> 中显式登记方法（不使用反射），
/// 通过 <see cref="Emit"/> / <see cref="EmitCoalesced"/> 推送事件。
/// </summary>
/// <remarks>
/// <para>
/// 每个 Domain 自动拥有 <c>{Name}.enable</c> 与 <c>{Name}.disable</c>。启用是按会话的：事件只发给启用了该 Domain 的会话；
/// 第一个会话启用时调用 <see cref="OnEnabled"/> 并打开构造时声明的探针类别，最后一个会话停用时调用 <see cref="OnDisabled"/>
/// 并关闭它们 —— 「只开元素面板」时不付出性能面板的开销。
/// </para>
/// <para>线程：方法处理器在 Agent 的调度线程上执行；以 <c>runOnTargetThread: true</c> 登记的方法改在请求目标的 UI 线程
/// （<see cref="DevToolsTarget.Dispatcher"/>）上执行，响应也在那里发出。<see cref="Emit"/> 可在任意线程调用。</para>
/// </remarks>
public abstract class DevToolsDomain
{
    readonly DiagnosticCategory[] _categories;
    readonly List<IDisposable> _probeTokens = new();
    readonly object _gate = new();
    int _enabledSessions;

    protected DevToolsDomain(string name, params DiagnosticCategory[] probeCategories)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('.') >= 0) throw new ArgumentException("Invalid domain name.", nameof(name));
        Name = name;
        _categories = probeCategories ?? Array.Empty<DiagnosticCategory>();
    }

    /// <summary>Domain 名，如 "Tree"。</summary>
    public string Name { get; }

    /// <summary>所属 Agent（登记后设置）。</summary>
    public DevToolsAgent? Agent { get; internal set; }

    /// <summary>是否至少有一个会话启用了本 Domain。</summary>
    public bool IsEnabled => System.Threading.Volatile.Read(ref _enabledSessions) > 0;

    /// <summary>登记方法。方法名不含 Domain 前缀。</summary>
    protected abstract void RegisterMethods(DevToolsMethodTable methods);

    /// <summary>第一个会话启用时调用（在调度线程上）。</summary>
    protected virtual void OnEnabled() { }

    /// <summary>最后一个会话停用时调用。</summary>
    protected virtual void OnDisabled() { }

    /// <summary>某个会话启用本 Domain 后调用，可向该会话补发初始状态（例如缓存的日志）。</summary>
    protected virtual void OnSessionEnabled(DevToolsSession session) { }

    /// <summary>
    /// 帧边界钩子：<see cref="DevToolsAgent.FlushFrame"/> 在发送合并事件之前调用每个已启用的 Domain，
    /// Domain 可在此把本帧积累的变化汇总成一批事件（design/20「事件节流」）。
    /// </summary>
    protected virtual void OnFrame() { }

    internal void RegisterMethodsCore(DevToolsMethodTable methods) => RegisterMethods(methods);

    /// <summary>某个会话即将启用本 Domain（在它被标为已启用、响应发出之前）。供内建 Domain 准备按会话的状态。</summary>
    internal virtual void SessionEnabling(DevToolsSession session) { }

    /// <summary>某个会话停用了本 Domain（显式 disable、重连或分离）。</summary>
    internal virtual void SessionDisabledFor(DevToolsSession session) { }
    internal void SessionEnabledCore(DevToolsSession session) => OnSessionEnabled(session);
    internal void FrameCore() => OnFrame();

    /// <summary>立即向所有启用了本 Domain 的会话推送事件 <c>{Name}.{eventName}</c>。</summary>
    protected void Emit(string eventName, JsonValue? parameters, string? target = null)
    {
        if (!IsEnabled) return;
        Agent?.SendEvent(this, null, eventName, parameters, target);
    }

    /// <summary>只向指定会话推送事件（例如启用时补发）。</summary>
    protected void EmitTo(DevToolsSession session, string eventName, JsonValue? parameters, string? target = null)
    {
        Agent?.SendEvent(this, session, eventName, parameters, target);
    }

    /// <summary>
    /// 按帧合并的事件：同一 <paramref name="coalescingKey"/> 在一帧内多次调用只保留最后一次，
    /// 在下一次 <see cref="DevToolsAgent.FlushFrame"/> 时发出（保持首次入队的相对顺序）。
    /// </summary>
    protected void EmitCoalesced(string eventName, string coalescingKey, JsonValue? parameters, string? target = null)
    {
        if (!IsEnabled) return;
        Agent?.QueueCoalesced(this, eventName, coalescingKey, parameters, target);
    }

    internal void SessionEnabled()
    {
        lock (_gate)
        {
            if (_enabledSessions++ != 0) return;
            foreach (var c in _categories) _probeTokens.Add(DiagnosticHub.Enable(c));
        }
        OnEnabled();
    }

    internal void SessionDisabled(DevToolsSession session)
    {
        SessionDisabledFor(session);
        lock (_gate)
        {
            if (_enabledSessions == 0 || --_enabledSessions != 0) return;
            foreach (var t in _probeTokens) t.Dispose();
            _probeTokens.Clear();
        }
        OnDisabled();
    }
}

/// <summary>Domain 的方法表。</summary>
public sealed class DevToolsMethodTable
{
    readonly DevToolsDomain _domain;
    readonly Dictionary<string, MethodEntry> _target;

    internal DevToolsMethodTable(DevToolsDomain domain, Dictionary<string, MethodEntry> target)
    {
        _domain = domain;
        _target = target;
    }

    /// <summary>登记同步方法。</summary>
    /// <param name="name">不含 Domain 前缀的方法名。</param>
    /// <param name="handler">处理器。</param>
    /// <param name="requiresEnable">为 true 时，会话未启用本 Domain 则返回 -32003。</param>
    /// <param name="sinceMinor">方法在协议的哪个次版本引入；协商出的次版本更低时按「方法不存在」处理。</param>
    /// <param name="runOnTargetThread">
    /// 为 true 时，处理器在请求目标的 UI 线程（<see cref="DevToolsTarget.Dispatcher"/>，否则 Agent 的调度器）上执行，
    /// 响应也在那里发出；用于访问元素树等线程亲和状态的方法。
    /// </param>
    public void Add(string name, DevToolsMethodHandler handler, bool requiresEnable = false, int sinceMinor = 0, bool runOnTargetThread = false)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        AddEntry(name, new MethodEntry(_domain, handler, null, requiresEnable, sinceMinor, runOnTargetThread));
    }

    /// <summary>登记异步方法。</summary>
    public void AddAsync(string name, DevToolsAsyncMethodHandler handler, bool requiresEnable = false, int sinceMinor = 0)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        AddEntry(name, new MethodEntry(_domain, null, handler, requiresEnable, sinceMinor, false));
    }

    void AddEntry(string name, MethodEntry entry)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('.') >= 0) throw new ArgumentException("Invalid method name.", nameof(name));
        if (name is "enable" or "disable") throw new ArgumentException("enable/disable are provided by the agent.", nameof(name));
        string full = _domain.Name + "." + name;
        if (_target.ContainsKey(full)) throw new InvalidOperationException($"Method '{full}' is already registered.");
        _target.Add(full, entry);
    }
}

internal sealed class MethodEntry
{
    public MethodEntry(DevToolsDomain domain, DevToolsMethodHandler? sync, DevToolsAsyncMethodHandler? async, bool requiresEnable, int sinceMinor,
        bool runOnTargetThread)
    {
        Domain = domain; Sync = sync; Async = async; RequiresEnable = requiresEnable; SinceMinor = sinceMinor; RunOnTargetThread = runOnTargetThread;
    }

    public DevToolsDomain Domain { get; }
    public DevToolsMethodHandler? Sync { get; }
    public DevToolsAsyncMethodHandler? Async { get; }
    public bool RequiresEnable { get; }
    public int SinceMinor { get; }
    public bool RunOnTargetThread { get; }
}
