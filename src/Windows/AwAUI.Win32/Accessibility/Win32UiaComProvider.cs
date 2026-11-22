using System;
using System.Runtime.InteropServices;

namespace AwAUI.Win32;

// 手写 COM vtable（design/11：netstandard2.0 没有 ComWrappers，统一用裸 vtable 指针，不使用运行时 COM 封送）。
// 每个 provider 在非托管内存里有一个 { vtable, GCHandle } 块；回调经 GCHandle 取回托管对象。
// 引用计数统一指向托管对象，多个接口块共享同一个计数与生命周期。

internal enum Win32ComInterfaceKind
{
    Simple,
    Fragment,
    FragmentRoot,
    Invoke,
    Toggle,
    Value,
    RangeValue,
    ExpandCollapse,
    SelectionItem,
    Selection,
    Grid,
    GridItem,
    ScrollItem,
}

/// <summary>手写 COM 对象的基类：引用计数、接口块缓存与 QueryInterface 分派。</summary>
internal abstract unsafe class Win32ComProvider
{
    readonly GCHandle _handle;
    readonly object _gate = new();
    readonly System.Collections.Generic.Dictionary<Win32ComInterfaceKind, IntPtr> _blocks = new();
    int _refCount = 1; // 桥持有的一枚引用

    protected Win32ComProvider()
    {
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    /// <summary>是否支持某个接口（由派生类按节点模式决定）。</summary>
    protected internal abstract bool SupportsInterface(Win32ComInterfaceKind kind);

    /// <summary>返回接口块指针；<paramref name="addRef"/> 为 true 时按 COM 约定增加引用。</summary>
    internal IntPtr GetInterfacePointer(Win32ComInterfaceKind kind, bool addRef)
    {
        lock (_gate)
        {
            if (!SupportsInterface(kind)) return IntPtr.Zero;
            if (!_blocks.TryGetValue(kind, out var block))
            {
                var vtable = Win32UiaVtables.Get(kind);
                block = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(block, 0, vtable);
                Marshal.WriteIntPtr(block, IntPtr.Size, GCHandle.ToIntPtr(_handle));
                _blocks.Add(kind, block);
            }
            if (addRef) AddRefCore();
            return block;
        }
    }

    /// <summary>释放桥持有的那一枚引用（节点被移出语义树时调用）。</summary>
    internal void ReleaseBridgeReference()
    {
        ReleaseCore();
    }

    internal uint AddRefCore()
    {
        int count = System.Threading.Interlocked.Increment(ref _refCount);
        return (uint)count;
    }

    internal uint ReleaseCore()
    {
        int count = System.Threading.Interlocked.Decrement(ref _refCount);
        if (count == 0) FreeNative();
        return count <= 0 ? 0u : (uint)count;
    }

    void FreeNative()
    {
        lock (_gate)
        {
            foreach (var block in _blocks.Values) Marshal.FreeHGlobal(block);
            _blocks.Clear();
            if (_handle.IsAllocated) _handle.Free();
        }
    }

    internal static Win32ComProvider FromPointer(IntPtr self)
    {
        var handle = Marshal.ReadIntPtr(self, IntPtr.Size);
        var target = GCHandle.FromIntPtr(handle).Target;
        return (Win32ComProvider)target!;
    }

    internal int QueryInterfaceCore(ref Guid iid, out IntPtr interfacePointer)
    {
        interfacePointer = IntPtr.Zero;
        var kind = Win32UiaVtables.KindForIid(iid);
        if (kind is null || !SupportsInterface(kind.Value)) return Win32UiaNative.E_NOINTERFACE;
        interfacePointer = GetInterfacePointer(kind.Value, addRef: true);
        return interfacePointer == IntPtr.Zero ? Win32UiaNative.E_NOINTERFACE : Win32UiaNative.S_OK;
    }
}

/// <summary>所有 provider 接口的 vtable 与静态回调。委托保存在静态字段中，防止 GC 回收原生函数指针。</summary>
internal static unsafe class Win32UiaVtables
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceCallback(IntPtr self, ref Guid iid, out IntPtr interfacePointer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint AddRefCallback(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint ReleaseCallback(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetIntCallback(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetDoubleCallback(IntPtr self, out double value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetStringCallback(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetProviderCallback(IntPtr self, out IntPtr provider);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetPatternProviderCallback(IntPtr self, int patternId, out IntPtr provider);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetPropertyValueCallback(IntPtr self, int propertyId, Win32UiaVariant* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int NavigateCallback(IntPtr self, int direction, out IntPtr provider);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetSafeArrayCallback(IntPtr self, out IntPtr array);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetRectCallback(IntPtr self, Win32UiaRect* rect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int NoArgCallback(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetStringCallback(IntPtr self, char* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetDoubleCallback(IntPtr self, double value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetItemCallback(IntPtr self, int row, int column, out IntPtr provider);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ElementFromPointCallback(IntPtr self, double x, double y, out IntPtr provider);

    // ------------------------------------------------------------ 静态委托（保持强引用）
    static readonly QueryInterfaceCallback s_queryInterface = QueryInterface;
    static readonly AddRefCallback s_addRef = AddRef;
    static readonly ReleaseCallback s_release = Release;
    static readonly GetIntCallback s_getProviderOptions = GetProviderOptions;
    static readonly GetPatternProviderCallback s_getPatternProvider = GetPatternProvider;
    static readonly GetPropertyValueCallback s_getPropertyValue = GetPropertyValue;
    static readonly GetProviderCallback s_getHostRawElementProvider = GetHostRawElementProvider;
    static readonly NavigateCallback s_navigate = Navigate;
    static readonly GetSafeArrayCallback s_getRuntimeId = GetRuntimeId;
    static readonly GetRectCallback s_getBoundingRectangle = GetBoundingRectangle;
    static readonly GetSafeArrayCallback s_getEmbeddedFragmentRoots = GetEmbeddedFragmentRoots;
    static readonly NoArgCallback s_setFocus = SetFocus;
    static readonly GetProviderCallback s_getFragmentRoot = GetFragmentRoot;
    static readonly ElementFromPointCallback s_elementProviderFromPoint = ElementProviderFromPoint;
    static readonly GetProviderCallback s_getFocus = GetFocus;
    static readonly NoArgCallback s_invoke = Invoke;
    static readonly NoArgCallback s_toggle = Toggle;
    static readonly GetIntCallback s_getToggleState = GetToggleState;
    static readonly SetStringCallback s_setValueString = SetValueString;
    static readonly GetStringCallback s_getValueString = GetValueString;
    static readonly GetIntCallback s_getIsReadOnly = GetIsReadOnly;
    static readonly SetDoubleCallback s_setValueDouble = SetValueDouble;
    static readonly GetDoubleCallback s_getValueDouble = GetValueDouble;
    static readonly GetDoubleCallback s_getMaximum = GetMaximum;
    static readonly GetDoubleCallback s_getMinimum = GetMinimum;
    static readonly GetDoubleCallback s_getLargeChange = GetLargeChange;
    static readonly GetDoubleCallback s_getSmallChange = GetSmallChange;
    static readonly NoArgCallback s_expand = Expand;
    static readonly NoArgCallback s_collapse = Collapse;
    static readonly GetIntCallback s_getExpandCollapseState = GetExpandCollapseState;
    static readonly NoArgCallback s_select = Select;
    static readonly NoArgCallback s_addToSelection = AddToSelection;
    static readonly NoArgCallback s_removeFromSelection = RemoveFromSelection;
    static readonly GetIntCallback s_getIsSelected = GetIsSelected;
    static readonly GetProviderCallback s_getSelectionContainer = GetSelectionContainer;
    static readonly GetSafeArrayCallback s_getSelection = GetSelectionArray;
    static readonly GetIntCallback s_getCanSelectMultiple = GetCanSelectMultiple;
    static readonly GetIntCallback s_getIsSelectionRequired = GetIsSelectionRequired;
    static readonly GetItemCallback s_gridGetItem = GridGetItem;
    static readonly GetIntCallback s_getRowCount = GetRowCount;
    static readonly GetIntCallback s_getColumnCount = GetColumnCount;
    static readonly GetIntCallback s_getGridRow = GetGridRow;
    static readonly GetIntCallback s_getGridColumn = GetGridColumn;
    static readonly GetIntCallback s_getGridRowSpan = GetGridRowSpan;
    static readonly GetIntCallback s_getGridColumnSpan = GetGridColumnSpan;
    static readonly GetProviderCallback s_getContainingGrid = GetContainingGrid;
    static readonly NoArgCallback s_scrollIntoView = ScrollIntoViewAction;

    // ------------------------------------------------------------ vtable（进程生命周期内不释放）
    static readonly IntPtr s_simpleVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_getProviderOptions, s_getPatternProvider, s_getPropertyValue, s_getHostRawElementProvider);
    static readonly IntPtr s_fragmentVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_navigate, s_getRuntimeId, s_getBoundingRectangle, s_getEmbeddedFragmentRoots, s_setFocus, s_getFragmentRoot);
    static readonly IntPtr s_fragmentRootVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_elementProviderFromPoint, s_getFocus);
    static readonly IntPtr s_invokeVtable = Build(
        s_queryInterface, s_addRef, s_release, s_invoke);
    static readonly IntPtr s_toggleVtable = Build(
        s_queryInterface, s_addRef, s_release, s_toggle, s_getToggleState);
    static readonly IntPtr s_valueVtable = Build(
        s_queryInterface, s_addRef, s_release, s_setValueString, s_getValueString, s_getIsReadOnly);
    static readonly IntPtr s_rangeValueVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_setValueDouble, s_getValueDouble, s_getIsReadOnly, s_getMaximum, s_getMinimum, s_getLargeChange, s_getSmallChange);
    static readonly IntPtr s_expandCollapseVtable = Build(
        s_queryInterface, s_addRef, s_release, s_expand, s_collapse, s_getExpandCollapseState);
    static readonly IntPtr s_selectionItemVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_select, s_addToSelection, s_removeFromSelection, s_getIsSelected, s_getSelectionContainer);
    static readonly IntPtr s_selectionVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_getSelection, s_getCanSelectMultiple, s_getIsSelectionRequired);
    static readonly IntPtr s_gridVtable = Build(
        s_queryInterface, s_addRef, s_release, s_gridGetItem, s_getRowCount, s_getColumnCount);
    static readonly IntPtr s_gridItemVtable = Build(
        s_queryInterface, s_addRef, s_release,
        s_getGridRow, s_getGridColumn, s_getGridRowSpan, s_getGridColumnSpan, s_getContainingGrid);
    static readonly IntPtr s_scrollItemVtable = Build(
        s_queryInterface, s_addRef, s_release, s_scrollIntoView);

    public static IntPtr Get(Win32ComInterfaceKind kind)
    {
        switch (kind)
        {
            case Win32ComInterfaceKind.Simple: return s_simpleVtable;
            case Win32ComInterfaceKind.Fragment: return s_fragmentVtable;
            case Win32ComInterfaceKind.FragmentRoot: return s_fragmentRootVtable;
            case Win32ComInterfaceKind.Invoke: return s_invokeVtable;
            case Win32ComInterfaceKind.Toggle: return s_toggleVtable;
            case Win32ComInterfaceKind.Value: return s_valueVtable;
            case Win32ComInterfaceKind.RangeValue: return s_rangeValueVtable;
            case Win32ComInterfaceKind.ExpandCollapse: return s_expandCollapseVtable;
            case Win32ComInterfaceKind.SelectionItem: return s_selectionItemVtable;
            case Win32ComInterfaceKind.Selection: return s_selectionVtable;
            case Win32ComInterfaceKind.Grid: return s_gridVtable;
            case Win32ComInterfaceKind.GridItem: return s_gridItemVtable;
            case Win32ComInterfaceKind.ScrollItem: return s_scrollItemVtable;
            default: return IntPtr.Zero;
        }
    }

    /// <summary>IID → 接口种类。IRawElementProviderSimple / IUnknown 统一到 Simple（COM 标识）。</summary>
    public static Win32ComInterfaceKind? KindForIid(Guid iid)
    {
        if (iid == Win32UiaGuids.IUnknown || iid == Win32UiaGuids.IRawElementProviderSimple)
            return Win32ComInterfaceKind.Simple;
        if (iid == Win32UiaGuids.IRawElementProviderFragment) return Win32ComInterfaceKind.Fragment;
        if (iid == Win32UiaGuids.IRawElementProviderFragmentRoot) return Win32ComInterfaceKind.FragmentRoot;
        if (iid == Win32UiaGuids.IInvokeProvider) return Win32ComInterfaceKind.Invoke;
        if (iid == Win32UiaGuids.IToggleProvider) return Win32ComInterfaceKind.Toggle;
        if (iid == Win32UiaGuids.IValueProvider) return Win32ComInterfaceKind.Value;
        if (iid == Win32UiaGuids.IRangeValueProvider) return Win32ComInterfaceKind.RangeValue;
        if (iid == Win32UiaGuids.IExpandCollapseProvider) return Win32ComInterfaceKind.ExpandCollapse;
        if (iid == Win32UiaGuids.ISelectionItemProvider) return Win32ComInterfaceKind.SelectionItem;
        if (iid == Win32UiaGuids.ISelectionProvider) return Win32ComInterfaceKind.Selection;
        if (iid == Win32UiaGuids.IGridProvider) return Win32ComInterfaceKind.Grid;
        if (iid == Win32UiaGuids.IGridItemProvider) return Win32ComInterfaceKind.GridItem;
        if (iid == Win32UiaGuids.IScrollItemProvider) return Win32ComInterfaceKind.ScrollItem;
        return null;
    }

    static IntPtr Build(params Delegate[] methods)
    {
        var pointer = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
        for (int i = 0; i < methods.Length; i++)
        {
            // netstandard2.0 没有 ComWrappers，只能使用运行时反向 P/Invoke 存根；
            // NativeAOT 下 UIA provider 需要改用 COM 源生成器，这里以软件呈现后端为基线。
#pragma warning disable IL3050
            Marshal.WriteIntPtr(pointer, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[i]));
#pragma warning restore IL3050
        }
        return pointer;
    }

    static Win32UiaNode Node(IntPtr self) => (Win32UiaNode)Win32ComProvider.FromPointer(self);

    // ------------------------------------------------------------ IUnknown
    static int QueryInterface(IntPtr self, ref Guid iid, out IntPtr interfacePointer)
    {
        interfacePointer = IntPtr.Zero;
        try
        {
            return Win32ComProvider.FromPointer(self).QueryInterfaceCore(ref iid, out interfacePointer);
        }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static uint AddRef(IntPtr self)
    {
        try { return Win32ComProvider.FromPointer(self).AddRefCore(); }
        catch { return 0; }
    }

    static uint Release(IntPtr self)
    {
        try { return Win32ComProvider.FromPointer(self).ReleaseCore(); }
        catch { return 0; }
    }

    // ------------------------------------------------------------ IRawElementProviderSimple
    static int GetProviderOptions(IntPtr self, out int value)
    {
        value = Win32UiaConstants.ProviderOptionsServerSide;
        return Win32UiaNative.S_OK;
    }

    static int GetHostRawElementProvider(IntPtr self, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GetHostRawElementProvider(out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetPatternProvider(IntPtr self, int patternId, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GetPatternProvider(patternId, out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetPropertyValue(IntPtr self, int propertyId, Win32UiaVariant* value)
    {
        try { return Node(self).GetPropertyValue(propertyId, value); }
        catch { if (value != null) *value = default; return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IRawElementProviderFragment
    static int Navigate(IntPtr self, int direction, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).Navigate(direction, out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetRuntimeId(IntPtr self, out IntPtr array)
    {
        array = IntPtr.Zero;
        try { return Node(self).GetRuntimeId(out array); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetBoundingRectangle(IntPtr self, Win32UiaRect* rect)
    {
        try { return Node(self).GetBoundingRectangle(rect); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetEmbeddedFragmentRoots(IntPtr self, out IntPtr array)
    {
        array = IntPtr.Zero;
        return Win32UiaNative.S_OK;
    }

    static int SetFocus(IntPtr self)
    {
        try { return Node(self).SetFocus(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetFragmentRoot(IntPtr self, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GetFragmentRoot(out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IRawElementProviderFragmentRoot
    static int ElementProviderFromPoint(IntPtr self, double x, double y, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).ElementProviderFromPoint(x, y, out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetFocus(IntPtr self, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GetFocus(out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IInvokeProvider
    static int Invoke(IntPtr self)
    {
        try { return Node(self).Invoke(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IToggleProvider
    static int Toggle(IntPtr self)
    {
        try { return Node(self).Toggle(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetToggleState(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetToggleState(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IValueProvider
    static int SetValueString(IntPtr self, char* value)
    {
        try
        {
            string text = value == null ? "" : Marshal.PtrToStringUni((IntPtr)value) ?? "";
            return Node(self).SetValue(text);
        }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetValueString(IntPtr self, out IntPtr value)
    {
        value = IntPtr.Zero;
        try { return Node(self).GetValue(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetIsReadOnly(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetIsReadOnly(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IRangeValueProvider
    static int SetValueDouble(IntPtr self, double value)
    {
        try { return Node(self).SetRangeValue(value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetValueDouble(IntPtr self, out double value)
    {
        value = 0;
        try { return Node(self).GetRangeValue(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetMaximum(IntPtr self, out double value)
    {
        value = 0;
        try { return Node(self).GetRangeMaximum(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetMinimum(IntPtr self, out double value)
    {
        value = 0;
        try { return Node(self).GetRangeMinimum(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetLargeChange(IntPtr self, out double value)
    {
        value = 0;
        try { return Node(self).GetRangeLargeChange(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetSmallChange(IntPtr self, out double value)
    {
        value = 0;
        try { return Node(self).GetRangeSmallChange(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IExpandCollapseProvider
    static int Expand(IntPtr self)
    {
        try { return Node(self).Expand(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int Collapse(IntPtr self)
    {
        try { return Node(self).Collapse(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetExpandCollapseState(IntPtr self, out int value)
    {
        value = Win32UiaConstants.ExpandCollapseStateLeafNode;
        try { return Node(self).GetExpandCollapseState(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ ISelectionItemProvider
    static int Select(IntPtr self)
    {
        try { return Node(self).Select(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int AddToSelection(IntPtr self)
    {
        try { return Node(self).AddToSelection(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int RemoveFromSelection(IntPtr self)
    {
        try { return Node(self).RemoveFromSelection(); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetIsSelected(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetIsSelected(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetSelectionContainer(IntPtr self, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GetSelectionContainer(out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ ISelectionProvider
    static int GetSelectionArray(IntPtr self, out IntPtr array)
    {
        array = IntPtr.Zero;
        try { return Node(self).GetSelectionArray(out array); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetCanSelectMultiple(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetCanSelectMultiple(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetIsSelectionRequired(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetIsSelectionRequired(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IGridProvider
    static int GridGetItem(IntPtr self, int row, int column, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GridGetItem(row, column, out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetRowCount(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetRowCount(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetColumnCount(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetColumnCount(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IGridItemProvider
    static int GetGridRow(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetGridRow(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetGridColumn(IntPtr self, out int value)
    {
        value = 0;
        try { return Node(self).GetGridColumn(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetGridRowSpan(IntPtr self, out int value)
    {
        value = 1;
        try { return Node(self).GetGridRowSpan(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetGridColumnSpan(IntPtr self, out int value)
    {
        value = 1;
        try { return Node(self).GetGridColumnSpan(out value); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    static int GetContainingGrid(IntPtr self, out IntPtr provider)
    {
        provider = IntPtr.Zero;
        try { return Node(self).GetContainingGrid(out provider); }
        catch { return Win32UiaNative.E_FAIL; }
    }

    // ------------------------------------------------------------ IScrollItemProvider
    static int ScrollIntoViewAction(IntPtr self)
    {
        try { return Node(self).ScrollIntoViewAction(); }
        catch { return Win32UiaNative.E_FAIL; }
    }
}
