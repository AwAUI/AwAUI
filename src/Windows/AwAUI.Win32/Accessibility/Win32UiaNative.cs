using System;
using System.Runtime.InteropServices;

namespace AwAUI.Win32;

// Win32 / UI Automation 互操作声明。只使用操作系统自带的 uiautomationcore.dll / oleaut32.dll / user32.dll，
// 不引入 NuGet。所有结构 blittable；32 位与 64 位布局均按 Windows SDK 头文件。

/// <summary>UIA 的 UiaRect（屏幕物理像素，四个 double，见 UIAutomationCore.h）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Win32UiaRect
{
    public double Left;
    public double Top;
    public double Width;
    public double Height;

    public static Win32UiaRect From(in Win32AccessibilityRect rect) => new()
    {
        Left = rect.Left,
        Top = rect.Top,
        Width = rect.Width,
        Height = rect.Height,
    };
}

/// <summary>Win32 POINT。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Win32UiaPoint
{
    public int X;
    public int Y;
}

/// <summary>
/// VARIANT（oaidl.h）。x64 为 24 字节（2+2+2+2 + 8 + 8），x86 为 16 字节（8 + 4 + 4），
/// 与原生布局一致。只实现 UIA 属性事件需要的几种类型。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Win32UiaVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Data;
    public IntPtr Data2;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_I4 = 3;
    public const ushort VT_R8 = 5;
    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UNKNOWN = 13;
    public const ushort VT_ARRAY = 0x2000;

    public static Win32UiaVariant Empty => default;

    public static Win32UiaVariant FromInt(int value)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_I4;
        variant.Data = new IntPtr(value);
        return variant;
    }

    public static Win32UiaVariant FromBool(bool value)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_BOOL;
        variant.Data = new IntPtr(value ? -1 : 0); // VARIANT_TRUE = -1
        return variant;
    }

    public static Win32UiaVariant FromDouble(double value)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_R8;
        *(double*)&variant.Data = value;
        return variant;
    }

    public static Win32UiaVariant FromBstr(IntPtr bstr)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_BSTR;
        variant.Data = bstr;
        return variant;
    }

    public static Win32UiaVariant FromUnknown(IntPtr unknown)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_UNKNOWN;
        variant.Data = unknown;
        return variant;
    }

    public static Win32UiaVariant FromDoubleArray(IntPtr safeArray)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_ARRAY | VT_R8;
        variant.Data = safeArray;
        return variant;
    }

    public static Win32UiaVariant FromUnknownArray(IntPtr safeArray)
    {
        var variant = default(Win32UiaVariant);
        variant.Vt = VT_ARRAY | VT_UNKNOWN;
        variant.Data = safeArray;
        return variant;
    }
}

/// <summary>uiautomationcore / oleaut32 / user32 的裸互操作。</summary>
internal static unsafe class Win32UiaNative
{
    const string Uia = "uiautomationcore.dll";
    const string OleAut = "oleaut32.dll";
    const string User = "user32.dll";

    // ------------------------------------------------------------ 错误码
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int UIA_E_ELEMENTNOTAVAILABLE = unchecked((int)0x80040201);
    public const int UIA_E_NOTSUPPORTED = unchecked((int)0x80040204);

    // ------------------------------------------------------------ UIA
    [DllImport(Uia, ExactSpelling = true)]
    public static extern IntPtr UiaReturnRawElementProvider(IntPtr hwnd, IntPtr wParam, IntPtr lParam, IntPtr provider);

    /// <summary>取窗口的宿主 provider（根节点的 get_HostRawElementProvider 必须返回它）。</summary>
    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IntPtr provider);

    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaRaiseAutomationEvent(IntPtr provider, int eventId);

    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaRaiseAutomationPropertyChangedEvent(
        IntPtr provider, int propertyId, Win32UiaVariant oldValue, Win32UiaVariant newValue);

    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaRaiseStructureChangedEvent(
        IntPtr provider, int structureChangeType, int* runtimeIds, int runtimeIdCount);

    /// <summary>Win10 1709+（build 16299）。在旧系统上调用会抛 EntryPointNotFoundException。</summary>
    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaRaiseNotificationEvent(
        IntPtr provider, int notificationKind, int notificationProcessing, IntPtr displayString, IntPtr activityId);

    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaClientsAreListening();

    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaDisconnectProvider(IntPtr provider);

    [DllImport(Uia, ExactSpelling = true)]
    public static extern int UiaDisconnectAllProviders();

    // ------------------------------------------------------------ OLE Automation（BSTR / SAFEARRAY）
    [DllImport(OleAut, ExactSpelling = true)]
    public static extern IntPtr SysAllocString(char* value);

    [DllImport(OleAut, ExactSpelling = true)]
    public static extern void SysFreeString(IntPtr bstr);

    [DllImport(OleAut, ExactSpelling = true)]
    public static extern IntPtr SafeArrayCreateVector(ushort vt, int lowerBound, uint elements);

    [DllImport(OleAut, ExactSpelling = true)]
    public static extern int SafeArrayPutElement(IntPtr array, int* index, void* value);

    [DllImport(OleAut, ExactSpelling = true)]
    public static extern int SafeArrayDestroy(IntPtr array);

    // ------------------------------------------------------------ user32（客户区原点）
    [DllImport(User, ExactSpelling = true)]
    public static extern int ClientToScreen(IntPtr hwnd, ref Win32UiaPoint point);

    // ------------------------------------------------------------ 帮助函数

    /// <summary>分配 BSTR；<paramref name="value"/> 为 null 时返回 <see cref="IntPtr.Zero"/>（空串仍分配 BSTR）。</summary>
    public static IntPtr AllocBstr(string? value)
    {
        if (value is null) return IntPtr.Zero;
        fixed (char* p = value)
            return SysAllocString(p);
    }

    /// <summary>创建 int[] 的 SAFEARRAY（VT_I4）。调用方（UIA）负责释放。</summary>
    public static IntPtr CreateIntArray(int[] values)
    {
        if (values is null || values.Length == 0) return IntPtr.Zero;
        var array = SafeArrayCreateVector(Win32UiaVariant.VT_I4, 0, (uint)values.Length);
        if (array == IntPtr.Zero) return IntPtr.Zero;
        for (int i = 0; i < values.Length; i++)
        {
            int index = i;
            int value = values[i];
            if (SafeArrayPutElement(array, &index, &value) < 0)
            {
                SafeArrayDestroy(array);
                return IntPtr.Zero;
            }
        }
        return array;
    }

    /// <summary>创建 double[] 的 SAFEARRAY（VT_R8）。</summary>
    public static IntPtr CreateDoubleArray(double[] values)
    {
        if (values is null || values.Length == 0) return IntPtr.Zero;
        var array = SafeArrayCreateVector(Win32UiaVariant.VT_R8, 0, (uint)values.Length);
        if (array == IntPtr.Zero) return IntPtr.Zero;
        for (int i = 0; i < values.Length; i++)
        {
            int index = i;
            double value = values[i];
            if (SafeArrayPutElement(array, &index, &value) < 0)
            {
                SafeArrayDestroy(array);
                return IntPtr.Zero;
            }
        }
        return array;
    }

    /// <summary>
    /// 创建接口指针的 SAFEARRAY（VT_UNKNOWN）。<see cref="SafeArrayPutElement"/> 会为接口指针 AddRef，
    /// 调用方应释放自己持有的临时引用。
    /// </summary>
    public static IntPtr CreateUnknownArray(IntPtr[] pointers)
    {
        if (pointers is null) return IntPtr.Zero;
        // UIA 要求“没有选中项”时返回空 SAFEARRAY，而不是 NULL。
        var array = SafeArrayCreateVector(Win32UiaVariant.VT_UNKNOWN, 0, (uint)pointers.Length);
        if (array == IntPtr.Zero) return IntPtr.Zero;
        for (int i = 0; i < pointers.Length; i++)
        {
            int index = i;
            IntPtr pointer = pointers[i];
            if (SafeArrayPutElement(array, &index, &pointer) < 0)
            {
                SafeArrayDestroy(array);
                return IntPtr.Zero;
            }
        }
        return array;
    }
}

/// <summary>UIA provider 接口 IID（取自 UIAutomationProvider.h / .NET 参考程序集）。</summary>
internal static class Win32UiaGuids
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IRawElementProviderSimple = new("D6DD68D1-86FD-4332-8666-9ABEDEA2D24C");
    public static readonly Guid IRawElementProviderFragment = new("F7063DA8-8359-439C-9297-BBC5299A7D87");
    public static readonly Guid IRawElementProviderFragmentRoot = new("620CE2A5-AB8F-40A9-86CB-DE3C75599B58");
    public static readonly Guid IInvokeProvider = new("54FCB24B-E18E-47A2-B4D3-ECCBE77599A2");
    public static readonly Guid IToggleProvider = new("56D00BD0-C4F4-433C-A836-1A52A57E0892");
    public static readonly Guid IValueProvider = new("C7935180-6FB3-4201-B174-7DF73ADBF64A");
    public static readonly Guid IRangeValueProvider = new("36DC7AEF-33E6-4691-AFE1-2BE7274B3D33");
    public static readonly Guid IExpandCollapseProvider = new("D847D3A5-CAB0-4A98-8C32-ECB45C59AD24");
    public static readonly Guid ISelectionItemProvider = new("2ACAD808-B2D4-452D-A407-91FF1AD167B2");
    public static readonly Guid ISelectionProvider = new("FB8B03AF-3BDF-48D4-BD36-1A65793BE168");
    public static readonly Guid IGridProvider = new("B17D6187-0907-464B-A168-0EF17A1572B1");
    public static readonly Guid IGridItemProvider = new("D02541F1-FB81-4D64-AE32-F520F8A6DBD1");
    public static readonly Guid IScrollItemProvider = new("2360C714-4BF1-4B26-BA65-9B21316127EB");
}
