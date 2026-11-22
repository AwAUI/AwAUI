using System;
using System.Globalization;
using AwAUI.Platform;

namespace AwAUI.Win32;

/// <summary>
/// 一个语义节点的 UIA provider。COM 回调（任意 UIA 线程）只读取不可变条目并调用桥；
/// 动作全部封送回 UI 线程。节点条目会在每次快照更新时由 UI 线程替换。
/// </summary>
internal sealed unsafe class Win32UiaNode : Win32ComProvider
{
    readonly Win32AccessibilityBridge _bridge;
    readonly int _nodeId;
    volatile Win32AccessibilityEntry? _entry;

    internal Win32UiaNode(Win32AccessibilityBridge bridge, int nodeId)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _nodeId = nodeId;
    }

    internal int NodeId => _nodeId;

    internal Win32AccessibilityEntry? Entry => _entry;

    internal void UpdateEntry(Win32AccessibilityEntry? entry)
    {
        _entry = entry;
    }

    internal void Detach()
    {
        _entry = null;
        ReleaseBridgeReference();
    }

    protected internal override bool SupportsInterface(Win32ComInterfaceKind kind)
    {
        switch (kind)
        {
            case Win32ComInterfaceKind.Simple:
            case Win32ComInterfaceKind.Fragment:
                return true;
            case Win32ComInterfaceKind.FragmentRoot:
                return _nodeId == _bridge.RootId;
            default:
                var patterns = _entry?.Patterns ?? Win32AccessibilityPattern.None;
                return PatternMatches(patterns, kind);
        }
    }

    static bool PatternMatches(Win32AccessibilityPattern patterns, Win32ComInterfaceKind kind)
    {
        switch (kind)
        {
            case Win32ComInterfaceKind.Invoke: return (patterns & Win32AccessibilityPattern.Invoke) != 0;
            case Win32ComInterfaceKind.Toggle: return (patterns & Win32AccessibilityPattern.Toggle) != 0;
            case Win32ComInterfaceKind.Value: return (patterns & Win32AccessibilityPattern.Value) != 0;
            case Win32ComInterfaceKind.RangeValue: return (patterns & Win32AccessibilityPattern.RangeValue) != 0;
            case Win32ComInterfaceKind.ExpandCollapse: return (patterns & Win32AccessibilityPattern.ExpandCollapse) != 0;
            case Win32ComInterfaceKind.SelectionItem: return (patterns & Win32AccessibilityPattern.SelectionItem) != 0;
            case Win32ComInterfaceKind.Selection: return (patterns & Win32AccessibilityPattern.Selection) != 0;
            case Win32ComInterfaceKind.Grid: return (patterns & Win32AccessibilityPattern.Grid) != 0;
            case Win32ComInterfaceKind.GridItem: return (patterns & Win32AccessibilityPattern.GridItem) != 0;
            case Win32ComInterfaceKind.ScrollItem: return (patterns & Win32AccessibilityPattern.ScrollItem) != 0;
            default: return false;
        }
    }

    internal int GetPatternProvider(int patternId, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        Win32ComInterfaceKind kind;
        switch (patternId)
        {
            case Win32UiaPatternIds.Invoke: kind = Win32ComInterfaceKind.Invoke; break;
            case Win32UiaPatternIds.Toggle: kind = Win32ComInterfaceKind.Toggle; break;
            case Win32UiaPatternIds.Value: kind = Win32ComInterfaceKind.Value; break;
            case Win32UiaPatternIds.RangeValue: kind = Win32ComInterfaceKind.RangeValue; break;
            case Win32UiaPatternIds.ExpandCollapse: kind = Win32ComInterfaceKind.ExpandCollapse; break;
            case Win32UiaPatternIds.SelectionItem: kind = Win32ComInterfaceKind.SelectionItem; break;
            case Win32UiaPatternIds.Selection: kind = Win32ComInterfaceKind.Selection; break;
            case Win32UiaPatternIds.Grid: kind = Win32ComInterfaceKind.Grid; break;
            case Win32UiaPatternIds.GridItem: kind = Win32ComInterfaceKind.GridItem; break;
            case Win32UiaPatternIds.ScrollItem: kind = Win32ComInterfaceKind.ScrollItem; break;
            case Win32UiaPatternIds.Text:
                // ITextProvider 未实现：文本通过 ValuePattern 暴露（见 notes）。
                return Win32UiaNative.S_OK;
            default:
                return Win32UiaNative.S_OK;
        }
        provider = GetInterfacePointer(kind, addRef: true);
        return Win32UiaNative.S_OK;
    }

    /// <summary>根节点返回窗口宿主 provider；其他节点不属于宿主，返回 null。</summary>
    internal int GetHostRawElementProvider(out IntPtr provider)
    {
        provider = IntPtr.Zero;
        if (_nodeId != _bridge.RootId) return Win32UiaNative.S_OK;
        var hwnd = _bridge.WindowHandle;
        if (hwnd == IntPtr.Zero) return Win32UiaNative.S_OK;
        return Win32UiaNative.UiaHostProviderFromHwnd(hwnd, out provider);
    }

    internal int GetPropertyValue(int propertyId, Win32UiaVariant* value)
    {
        if (value == null) return Win32UiaNative.E_INVALIDARG;
        var entry = _entry;
        if (entry is null)
        {
            *value = default;
            return Win32UiaNative.S_OK;
        }
        var property = _bridge.Core.GetProperty(_nodeId, propertyId);
        switch (property.Kind)
        {
            case Win32AccessibilityValueKind.String:
                var bstr = Win32UiaNative.AllocBstr(property.StringValue ?? "");
                if (bstr == IntPtr.Zero) return Win32UiaNative.E_FAIL;
                *value = Win32UiaVariant.FromBstr(bstr);
                return Win32UiaNative.S_OK;

            case Win32AccessibilityValueKind.Int32:
                *value = Win32UiaVariant.FromInt(property.IntValue);
                return Win32UiaNative.S_OK;

            case Win32AccessibilityValueKind.Double:
                *value = Win32UiaVariant.FromDouble(property.DoubleValue);
                return Win32UiaNative.S_OK;

            case Win32AccessibilityValueKind.Boolean:
                *value = Win32UiaVariant.FromBool(property.BoolValue);
                return Win32UiaNative.S_OK;

            case Win32AccessibilityValueKind.Rect:
                var rect = property.RectValue;
                var array = Win32UiaNative.CreateDoubleArray(new[]
                {
                    (double)rect.Left, (double)rect.Top, (double)rect.Width, (double)rect.Height,
                });
                if (array == IntPtr.Zero) return Win32UiaNative.E_FAIL;
                *value = Win32UiaVariant.FromDoubleArray(array);
                return Win32UiaNative.S_OK;

            case Win32AccessibilityValueKind.NodeId:
                if (property.NodeId < 0) { *value = default; return Win32UiaNative.S_OK; }
                var nodePointer = _bridge.GetProviderPointer(property.NodeId, addRef: true);
                *value = nodePointer == IntPtr.Zero
                    ? default
                    : Win32UiaVariant.FromUnknown(nodePointer);
                return Win32UiaNative.S_OK;

            case Win32AccessibilityValueKind.NodeIds:
                return FillNodeArray(property.NodeIds, value);

            default:
                *value = default;
                return Win32UiaNative.S_OK;
        }
    }

    int FillNodeArray(int[]? nodeIds, Win32UiaVariant* value)
    {
        *value = default;
        if (nodeIds is null) return Win32UiaNative.S_OK;
        var pointers = new IntPtr[nodeIds.Length];
        try
        {
            for (int i = 0; i < nodeIds.Length; i++)
                pointers[i] = _bridge.GetProviderPointer(nodeIds[i], addRef: true);
            var array = Win32UiaNative.CreateUnknownArray(pointers);
            if (array == IntPtr.Zero) return Win32UiaNative.E_FAIL;
            *value = Win32UiaVariant.FromUnknownArray(array);
            return Win32UiaNative.S_OK;
        }
        finally
        {
            // SafeArrayPutElement 已为数组内的接口指针 AddRef；释放我们持有的临时引用。
            for (int i = 0; i < pointers.Length; i++)
            {
                if (pointers[i] == IntPtr.Zero) continue;
                var provider = Win32ComProvider.FromPointer(pointers[i]);
                provider.ReleaseCore();
            }
        }
    }

    internal int Navigate(int direction, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        if (_bridge.Core.TryNavigate(_nodeId, direction, out int target) && target >= 0)
            provider = _bridge.GetProviderPointer(target, addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int GetRuntimeId(out IntPtr array)
    {
        array = Win32UiaNative.CreateIntArray(_bridge.Core.GetRuntimeId(_nodeId));
        return array == IntPtr.Zero ? Win32UiaNative.E_FAIL : Win32UiaNative.S_OK;
    }

    internal int GetBoundingRectangle(Win32UiaRect* rect)
    {
        if (rect == null) return Win32UiaNative.E_INVALIDARG;
        *rect = Win32UiaRect.From(_bridge.Core.GetBoundingRectangle(_nodeId));
        return Win32UiaNative.S_OK;
    }

    internal int SetFocus()
    {
        return RequestAction(AccessibilityActionKind.Focus);
    }

    internal int GetFragmentRoot(out IntPtr provider)
    {
        provider = _bridge.GetRootPointer(addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int ElementProviderFromPoint(double x, double y, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        int hit = _bridge.Core.HitTest(x, y, _bridge.RootId);
        if (hit >= 0) provider = _bridge.GetProviderPointer(hit, addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int GetFocus(out IntPtr provider)
    {
        provider = IntPtr.Zero;
        int focused = _bridge.Core.FindFocusedId(_bridge.RootId);
        if (focused >= 0) provider = _bridge.GetProviderPointer(focused, addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int Invoke() => RequestAction(AccessibilityActionKind.Invoke);

    internal int Toggle() => RequestAction(AccessibilityActionKind.Toggle);

    internal int GetToggleState(out int value)
    {
        var entry = _entry;
        value = entry is null ? 0 : Win32AccessibilityMapping.ToggleState(entry.Node);
        return Win32UiaNative.S_OK;
    }

    internal int SetValue(string text)
    {
        if (!SupportsPattern(Win32AccessibilityPattern.Value)) return Win32UiaNative.UIA_E_NOTSUPPORTED;
        return RequestAction(AccessibilityActionKind.SetValue, text);
    }

    internal int GetValue(out IntPtr value)
    {
        value = IntPtr.Zero;
        var entry = _entry;
        if (entry is null) return Win32UiaNative.S_OK;
        value = Win32UiaNative.AllocBstr(Win32AccessibilityMapping.TextValue(entry.Node));
        return Win32UiaNative.S_OK;
    }

    internal int GetIsReadOnly(out int value)
    {
        var entry = _entry;
        value = entry is null ? 1 : (Win32AccessibilityMapping.IsReadOnly(entry.Node) ? 1 : 0);
        return Win32UiaNative.S_OK;
    }

    internal int SetRangeValue(double value)
    {
        if (!SupportsPattern(Win32AccessibilityPattern.RangeValue)) return Win32UiaNative.UIA_E_NOTSUPPORTED;
        return RequestAction(AccessibilityActionKind.SetValue,
            value.ToString("R", CultureInfo.InvariantCulture));
    }

    internal int GetRangeValue(out double value) => GetRangePart(RangePart.Value, out value);
    internal int GetRangeMaximum(out double value) => GetRangePart(RangePart.Maximum, out value);
    internal int GetRangeMinimum(out double value) => GetRangePart(RangePart.Minimum, out value);
    internal int GetRangeLargeChange(out double value) => GetRangePart(RangePart.LargeChange, out value);
    internal int GetRangeSmallChange(out double value) => GetRangePart(RangePart.SmallChange, out value);

    enum RangePart { Value, Minimum, Maximum, LargeChange, SmallChange }

    int GetRangePart(RangePart part, out double value)
    {
        value = 0;
        var entry = _entry;
        if (entry is null) return Win32UiaNative.S_OK;
        Win32AccessibilityPropertyReader.TryGetRange(entry.Node, out double minimum, out double maximum,
            out double current, out double small, out double large, out _);
        switch (part)
        {
            case RangePart.Value: value = current; break;
            case RangePart.Minimum: value = minimum; break;
            case RangePart.Maximum: value = maximum; break;
            case RangePart.LargeChange: value = large; break;
            case RangePart.SmallChange: value = small; break;
        }
        return Win32UiaNative.S_OK;
    }

    internal int Expand() => RequestAction(AccessibilityActionKind.Expand);
    internal int Collapse() => RequestAction(AccessibilityActionKind.Collapse);

    internal int GetExpandCollapseState(out int value)
    {
        var entry = _entry;
        value = entry is null
            ? Win32UiaConstants.ExpandCollapseStateLeafNode
            : Win32AccessibilityMapping.ExpandCollapseState(entry.Node);
        return Win32UiaNative.S_OK;
    }

    // AccessibilityActionKind 没有 Select：选择项动作映射为 Invoke（点击通常完成选择）。
    internal int Select() => RequestAction(AccessibilityActionKind.Invoke);
    internal int AddToSelection() => RequestAction(AccessibilityActionKind.Invoke);
    internal int RemoveFromSelection() => RequestAction(AccessibilityActionKind.Invoke);

    internal int GetIsSelected(out int value)
    {
        var entry = _entry;
        value = entry is not null && Win32AccessibilityMapping.IsSelected(entry.Node) ? 1 : 0;
        return Win32UiaNative.S_OK;
    }

    internal int GetSelectionContainer(out IntPtr provider)
    {
        provider = IntPtr.Zero;
        var entry = _entry;
        if (entry is not null && entry.ParentId >= 0)
            provider = _bridge.GetProviderPointer(entry.ParentId, addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int GetSelectionArray(out IntPtr array)
    {
        var ids = _bridge.Core.SelectedChildren(_nodeId);
        var pointers = new IntPtr[ids.Length];
        try
        {
            for (int i = 0; i < ids.Length; i++)
                pointers[i] = _bridge.GetProviderPointer(ids[i], addRef: true);
            array = Win32UiaNative.CreateUnknownArray(pointers);
            return array == IntPtr.Zero ? Win32UiaNative.E_FAIL : Win32UiaNative.S_OK;
        }
        finally
        {
            for (int i = 0; i < pointers.Length; i++)
            {
                if (pointers[i] == IntPtr.Zero) continue;
                Win32ComProvider.FromPointer(pointers[i]).ReleaseCore();
            }
        }
    }

    internal int GetCanSelectMultiple(out int value)
    {
        var entry = _entry;
        value = entry is not null && Win32AccessibilityMapping.HasState(entry.Node, "multiselectable") ? 1 : 0;
        return Win32UiaNative.S_OK;
    }

    internal int GetIsSelectionRequired(out int value)
    {
        value = 0;
        return Win32UiaNative.S_OK;
    }

    internal int GridGetItem(int row, int column, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        var entry = _entry;
        if (entry is null || row != 0) return Win32UiaNative.S_OK;
        if (column < 0 || column >= entry.Children.Length) return Win32UiaNative.S_OK;
        provider = _bridge.GetProviderPointer(entry.Children[column], addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int GetRowCount(out int value)
    {
        var entry = _entry;
        value = entry is not null && entry.Children.Length > 0 ? 1 : 0;
        return Win32UiaNative.S_OK;
    }

    internal int GetColumnCount(out int value)
    {
        var entry = _entry;
        value = entry?.Children.Length ?? 0;
        return Win32UiaNative.S_OK;
    }

    internal int GetGridRow(out int value) { value = 0; return Win32UiaNative.S_OK; }

    internal int GetGridColumn(out int value)
    {
        value = _entry?.SiblingIndex ?? 0;
        return Win32UiaNative.S_OK;
    }

    internal int GetGridRowSpan(out int value) { value = 1; return Win32UiaNative.S_OK; }
    internal int GetGridColumnSpan(out int value) { value = 1; return Win32UiaNative.S_OK; }

    internal int GetContainingGrid(out IntPtr provider)
    {
        provider = IntPtr.Zero;
        var entry = _entry;
        if (entry is not null && entry.ParentId >= 0)
            provider = _bridge.GetProviderPointer(entry.ParentId, addRef: true);
        return Win32UiaNative.S_OK;
    }

    internal int ScrollIntoViewAction() => RequestAction(AccessibilityActionKind.ScrollIntoView);

    bool SupportsPattern(Win32AccessibilityPattern pattern)
    {
        var entry = _entry;
        return entry is not null && (entry.Patterns & pattern) == pattern;
    }

    int RequestAction(AccessibilityActionKind action, string? value = null)
    {
        if (_entry is null) return Win32UiaNative.UIA_E_ELEMENTNOTAVAILABLE;
        return _bridge.RequestAction(_nodeId, action, value)
            ? Win32UiaNative.S_OK
            : Win32UiaNative.UIA_E_NOTSUPPORTED;
    }
}
