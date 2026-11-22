using System;
using System.Runtime.InteropServices;

namespace AwAUI.MacOS.Interop;

/// <summary>
/// 原生 → 托管的回调入口。现代 .NET 使用 [UnmanagedCallersOnly] + 函数指针（无封送、NativeAOT 友好）；
/// netstandard2.0 基线使用委托 + Marshal.GetFunctionPointerForDelegate（委托保存在静态字段中以免被回收）。
/// 回调中的异常绝不能穿过原生栈帧，统一在这里捕获。
/// </summary>
internal static unsafe class Callbacks
{
    public static Action<IntPtr, IntPtr, IntPtr>? OnEvent;      // -(void)xxx:(id)arg
    public static Action<IntPtr, NSRect>? OnDrawRect;            // -(void)drawRect:(NSRect)r
    public static Func<IntPtr, IntPtr, bool>? OnQueryBool;       // -(BOOL)xxx
    public static Func<IntPtr, IntPtr, IntPtr, bool>? OnQueryBoolArg; // -(BOOL)xxx:(id)arg

    // NSAccessibility（design/10「平台桥」）：self 既可能是代理元素，也可能是窗口内容视图。
    public static Func<IntPtr, IntPtr, IntPtr>? OnA11yObject;              // -(id)xxx
    public static Func<IntPtr, IntPtr, IntPtr, IntPtr>? OnA11yObjectArg;   // -(id)xxx:(id)arg
    public static Func<IntPtr, IntPtr, bool>? OnA11yBool;                  // -(BOOL)xxx
    public static Func<IntPtr, IntPtr, IntPtr, bool>? OnA11yBoolArg;       // -(BOOL)xxx:(id)arg
    public static Action<IntPtr, IntPtr, IntPtr>? OnA11ySetObject;         // -(void)xxx:(id)arg
    public static Action<IntPtr, IntPtr, IntPtr, IntPtr>? OnA11ySetObject2; // -(void)xxx:(id)a yyy:(id)b
    public static Func<IntPtr, IntPtr, NSRect>? OnA11yRect;                // -(NSRect)xxx

    // NSTextInputClient（design/04「IME」）：都在主线程上由 NSTextInputContext 调用
    public static Action<IntPtr, IntPtr, NSRange>? OnInsertText;              // insertText:replacementRange:
    public static Action<IntPtr, IntPtr, NSRange, NSRange>? OnSetMarkedText;   // setMarkedText:selectedRange:replacementRange:
    public static Action<IntPtr, IntPtr>? OnVoid;                               // unmarkText
    public static Func<IntPtr, IntPtr, NSRange>? OnQueryRange;                  // selectedRange / markedRange
    public static Func<IntPtr, IntPtr, IntPtr>? OnQueryObject;                  // validAttributesForMarkedText
    public static Func<IntPtr, NSRange, IntPtr, IntPtr>? OnAttributedSubstring; // attributedSubstringForProposedRange:actualRange:
    public static Func<IntPtr, NSRange, IntPtr, NSRect>? OnFirstRect;           // firstRectForCharacterRange:actualRange:
    public static Func<IntPtr, NSPoint, ulong>? OnCharacterIndex;              // characterIndexForPoint:

    /// <summary>文件面板结束（awPanelDidEnd:returnCode:contextInfo:）：panel、NSModalResponse、contextInfo。</summary>
    public static Action<IntPtr, long, IntPtr>? OnPanelEnd;

    /// <summary>CVDisplayLink 输出回调 (userInfo, inOutputTime)。在 CoreVideo 的后台线程上调用。</summary>
    public static Action<IntPtr, IntPtr>? OnDisplayLinkOutput;

    static void Report(Exception e)
    {
        try { Console.Error.WriteLine("[AwAUI.MacOS] Unhandled exception in native callback: " + e); } catch { }
    }

    static void EventImpl(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { OnEvent?.Invoke(self, sel, arg); } catch (Exception e) { Report(e); }
    }

    static void DrawRectImpl(IntPtr self, IntPtr sel, NSRect rect)
    {
        try { OnDrawRect?.Invoke(self, rect); } catch (Exception e) { Report(e); }
    }

    static byte BoolImpl(IntPtr self, IntPtr sel)
    {
        try { return OnQueryBool?.Invoke(self, sel) == true ? (byte)1 : (byte)0; } catch (Exception e) { Report(e); return 0; }
    }

    static byte BoolArgImpl(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { return OnQueryBoolArg?.Invoke(self, sel, arg) == true ? (byte)1 : (byte)0; } catch (Exception e) { Report(e); return 0; }
    }

    static void InsertTextImpl(IntPtr self, IntPtr sel, IntPtr text, NSRange replacement)
    {
        try { OnInsertText?.Invoke(self, text, replacement); } catch (Exception e) { Report(e); }
    }

    static void SetMarkedTextImpl(IntPtr self, IntPtr sel, IntPtr text, NSRange selected, NSRange replacement)
    {
        try { OnSetMarkedText?.Invoke(self, text, selected, replacement); } catch (Exception e) { Report(e); }
    }

    static void VoidImpl(IntPtr self, IntPtr sel)
    {
        try { OnVoid?.Invoke(self, sel); } catch (Exception e) { Report(e); }
    }

    /// <summary>NSNotFound（NSIntegerMax）：没有范围时 location 取它。</summary>
    public const ulong NotFound = long.MaxValue;

    static NSRange RangeImpl(IntPtr self, IntPtr sel)
    {
        try { return OnQueryRange?.Invoke(self, sel) ?? new NSRange { Location = NotFound }; }
        catch (Exception e) { Report(e); return new NSRange { Location = NotFound }; }
    }

    static IntPtr ObjectImpl(IntPtr self, IntPtr sel)
    {
        try { return OnQueryObject?.Invoke(self, sel) ?? IntPtr.Zero; } catch (Exception e) { Report(e); return IntPtr.Zero; }
    }

    static IntPtr AttributedSubstringImpl(IntPtr self, IntPtr sel, NSRange range, IntPtr actual)
    {
        try { return OnAttributedSubstring?.Invoke(self, range, actual) ?? IntPtr.Zero; } catch (Exception e) { Report(e); return IntPtr.Zero; }
    }

    static NSRect FirstRectImpl(IntPtr self, IntPtr sel, NSRange range, IntPtr actual)
    {
        try { return OnFirstRect?.Invoke(self, range, actual) ?? default; } catch (Exception e) { Report(e); return default; }
    }

    static ulong CharacterIndexImpl(IntPtr self, IntPtr sel, NSPoint point)
    {
        try { return OnCharacterIndex?.Invoke(self, point) ?? NotFound; } catch (Exception e) { Report(e); return NotFound; }
    }

    static void PanelEndImpl(IntPtr self, IntPtr sel, IntPtr panel, long returnCode, IntPtr contextInfo)
    {
        try { OnPanelEnd?.Invoke(panel, returnCode, contextInfo); } catch (Exception e) { Report(e); }
    }

    static IntPtr A11yObjectImpl(IntPtr self, IntPtr sel)
    {
        try { return OnA11yObject?.Invoke(self, sel) ?? IntPtr.Zero; } catch (Exception e) { Report(e); return IntPtr.Zero; }
    }

    static IntPtr A11yObjectArgImpl(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { return OnA11yObjectArg?.Invoke(self, sel, arg) ?? IntPtr.Zero; } catch (Exception e) { Report(e); return IntPtr.Zero; }
    }

    static byte A11yBoolImpl(IntPtr self, IntPtr sel)
    {
        try { return OnA11yBool?.Invoke(self, sel) == true ? (byte)1 : (byte)0; } catch (Exception e) { Report(e); return 0; }
    }

    static byte A11yBoolArgImpl(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { return OnA11yBoolArg?.Invoke(self, sel, arg) == true ? (byte)1 : (byte)0; } catch (Exception e) { Report(e); return 0; }
    }

    static void A11ySetObjectImpl(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { OnA11ySetObject?.Invoke(self, sel, arg); } catch (Exception e) { Report(e); }
    }

    static void A11ySetObject2Impl(IntPtr self, IntPtr sel, IntPtr arg1, IntPtr arg2)
    {
        try { OnA11ySetObject2?.Invoke(self, sel, arg1, arg2); } catch (Exception e) { Report(e); }
    }

    static NSRect A11yRectImpl(IntPtr self, IntPtr sel)
    {
        try { return OnA11yRect?.Invoke(self, sel) ?? default; } catch (Exception e) { Report(e); return default; }
    }

    // CVReturn (*)(CVDisplayLinkRef, const CVTimeStamp* inNow, const CVTimeStamp* inOutputTime,
    //             CVOptionFlags flagsIn, CVOptionFlags* flagsOut, void* userInfo)
    static int DisplayLinkOutputImpl(IntPtr link, IntPtr now, IntPtr outputTime, ulong flagsIn, IntPtr flagsOut, IntPtr userInfo)
    {
        try { OnDisplayLinkOutput?.Invoke(userInfo, outputTime); } catch (Exception e) { Report(e); }
        return 0; // kCVReturnSuccess
    }

#if NET8_0_OR_GREATER
    [UnmanagedCallersOnly] static void EventEntry(IntPtr self, IntPtr sel, IntPtr arg) => EventImpl(self, sel, arg);
    [UnmanagedCallersOnly] static void DrawRectEntry(IntPtr self, IntPtr sel, NSRect rect) => DrawRectImpl(self, sel, rect);
    [UnmanagedCallersOnly] static byte BoolEntry(IntPtr self, IntPtr sel) => BoolImpl(self, sel);
    [UnmanagedCallersOnly] static byte BoolArgEntry(IntPtr self, IntPtr sel, IntPtr arg) => BoolArgImpl(self, sel, arg);

    [UnmanagedCallersOnly] static int DisplayLinkOutputEntry(IntPtr link, IntPtr now, IntPtr outputTime, ulong flagsIn, IntPtr flagsOut, IntPtr userInfo)
        => DisplayLinkOutputImpl(link, now, outputTime, flagsIn, flagsOut, userInfo);

    [UnmanagedCallersOnly] static void InsertTextEntry(IntPtr self, IntPtr sel, IntPtr text, NSRange r) => InsertTextImpl(self, sel, text, r);
    [UnmanagedCallersOnly] static void SetMarkedTextEntry(IntPtr self, IntPtr sel, IntPtr text, NSRange s, NSRange r) => SetMarkedTextImpl(self, sel, text, s, r);
    [UnmanagedCallersOnly] static void VoidEntry(IntPtr self, IntPtr sel) => VoidImpl(self, sel);
    [UnmanagedCallersOnly] static NSRange RangeEntry(IntPtr self, IntPtr sel) => RangeImpl(self, sel);
    [UnmanagedCallersOnly] static IntPtr ObjectEntry(IntPtr self, IntPtr sel) => ObjectImpl(self, sel);
    [UnmanagedCallersOnly] static IntPtr AttributedSubstringEntry(IntPtr self, IntPtr sel, NSRange r, IntPtr a) => AttributedSubstringImpl(self, sel, r, a);
    [UnmanagedCallersOnly] static NSRect FirstRectEntry(IntPtr self, IntPtr sel, NSRange r, IntPtr a) => FirstRectImpl(self, sel, r, a);
    [UnmanagedCallersOnly] static ulong CharacterIndexEntry(IntPtr self, IntPtr sel, NSPoint p) => CharacterIndexImpl(self, sel, p);
    [UnmanagedCallersOnly] static void PanelEndEntry(IntPtr self, IntPtr sel, IntPtr panel, long code, IntPtr ctx) => PanelEndImpl(self, sel, panel, code, ctx);

    [UnmanagedCallersOnly] static IntPtr A11yObjectEntry(IntPtr self, IntPtr sel) => A11yObjectImpl(self, sel);
    [UnmanagedCallersOnly] static IntPtr A11yObjectArgEntry(IntPtr self, IntPtr sel, IntPtr arg) => A11yObjectArgImpl(self, sel, arg);
    [UnmanagedCallersOnly] static byte A11yBoolEntry(IntPtr self, IntPtr sel) => A11yBoolImpl(self, sel);
    [UnmanagedCallersOnly] static byte A11yBoolArgEntry(IntPtr self, IntPtr sel, IntPtr arg) => A11yBoolArgImpl(self, sel, arg);
    [UnmanagedCallersOnly] static void A11ySetObjectEntry(IntPtr self, IntPtr sel, IntPtr arg) => A11ySetObjectImpl(self, sel, arg);
    [UnmanagedCallersOnly] static void A11ySetObject2Entry(IntPtr self, IntPtr sel, IntPtr a, IntPtr b) => A11ySetObject2Impl(self, sel, a, b);
    [UnmanagedCallersOnly] static NSRect A11yRectEntry(IntPtr self, IntPtr sel) => A11yRectImpl(self, sel);

    public static IntPtr InsertText => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, NSRange, void>)&InsertTextEntry;
    public static IntPtr SetMarkedText => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, NSRange, NSRange, void>)&SetMarkedTextEntry;
    public static IntPtr Void => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&VoidEntry;
    public static IntPtr Range => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSRange>)&RangeEntry;
    public static IntPtr Object => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&ObjectEntry;
    public static IntPtr AttributedSubstring => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSRange, IntPtr, IntPtr>)&AttributedSubstringEntry;
    public static IntPtr FirstRect => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSRange, IntPtr, NSRect>)&FirstRectEntry;
    public static IntPtr CharacterIndex => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSPoint, ulong>)&CharacterIndexEntry;
    public static IntPtr PanelEnd => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long, IntPtr, void>)&PanelEndEntry;

    public static IntPtr A11yObject => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&A11yObjectEntry;
    public static IntPtr A11yObjectArg => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)&A11yObjectArgEntry;
    public static IntPtr A11yBool => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&A11yBoolEntry;
    public static IntPtr A11yBoolArg => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&A11yBoolArgEntry;
    public static IntPtr A11ySetObject => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&A11ySetObjectEntry;
    public static IntPtr A11ySetObject2 => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&A11ySetObject2Entry;
    public static IntPtr A11yRect => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSRect>)&A11yRectEntry;

    public static IntPtr Event => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&EventEntry;
    public static IntPtr DrawRect => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSRect, void>)&DrawRectEntry;
    public static IntPtr Bool => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&BoolEntry;
    public static IntPtr BoolArg => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&BoolArgEntry;
    public static IntPtr DisplayLinkOutput => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, ulong, IntPtr, IntPtr, int>)&DisplayLinkOutputEntry;
#else
    delegate void EventFn(IntPtr self, IntPtr sel, IntPtr arg);
    delegate void DrawRectFn(IntPtr self, IntPtr sel, NSRect rect);
    delegate byte BoolFn(IntPtr self, IntPtr sel);
    delegate byte BoolArgFn(IntPtr self, IntPtr sel, IntPtr arg);
    delegate int DisplayLinkOutputFn(IntPtr link, IntPtr now, IntPtr outputTime, ulong flagsIn, IntPtr flagsOut, IntPtr userInfo);

    static readonly EventFn s_event = EventImpl;
    static readonly DrawRectFn s_drawRect = DrawRectImpl;
    static readonly BoolFn s_bool = BoolImpl;
    static readonly BoolArgFn s_boolArg = BoolArgImpl;
    static readonly DisplayLinkOutputFn s_displayLinkOutput = DisplayLinkOutputImpl;

    delegate void InsertTextFn(IntPtr self, IntPtr sel, IntPtr text, NSRange r);
    delegate void SetMarkedTextFn(IntPtr self, IntPtr sel, IntPtr text, NSRange s, NSRange r);
    delegate void VoidFn(IntPtr self, IntPtr sel);
    delegate NSRange RangeFn(IntPtr self, IntPtr sel);
    delegate IntPtr ObjectFn(IntPtr self, IntPtr sel);
    delegate IntPtr AttributedSubstringFn(IntPtr self, IntPtr sel, NSRange r, IntPtr a);
    delegate NSRect FirstRectFn(IntPtr self, IntPtr sel, NSRange r, IntPtr a);
    delegate ulong CharacterIndexFn(IntPtr self, IntPtr sel, NSPoint p);
    delegate void PanelEndFn(IntPtr self, IntPtr sel, IntPtr panel, long code, IntPtr ctx);

    delegate IntPtr A11yObjectFn(IntPtr self, IntPtr sel);
    delegate IntPtr A11yObjectArgFn(IntPtr self, IntPtr sel, IntPtr arg);
    delegate byte A11yBoolFn(IntPtr self, IntPtr sel);
    delegate byte A11yBoolArgFn(IntPtr self, IntPtr sel, IntPtr arg);
    delegate void A11ySetObjectFn(IntPtr self, IntPtr sel, IntPtr arg);
    delegate void A11ySetObject2Fn(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    delegate NSRect A11yRectFn(IntPtr self, IntPtr sel);

    static readonly A11yObjectFn s_a11yObject = A11yObjectImpl;
    static readonly A11yObjectArgFn s_a11yObjectArg = A11yObjectArgImpl;
    static readonly A11yBoolFn s_a11yBool = A11yBoolImpl;
    static readonly A11yBoolArgFn s_a11yBoolArg = A11yBoolArgImpl;
    static readonly A11ySetObjectFn s_a11ySetObject = A11ySetObjectImpl;
    static readonly A11ySetObject2Fn s_a11ySetObject2 = A11ySetObject2Impl;
    static readonly A11yRectFn s_a11yRect = A11yRectImpl;

    static readonly PanelEndFn s_panelEnd = PanelEndImpl;
    static readonly InsertTextFn s_insertText = InsertTextImpl;
    static readonly SetMarkedTextFn s_setMarkedText = SetMarkedTextImpl;
    static readonly VoidFn s_void = VoidImpl;
    static readonly RangeFn s_range = RangeImpl;
    static readonly ObjectFn s_object = ObjectImpl;
    static readonly AttributedSubstringFn s_attributedSubstring = AttributedSubstringImpl;
    static readonly FirstRectFn s_firstRect = FirstRectImpl;
    static readonly CharacterIndexFn s_characterIndex = CharacterIndexImpl;

    public static IntPtr InsertText => Marshal.GetFunctionPointerForDelegate(s_insertText);
    public static IntPtr SetMarkedText => Marshal.GetFunctionPointerForDelegate(s_setMarkedText);
    public static IntPtr Void => Marshal.GetFunctionPointerForDelegate(s_void);
    public static IntPtr Range => Marshal.GetFunctionPointerForDelegate(s_range);
    public static IntPtr Object => Marshal.GetFunctionPointerForDelegate(s_object);
    public static IntPtr AttributedSubstring => Marshal.GetFunctionPointerForDelegate(s_attributedSubstring);
    public static IntPtr FirstRect => Marshal.GetFunctionPointerForDelegate(s_firstRect);
    public static IntPtr CharacterIndex => Marshal.GetFunctionPointerForDelegate(s_characterIndex);
    public static IntPtr PanelEnd => Marshal.GetFunctionPointerForDelegate(s_panelEnd);

    public static IntPtr A11yObject => Marshal.GetFunctionPointerForDelegate(s_a11yObject);
    public static IntPtr A11yObjectArg => Marshal.GetFunctionPointerForDelegate(s_a11yObjectArg);
    public static IntPtr A11yBool => Marshal.GetFunctionPointerForDelegate(s_a11yBool);
    public static IntPtr A11yBoolArg => Marshal.GetFunctionPointerForDelegate(s_a11yBoolArg);
    public static IntPtr A11ySetObject => Marshal.GetFunctionPointerForDelegate(s_a11ySetObject);
    public static IntPtr A11ySetObject2 => Marshal.GetFunctionPointerForDelegate(s_a11ySetObject2);
    public static IntPtr A11yRect => Marshal.GetFunctionPointerForDelegate(s_a11yRect);

    public static IntPtr Event => Marshal.GetFunctionPointerForDelegate(s_event);
    public static IntPtr DrawRect => Marshal.GetFunctionPointerForDelegate(s_drawRect);
    public static IntPtr Bool => Marshal.GetFunctionPointerForDelegate(s_bool);
    public static IntPtr BoolArg => Marshal.GetFunctionPointerForDelegate(s_boolArg);
    public static IntPtr DisplayLinkOutput => Marshal.GetFunctionPointerForDelegate(s_displayLinkOutput);
#endif
}
