using System;
using System.Runtime.InteropServices;
using System.Text;
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
/// <item>候选窗位置：<see cref="NotifyStateChanged"/> 以插入点设置 <c>XNSpotLocation</c>（多数输入法服务器在各种风格下都用它定位候选窗）。</item>
/// <item><see cref="ResetComposition"/> 与切换客户端：<c>Xutf8ResetIC</c>（丢弃预编辑）。</item>
/// </list>
/// 预编辑文本按区域设置的多字节编码解码时假定为 UTF-8（<c>setlocale(LC_CTYPE, "")</c> 之后的常见情形）；宽字符（wchar_t）按 UTF-32 解码。
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
            var w = (uint*)t->@string;
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                uint cp = w[i];
                if (cp == 0) break;
                sb.Append(cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF) ? char.ConvertFromUtf32((int)cp) : "�");
            }
            text = sb.ToString();
            return;
        }
        // 多字节（UTF-8 区域设置）：以 NUL 结尾，每字符至多 4 字节
        var p = (byte*)t->@string;
        int max = length * 4 + 4, n = 0;
        while (n < max && p[n] != 0) n++;
        text = Encoding.UTF8.GetString(p, n);
    }
}
