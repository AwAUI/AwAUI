using System;
using System.Runtime.InteropServices;
using AwAUI.Platform;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// X11 的输入法桥（XIM，design/04「IME」）：
/// <list type="bullet">
/// <item>输入法服务器支持 <c>XIMPreeditCallbacks</c>（on-the-spot）时，预编辑由框架画：PreeditStart / Draw / Caret / Done 回调经
/// <see cref="XimPreeditState"/> 整理后交给客户端的 <see cref="ITextInputClient.SetComposition"/>；否则退回 Root 风格（输入法自己显示预编辑）。</item>
/// <item>提交的文本由 <c>Xutf8LookupString</c>（XLookupChars）取得，交给 <see cref="ITextInputClient.Commit"/>；被输入法过滤的按键（XFilterEvent）不交给框架。</item>
/// <item>候选窗位置：<see cref="NotifyStateChanged"/> 以插入点设置 <c>XNSpotLocation</c>（多数输入法服务器在各种风格下都用它定位候选窗）；
/// 插入点是客户区坐标，经 <c>XTranslateCoordinates</c> 换算成根窗口坐标（窗口被窗口管理器移动或加边框时也正确），换算失败时退回客户区坐标。</item>
/// <item><see cref="ResetComposition"/> 与切换客户端：<c>Xutf8ResetIC</c>（丢弃预编辑）。切换客户端时同步 XIC 焦点：
/// 客户端为 null 或密码框时 <c>XUnsetICFocus</c>（不接收组合输入），否则 <c>XSetICFocus</c>。</item>
/// </list>
/// 预编辑文本按区域设置的多字节编码解码：经 C 库的 <c>mbrtowc</c>（与 <c>setlocale(LC_CTYPE, "")</c> 使用同一区域设置，
/// 例如 zh_CN.GB18030 下按 GB18030）逐字符转换，<b>不假定为 UTF-8</b>；宽字符（wchar_t）按 UTF-32 解码。
/// </summary>
public sealed unsafe class X11TextInput : TextInputMethod
{
    readonly X11Window _window;
    readonly XimPreeditState _preedit = new();

    internal X11TextInput(X11Window window) => _window = window;

    /// <summary>输入上下文是否使用 on-the-spot 预编辑（框架绘制预编辑文本）。</summary>
    public bool OnTheSpot => _window.PreeditCallbacks;

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        Reset();
        var ic = _window.InputContext;
        if (ic != IntPtr.Zero)
        {
            // XIC 始终保留与窗口的关联（XIM 关联不随客户端切换断开）：只切换焦点。
            // 密码框与「没有可编辑元素」时不接收组合输入；预编辑的遮罩由 UI 层负责。
            if (newClient is null || newClient.Options.IsPassword) XUnsetICFocus(ic);
            else XSetICFocus(ic);
        }
        UpdateSpot();
    }

    public override void NotifyStateChanged() => UpdateSpot();

    public override void ResetComposition() => Reset();

    void Reset()
    {
        _preedit.Done();
        var ic = _window.InputContext;
        if (ic == IntPtr.Zero) return;
        var s = Xutf8ResetIC(ic);
        if (s != IntPtr.Zero) XFree((void*)s);
    }

    static readonly byte[] s_spotLocation = Z("spotLocation");
    static readonly byte[] s_preeditAttributes = Z("preeditAttributes");

    void UpdateSpot()
    {
        var ic = _window.InputContext;
        if (ic == IntPtr.Zero || Client is not { } client) return;
        var caret = client.GetCaretRect();
        double scale = _window.RenderScale;
        var pt = new XPoint { x = Clamp(caret.X * scale), y = Clamp((caret.Y + caret.Height) * scale) };

        // XNSpotLocation 要求根窗口坐标；GetCaretRect 给出的是客户区坐标。
        // XTranslateCoordinates 会考虑窗口管理器的外框与边框；失败（例如跨屏幕）时保留客户区坐标。
        int rootX = pt.x, rootY = pt.y;
        ulong child;
        if (XTranslateCoordinates(_window.Display, _window.Handle, _window.RootWindow, pt.x, pt.y, &rootX, &rootY, &child) != 0)
        {
            pt.x = Clamp(rootX);
            pt.y = Clamp(rootY);
        }

        fixed (byte* spot = s_spotLocation)
        fixed (byte* attrs = s_preeditAttributes)
        {
            var list = XVaCreateNestedList(0, spot, (IntPtr)(&pt), IntPtr.Zero);
            if (list == IntPtr.Zero) return;
            XSetICValues(ic, attrs, list, IntPtr.Zero); // 不支持的输入法服务器返回失败的名字：忽略
            XFree((void*)list);
        }
    }

    static short Clamp(double v) => double.IsNaN(v) ? (short)0 : (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v)));

    /// <summary>预编辑回调（kind：0 start、1 done、2 draw、3 caret）。</summary>
    internal void OnPreedit(int kind, IntPtr callData)
    {
        switch (kind)
        {
            case 0:
                _preedit.Start();
                return;
            case 1:
                _preedit.Done();
                if (Client is { Composition: not null } c) c.SetComposition(string.Empty, default);
                return;
            case 2:
            {
                if (callData == IntPtr.Zero) return;
                var draw = (XIMPreeditDrawCallbackStruct*)callData;
                string? text = null;
                ulong[]? feedback = null;
                if (draw->text != null) DecodeText(draw->text, out text, out feedback);
                // text 为 NULL：删除；text 的 string 为 NULL 而 feedback 不为 NULL：只改高亮（XIM 规范），文本不变
                _preedit.OnDraw(draw->caret, draw->chg_first, draw->chg_length, draw->text != null, text, feedback);
                break;
            }
            case 3:
                if (callData == IntPtr.Zero) return;
                _preedit.MoveCaret(((XIMPreeditCaretCallbackStruct*)callData)->position);
                break;
            default:
                return;
        }
        if (Client is { } client && !client.Options.IsPassword) client.SetComposition(_preedit.Text, _preedit.Selection);
    }

    /// <summary>XIMText → 文本与逐字符的反馈。长度与字节数都有上限。</summary>
    static void DecodeText(XIMText* t, out string? text, out ulong[]? feedback)
    {
        int length = Math.Min((int)t->length, XimPreeditState.MaxLength);
        text = null;
        feedback = null;
        if (t->feedback != null && length > 0)
        {
            feedback = new ulong[length];
            for (int i = 0; i < length; i++) feedback[i] = t->feedback[i];
        }
        if (t->@string == IntPtr.Zero) return;
        if (t->encoding_is_wchar != 0)
        {
            // wchar_t 在 Linux 上是 4 字节的 UTF-32
            int count = 0, max = Math.Max(length, 0);
            var w = (uint*)t->@string;
            while (count < max && w[count] != 0) count++;
            text = X11TextEncoding.FromUtf32(w, count);
            return;
        }
        // 多字节：当前 LC_CTYPE 区域设置的编码（不一定是 UTF-8），以 NUL 结尾，每字符至多 4 字节
        var p = (byte*)t->@string;
        int maxBytes = length * 4 + 4, n = 0;
        while (n < maxBytes && p[n] != 0) n++;
        text = X11TextEncoding.FromMultibyte(p, n, DecodeLocale);
    }

    /// <summary>
    /// 用 C 库的 <c>mbrtowc</c> 按当前区域设置把一个多字节字符转换为码点。返回值约定见
    /// <see cref="X11TextEncoding.MultibyteDecoder"/>；<c>state</c> 是每次解码新建的零初始化 mbstate_t。
    /// </summary>
    static int DecodeLocale(byte* state, byte* source, int remaining, uint* codePoint)
    {
        ulong consumed = LibC.mbrtowc(codePoint, source, (UIntPtr)(uint)remaining, state).ToUInt64();
        if (consumed == ulong.MaxValue) return -1;      // (size_t)-1：无效序列（EILSEQ）
        if (consumed == ulong.MaxValue - 1) return -2;  // (size_t)-2：末尾不完整序列
        return (int)consumed;                           // 0 表示遇到 NUL
    }
}
