using System;
using System.Collections.Generic;
using AwAUI.Browser.Interop;
using AwAUI.Platform;

namespace AwAUI.Browser.Accessibility;

/// <summary>
/// Shadow ARIA DOM bridge for the Web platform (design/04 "Web specifics", design/10 "platform bridges").
/// <para>
/// A canvas is invisible to screen readers, so the JS glue maintains a transparent, pointer-events-free
/// DOM tree above it: every semantic node becomes a div with role / aria-* attributes and <c>tabindex="-1"</c>.
/// This bridge encodes each snapshot as compact JSON for the glue (<c>a11yUpdate</c>) and forwards actions
/// bubbling from the DOM back to the framework through <see cref="AccessibilityBridge.RaiseActionRequested"/>.
/// </para>
/// <para>
/// The bridge is not created when <c>AWAUI_BROWSER_A11Y=0</c> (the semantic tree then stays lazy and no extra
/// DOM is added). Strings reach the DOM only through textContent / setAttribute. DOM actions are accepted only
/// when they use a known kind, a known node id, and an action declared by that node's latest snapshot.
/// </para>
/// </summary>
public sealed class BrowserAccessibilityBridge : AccessibilityBridge, IDisposable
{
    readonly int _handle;
    Dictionary<string, List<AccessibilityActionKind>> _actions = new Dictionary<string, List<AccessibilityActionKind>>(StringComparer.Ordinal);
    string? _lastJson;
    bool _enabled;
    bool _disposed;

    BrowserAccessibilityBridge(int handle) => _handle = handle;

    /// <summary>Whether the bridge is active and the JS-side shadow container exists.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>The native window handle whose shadow container this bridge drives (for diagnostics).</summary>
    public int Handle => _handle;

    /// <summary>
    /// Creates the bridge for a window. Returns null when disabled by <c>AWAUI_BROWSER_A11Y=0</c>,
    /// when the canvas is gone, or when the JS glue refuses to create a container.
    /// </summary>
    internal static BrowserAccessibilityBridge? TryCreate(int handle)
    {
        if (IsDisabledByEnvironment()) return null;
        var bridge = new BrowserAccessibilityBridge(handle);
        try
        {
            bridge._enabled = BrowserInterop.A11yAttach(handle);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 页面已卸载 / 句柄失效：无障碍是尽力而为的能力，不影响窗口本身。
            bridge._enabled = false;
        }
        return bridge._enabled ? bridge : null;
    }

    static bool IsDisabledByEnvironment()
    {
        try
        {
            return string.Equals(Environment.GetEnvironmentVariable("AWAUI_BROWSER_A11Y"), "0", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override void Update(AccessibilitySnapshot snapshot)
    {
        if (!_enabled || _disposed || snapshot is null) return;

        string json;
        try
        {
            json = BrowserAriaCodec.Encode(snapshot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return; // 编码失败时不把半截 JSON 交给 DOM
        }

        if (string.Equals(json, _lastJson, StringComparison.Ordinal)) return;

        Dictionary<string, List<AccessibilityActionKind>> actions;
        try
        {
            actions = BrowserAriaCodec.CollectActions(snapshot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return;
        }

        bool ok;
        try
        {
            ok = BrowserInterop.A11yUpdate(_handle, json);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ok = false;
        }

        if (!ok)
        {
            // 窗口已销毁或 JS 端拒绝模型：停止推送，避免每帧重复跨边界。
            _enabled = false;
            _lastJson = null;
            _actions.Clear();
            return;
        }

        _lastJson = json;
        _actions = actions;
    }

    /// <summary>DOM 侧发起动作（由 <see cref="BrowserCallbacks"/> 转入）。只接受已知编号与快照中声明过的动作。</summary>
    internal void HandleAction(string? nodeId, int kind, string? value)
    {
        if (!_enabled || _disposed || string.IsNullOrEmpty(nodeId)) return;
        if (!BrowserAriaCodec.TryParseAction(kind, out var action)) return;
        if (!BrowserAriaCodec.IsActionAllowed(_actions, nodeId, action)) return;

        string? argument = action == AccessibilityActionKind.SetValue
            ? BrowserAriaCodec.SanitizeValue(value)
            : null;

        RaiseActionRequested(new AccessibilityActionRequest(nodeId!, action, argument));
    }

    /// <summary>DOM 焦点移动到某个 ARIA 节点：转成框架的 Focus 动作（同样走白名单）。</summary>
    internal void HandleFocus(string? nodeId) => HandleAction(nodeId, BrowserAriaCodec.ActionFocus, null);

    /// <summary>Removes the shadow tree and its DOM listeners (also done by <c>detach</c> when the window closes).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _actions.Clear();
        _lastJson = null;
        try
        {
            BrowserInterop.A11yDetach(_handle);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 运行时已卸载时没有可清理的 DOM。
        }
    }
}
