using System;
using System.Collections.Generic;
using AwAUI.Platform;
using static AwAUI.Win32.Win32UiaNative;

namespace AwAUI.Win32;

/// <summary>
/// Win32 UI Automation 桥（design/10、design/04）：把语义树快照映射成一棵 COM provider 树，
/// 响应 WM_GETOBJECT，并把辅助技术发起的动作封送回 UI 线程。
/// </summary>
internal sealed unsafe class Win32AccessibilityBridge : AccessibilityBridge
{
    static readonly object s_logGate = new();
    static volatile bool s_available = true;
    static readonly Lazy<bool> s_enabled = new(ReadEnabled);

    readonly Win32Platform _platform;
    readonly Win32Window _window;
    readonly Win32AccessibilityProviderCore _core = new();
    readonly object _providersGate = new();
    readonly Dictionary<int, Win32UiaNode> _providers = new();

    volatile bool _disposed;
    IntPtr _hwnd;

    internal Win32AccessibilityBridge(Win32Platform platform, Win32Window window)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _core.ActionHandler = ExecuteAction;
    }

    /// <summary>环境变量 <c>AWAUI_WIN32_UIA=0</c> 关闭桥；UIA 调用失败会全局关闭。</summary>
    internal static bool IsEnabled => s_available && s_enabled.Value;

    internal static bool IsRootObjectRequest(IntPtr lParam) =>
        unchecked((int)lParam.ToInt64()) == Win32UiaConstants.RootObjectId;

    internal Win32AccessibilityProviderCore Core => _core;
    internal int RootId => _core.RootId;

    /// <summary>窗口句柄（根节点的 HostRawElementProvider 需要它）。</summary>
    internal IntPtr WindowHandle => _hwnd;

    static bool ReadEnabled()
    {
        var value = Environment.GetEnvironmentVariable("AWAUI_WIN32_UIA");
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>语义树快照更新（UI 线程）：按 Version 增量重建，并只对变化发事件。</summary>
    public override void Update(AccessibilitySnapshot snapshot)
    {
        if (_disposed || !IsEnabled) return;
        if (!_core.TryUpdate(snapshot, out var previous)) return;
        var current = _core.Index;
        if (current is null) return;

        RefreshProviders(current);

        if (previous is not null)
        {
            var diff = Win32AccessibilityDiff.Compute(previous, current);
            RaiseEvents(diff, previous, current);
        }
    }

    void RefreshProviders(Win32AccessibilityIndex current)
    {
        lock (_providersGate)
        {
            foreach (var entry in current.Entries.Values)
            {
                if (!_providers.TryGetValue(entry.Id, out var provider))
                {
                    provider = new Win32UiaNode(this, entry.Id);
                    _providers.Add(entry.Id, provider);
                }
                provider.UpdateEntry(entry);
            }

            List<int>? stale = null;
            foreach (var pair in _providers)
            {
                if (current.TryGet(pair.Key, out _)) continue;
                (stale ??= new List<int>()).Add(pair.Key);
            }
            if (stale is null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                if (!_providers.TryGetValue(stale[i], out var provider)) continue;
                _providers.Remove(stale[i]);
                provider.Detach();
            }
        }
    }

    internal Win32UiaNode? GetProvider(int nodeId)
    {
        if (_disposed) return null;
        lock (_providersGate)
        {
            if (_providers.TryGetValue(nodeId, out var existing)) return existing;
            var entry = _core.GetEntry(nodeId);
            if (entry is null) return null;
            var provider = new Win32UiaNode(this, nodeId);
            provider.UpdateEntry(entry);
            _providers.Add(nodeId, provider);
            return provider;
        }
    }

    internal IntPtr GetProviderPointer(int nodeId, bool addRef)
    {
        // 在同一把锁内完成查找/创建与 AddRef，避免 UI 线程移除节点时与 UIA 线程竞争。
        lock (_providersGate)
        {
            var provider = GetProvider(nodeId);
            return provider?.GetInterfacePointer(Win32ComInterfaceKind.Simple, addRef) ?? IntPtr.Zero;
        }
    }

    internal IntPtr GetRootPointer(bool addRef) => GetProviderPointer(_core.RootId, addRef);

    /// <summary>动作请求：UIA 线程 → 调度器 → UI 线程执行。</summary>
    internal bool RequestAction(int nodeId, AccessibilityActionKind action, string? value)
    {
        if (_disposed || !IsEnabled) return false;
        var entry = _core.GetEntry(nodeId);
        if (entry is null) return false;
        var request = new AccessibilityActionRequest(entry.StringId, action, value);

        if (_platform.Dispatcher.CheckAccess())
            return RaiseActionRequested(request);

        try
        {
            _platform.Dispatcher.Post(() =>
            {
                if (!_disposed) RaiseActionRequested(request);
            });
            return true;
        }
        catch (Exception e)
        {
            LogOnce("posting accessibility action failed: " + e.Message);
            return false;
        }
    }

    bool ExecuteAction(int nodeId, AccessibilityActionKind action, string? value) =>
        RequestAction(nodeId, action, value);

    /// <summary>客户区原点的屏幕物理像素与 DIP 客户区矩形（UI 线程调用）。</summary>
    internal void RefreshGeometry(IntPtr hwnd, double scale)
    {
        if (_disposed) return;
        _hwnd = hwnd;
        var origin = new Win32UiaPoint();
        try
        {
            if (hwnd == IntPtr.Zero || ClientToScreen(hwnd, ref origin) == 0)
            {
                origin.X = 0;
                origin.Y = 0;
            }
        }
        catch (Exception e)
        {
            LogOnce("ClientToScreen failed: " + e.Message);
            origin.X = 0;
            origin.Y = 0;
        }

        var size = _window.ClientSize;
        _core.Geometry = new Win32AccessibilityClientGeometry((float)scale, origin.X, origin.Y);
        _core.ClientBounds = new Rect(0, 0, size.Width, size.Height);
    }

    /// <summary>WM_GETOBJECT：把根 provider 交给 UIA。失败返回 <see cref="IntPtr.Zero"/> 并记录。</summary>
    internal IntPtr HandleGetObject(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed || !IsEnabled) return IntPtr.Zero;
        if (hwnd != IntPtr.Zero) _hwnd = hwnd;
        // 借用桥持有的引用：UIA 内部会按需 AddRef，窗口销毁时由 UiaDisconnectProvider 结束。
        var pointer = GetRootPointer(addRef: false);
        if (pointer == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var result = UiaReturnRawElementProvider(hwnd, wParam, lParam, pointer);
            if (result != IntPtr.Zero) return result;
            LogOnce("UiaReturnRawElementProvider returned 0");
        }
        catch (Exception e)
        {
            Disable(e);
        }
        return IntPtr.Zero;
    }

    /// <summary>窗口获得焦点时通知 UIA 当前焦点元素（失去焦点由系统转到其他窗口处理）。</summary>
    internal void OnWindowFocusChanged(bool focused)
    {
        if (!focused || _disposed || !IsEnabled) return;
        int nodeId = _core.FindFocusedId(_core.RootId);
        if (nodeId < 0) return;
        RaiseAutomationEvent(nodeId, Win32UiaEventIds.AutomationFocusChanged);
    }

    void RaiseEvents(Win32AccessibilityDiff diff, Win32AccessibilityIndex previous, Win32AccessibilityIndex current)
    {
        for (int i = 0; i < diff.Structures.Count; i++) RaiseStructureChanged(diff.Structures[i]);
        for (int i = 0; i < diff.Properties.Count; i++)
            RaisePropertyChanged(diff.Properties[i], previous, current);
        if (diff.FocusChanged) RaiseAutomationEvent(diff.FocusedNodeId, Win32UiaEventIds.AutomationFocusChanged);
        for (int i = 0; i < diff.Notifications.Count; i++) RaiseNotification(diff.Notifications[i]);
    }

    void RaiseStructureChanged(Win32AccessibilityStructureChange change)
    {
        var pointer = GetProviderPointer(change.ParentId, addRef: false);
        if (pointer == IntPtr.Zero) return;
        var childIds = change.ChildIds ?? Array.Empty<int>();
        var runtimeIds = new int[childIds.Length * 2];
        for (int i = 0; i < childIds.Length; i++)
        {
            runtimeIds[i * 2] = Win32UiaConstants.AppendRuntimeId;
            runtimeIds[i * 2 + 1] = childIds[i];
        }
        try
        {
            fixed (int* ids = runtimeIds)
                UiaRaiseStructureChangedEvent(pointer, change.ChangeType, ids, runtimeIds.Length);
        }
        catch (Exception e)
        {
            LogOnce("UiaRaiseStructureChangedEvent failed: " + e.Message);
        }
    }

    void RaisePropertyChanged(Win32AccessibilityPropertyChange change,
        Win32AccessibilityIndex previous, Win32AccessibilityIndex current)
    {
        var pointer = GetProviderPointer(change.NodeId, addRef: false);
        if (pointer == IntPtr.Zero) return;
        var oldValue = ToVariant(ReadProperty(previous, change.NodeId, change.PropertyId), out var oldBstr);
        var newValue = ToVariant(ReadProperty(current, change.NodeId, change.PropertyId), out var newBstr);
        try
        {
            UiaRaiseAutomationPropertyChangedEvent(pointer, change.PropertyId, oldValue, newValue);
        }
        catch (Exception e)
        {
            LogOnce("UiaRaiseAutomationPropertyChangedEvent failed: " + e.Message);
        }
        finally
        {
            if (oldBstr != IntPtr.Zero) SysFreeString(oldBstr);
            if (newBstr != IntPtr.Zero) SysFreeString(newBstr);
        }
    }

    void RaiseNotification(Win32AccessibilityNotification notification)
    {
        var pointer = GetProviderPointer(notification.NodeId, addRef: false);
        if (pointer == IntPtr.Zero) return;
        var text = AllocBstr(notification.Text);
        try
        {
            int processing = notification.Politeness == AccessibilityPoliteness.Assertive
                ? Win32UiaConstants.NotificationProcessingImportantMostRecent
                : Win32UiaConstants.NotificationProcessingMostRecent;
            UiaRaiseNotificationEvent(pointer, Win32UiaConstants.NotificationKindOther, processing, text, IntPtr.Zero);
        }
        catch (EntryPointNotFoundException)
        {
            // Win10 1709 之前没有通知 API：退化为 Name 属性变化事件。
            RaiseNameFallback(pointer);
        }
        catch (Exception e)
        {
            LogOnce("UiaRaiseNotificationEvent failed: " + e.Message);
            RaiseNameFallback(pointer);
        }
        finally
        {
            if (text != IntPtr.Zero) SysFreeString(text);
        }
    }

    static void RaiseNameFallback(IntPtr pointer)
    {
        try
        {
            UiaRaiseAutomationPropertyChangedEvent(pointer, Win32UiaPropertyIds.Name,
                Win32UiaVariant.Empty, Win32UiaVariant.Empty);
        }
        catch (Exception e)
        {
            LogOnce("name fallback event failed: " + e.Message);
        }
    }

    void RaiseAutomationEvent(int nodeId, int eventId)
    {
        if (nodeId < 0) return;
        var pointer = GetProviderPointer(nodeId, addRef: false);
        if (pointer == IntPtr.Zero) return;
        try
        {
            UiaRaiseAutomationEvent(pointer, eventId);
        }
        catch (Exception e)
        {
            LogOnce("UiaRaiseAutomationEvent failed: " + e.Message);
        }
    }

    Win32AccessibilityValue ReadProperty(Win32AccessibilityIndex index, int nodeId, int propertyId)
    {
        // 事件只使用当前桥的几何；属性事件的值主要用于日志/调试，客户端会重新查询。
        return GetProperty(index, nodeId, propertyId);
    }

    Win32AccessibilityValue GetProperty(Win32AccessibilityIndex index, int nodeId, int propertyId) =>
        Win32AccessibilityPropertyReader.GetProperty(index, nodeId, propertyId, _core.Geometry, _core.ClientBounds);

    static Win32UiaVariant ToVariant(Win32AccessibilityValue value, out IntPtr bstr)
    {
        bstr = IntPtr.Zero;
        switch (value.Kind)
        {
            case Win32AccessibilityValueKind.String:
                if (string.IsNullOrEmpty(value.StringValue)) return Win32UiaVariant.FromBstr(IntPtr.Zero);
                bstr = AllocBstr(value.StringValue);
                return Win32UiaVariant.FromBstr(bstr);
            case Win32AccessibilityValueKind.Int32:
                return Win32UiaVariant.FromInt(value.IntValue);
            case Win32AccessibilityValueKind.Double:
                return Win32UiaVariant.FromDouble(value.DoubleValue);
            case Win32AccessibilityValueKind.Boolean:
                return Win32UiaVariant.FromBool(value.BoolValue);
            default:
                return Win32UiaVariant.Empty;
        }
    }

    static void Disable(Exception e)
    {
        s_available = false;
        LogOnce("UIA unavailable, falling back to no accessibility bridge: " + e.Message);
    }

    static void LogOnce(string message)
    {
        lock (s_logGate)
        {
            try { Console.Error.WriteLine("[AwAUI.Win32.UIA] " + message); } catch { }
        }
    }

    /// <summary>窗口销毁时断开与 UIA 的连接并释放桥持有的 provider 引用。</summary>
    internal void Dispose()
    {
        if (_disposed) return;
        var rootPointer = GetRootPointer(addRef: false);
        _disposed = true;
        if (rootPointer != IntPtr.Zero)
        {
            try { UiaDisconnectProvider(rootPointer); }
            catch (EntryPointNotFoundException)
            {
                try { UiaDisconnectAllProviders(); }
                catch { /* Win7 没有断开 API；进程退出时由系统清理 */ }
            }
            catch (Exception e) { LogOnce("UiaDisconnectProvider failed: " + e.Message); }
        }

        lock (_providersGate)
        {
            foreach (var provider in _providers.Values) provider.Detach();
            _providers.Clear();
        }
    }
}
