using System;
using AwAUI.Platform;

namespace AwAUI.X11;

// 本目录（Input/）只含与 Xlib 无关的纯逻辑：由测试以源码链接方式在任何系统上编译运行（含 net48），
// 只能使用 netstandard2.0 可用的 API，不能引用 Interop/ 中的 P/Invoke。

/// <summary>X keysym → 逻辑键（W3C <c>KeyboardEvent.key</c>），与 Chromium / Firefox 在 X11 上的映射一致的常用子集。</summary>
internal static class X11Keysyms
{
    public const ulong XK_ISO_Level3_Shift = 0xFE03, XK_ISO_Left_Tab = 0xFE20;
    public const ulong XK_dead_first = 0xFE50, XK_dead_last = 0xFE93;
    public const ulong XK_BackSpace = 0xFF08, XK_Tab = 0xFF09, XK_Clear = 0xFF0B, XK_Return = 0xFF0D, XK_Pause = 0xFF13,
        XK_Scroll_Lock = 0xFF14, XK_Escape = 0xFF1B, XK_Multi_key = 0xFF20, XK_Kanji = 0xFF21, XK_Home = 0xFF50, XK_Left = 0xFF51,
        XK_Up = 0xFF52, XK_Right = 0xFF53, XK_Down = 0xFF54, XK_Prior = 0xFF55, XK_Next = 0xFF56, XK_End = 0xFF57, XK_Print = 0xFF61,
        XK_Insert = 0xFF63, XK_Menu = 0xFF67, XK_Num_Lock = 0xFF7F, XK_KP_Space = 0xFF80, XK_KP_Tab = 0xFF89, XK_KP_Enter = 0xFF8D,
        XK_KP_Home = 0xFF95, XK_KP_Left = 0xFF96, XK_KP_Up = 0xFF97, XK_KP_Right = 0xFF98, XK_KP_Down = 0xFF99, XK_KP_Prior = 0xFF9A,
        XK_KP_Next = 0xFF9B, XK_KP_End = 0xFF9C, XK_KP_Begin = 0xFF9D, XK_KP_Insert = 0xFF9E, XK_KP_Delete = 0xFF9F,
        XK_KP_Equal = 0xFFBD, XK_KP_Multiply = 0xFFAA, XK_KP_Add = 0xFFAB, XK_KP_Separator = 0xFFAC, XK_KP_Subtract = 0xFFAD,
        XK_KP_Decimal = 0xFFAE, XK_KP_Divide = 0xFFAF, XK_KP_0 = 0xFFB0, XK_KP_9 = 0xFFB9, XK_F1 = 0xFFBE, XK_F35 = 0xFFE0,
        XK_Shift_L = 0xFFE1, XK_Shift_R = 0xFFE2, XK_Control_L = 0xFFE3, XK_Control_R = 0xFFE4, XK_Caps_Lock = 0xFFE5,
        XK_Meta_L = 0xFFE7, XK_Meta_R = 0xFFE8, XK_Alt_L = 0xFFE9, XK_Alt_R = 0xFFEA, XK_Super_L = 0xFFEB, XK_Super_R = 0xFFEC,
        XK_Hyper_L = 0xFFED, XK_Hyper_R = 0xFFEE, XK_Delete = 0xFFFF;

    /// <summary>
    /// keysym → 逻辑键。字符 keysym（Latin-1 0x20–0x7E / 0xA0–0xFF、Unicode keysym 0x01000000 + 码点）给出该字符；功能键给出键名；
    /// 死键为 <c>"Dead"</c>。其余（旧式的非拉丁 keysym 等）用查找得到的文本（<paramref name="text"/>，单个可打印字符时）。
    /// 都不行时返回 null（由调用方按物理键推断）。
    /// </summary>
    public static string? ToKeyValue(ulong keysym, string? text)
    {
        if (keysym >= 0x20 && keysym <= 0x7E) return ((char)keysym).ToString();
        if (keysym >= 0xA0 && keysym <= 0xFF) return ((char)keysym).ToString();
        if (keysym >= 0x01000100 && keysym <= 0x0110FFFF)
        {
            int cp = (int)(keysym - 0x01000000);
            if (cp >= 0xD800 && cp <= 0xDFFF) return null;
            var s = char.ConvertFromUtf32(cp);
            return KeyValues.IsCharacter(s) ? s : null;
        }
        if (keysym >= XK_dead_first && keysym <= XK_dead_last) return KeyValues.Dead;
        if (keysym >= XK_F1 && keysym <= XK_F35) return "F" + ((int)(keysym - XK_F1) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (keysym >= XK_KP_0 && keysym <= XK_KP_9) return ((char)('0' + (int)(keysym - XK_KP_0))).ToString();
        switch (keysym)
        {
            case XK_BackSpace: return KeyValues.Backspace;
            case XK_Tab: case XK_ISO_Left_Tab: case XK_KP_Tab: return KeyValues.Tab;
            case XK_Clear: case XK_KP_Begin: return "Clear";
            case XK_Return: case XK_KP_Enter: return KeyValues.Enter;
            case XK_Pause: return "Pause";
            case XK_Scroll_Lock: return "ScrollLock";
            case XK_Escape: return KeyValues.Escape;
            case XK_Multi_key: return "Compose";
            case XK_Kanji: return "KanjiMode";
            case XK_Home: case XK_KP_Home: return "Home";
            case XK_Left: case XK_KP_Left: return "ArrowLeft";
            case XK_Up: case XK_KP_Up: return "ArrowUp";
            case XK_Right: case XK_KP_Right: return "ArrowRight";
            case XK_Down: case XK_KP_Down: return "ArrowDown";
            case XK_Prior: case XK_KP_Prior: return "PageUp";
            case XK_Next: case XK_KP_Next: return "PageDown";
            case XK_End: case XK_KP_End: return "End";
            case XK_Print: return "PrintScreen";
            case XK_Insert: case XK_KP_Insert: return "Insert";
            case XK_Menu: return "ContextMenu";
            case XK_Num_Lock: return "NumLock";
            case XK_KP_Space: return KeyValues.Space;
            case XK_KP_Equal: return "=";
            case XK_KP_Multiply: return "*";
            case XK_KP_Add: return "+";
            case XK_KP_Separator: return ",";
            case XK_KP_Subtract: return "-";
            case XK_KP_Decimal: return ".";
            case XK_KP_Divide: return "/";
            case XK_KP_Delete: case XK_Delete: return KeyValues.Delete;
            case XK_Shift_L: case XK_Shift_R: return "Shift";
            case XK_Control_L: case XK_Control_R: return "Control";
            case XK_Caps_Lock: return "CapsLock";
            case XK_Meta_L: case XK_Meta_R: return "Meta";
            case XK_Alt_L: case XK_Alt_R: return "Alt";
            case XK_Super_L: case XK_Super_R: case XK_Hyper_L: case XK_Hyper_R: return "Meta";
            case XK_ISO_Level3_Shift: return KeyValues.AltGraph;
        }
        if (text is not null && KeyValues.IsCharacter(text)) return text;
        return null;
    }
}

/// <summary>
/// XIM 预编辑（XIMPreeditCallbacks，「on-the-spot」）的状态：PreeditDraw 回调以码点为单位描述「把 [chg_first, chg_first + chg_length)
/// 换成新文本」，PreeditCaret 移动插入点。这里维护预编辑文本（UTF-16）、插入点与反显 / 高亮的文节（输入法正在转换的部分）。
/// 来自输入法服务器的数值都被限制（码点下标、长度上限 <see cref="MaxLength"/>）。
/// </summary>
internal sealed class XimPreeditState
{
    /// <summary>预编辑文本的码点数上限。</summary>
    public const int MaxLength = 4096;

    // XIMFeedback
    public const ulong XIMReverse = 1, XIMUnderline = 2, XIMHighlight = 4;

    readonly System.Collections.Generic.List<int> _cps = new();
    readonly System.Collections.Generic.List<ulong> _feedback = new();
    int _caret;

    public bool Active { get; private set; }

    public void Start()
    {
        Active = true;
        _cps.Clear();
        _feedback.Clear();
        _caret = 0;
    }

    public void Done()
    {
        Active = false;
        _cps.Clear();
        _feedback.Clear();
        _caret = 0;
    }

    /// <summary>XIMPreeditDrawCallbackStruct：<paramref name="text"/> 为 null 表示只删除；<paramref name="feedback"/> 与新文本按码点对齐（可为 null）。</summary>
    public void Draw(int caret, int changeFirst, int changeLength, string? text, ulong[]? feedback)
    {
        Active = true;
        int count = _cps.Count;
        int first = Clamp(changeFirst, 0, count);
        int length = Clamp(changeLength, 0, count - first);
        _cps.RemoveRange(first, length);
        _feedback.RemoveRange(first, length);
        if (!string.IsNullOrEmpty(text))
        {
            int i = 0, k = 0;
            while (i < text!.Length && _cps.Count < MaxLength)
            {
                int cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
                i += cp > 0xFFFF ? 2 : 1;
                if (cp >= 0xD800 && cp <= 0xDFFF) cp = 0xFFFD;
                _cps.Insert(first + k, cp);
                _feedback.Insert(first + k, feedback is not null && k < feedback.Length ? feedback[k] : 0);
                k++;
            }
        }
        _caret = Clamp(caret, 0, _cps.Count);
    }

    /// <summary>
    /// 一次 PreeditDraw 回调：<paramref name="hasText"/> 为 false（XIMText 为 NULL）表示删除；XIMText 的 string 为 NULL
    /// （<paramref name="text"/> 为 null）而有 <paramref name="feedback"/> 时只改高亮（<see cref="ChangeFeedback"/>）；否则替换文本。
    /// </summary>
    public void OnDraw(int caret, int changeFirst, int changeLength, bool hasText, string? text, ulong[]? feedback)
    {
        if (hasText && text is null && feedback is not null) ChangeFeedback(caret, changeFirst, changeLength, feedback);
        else Draw(caret, changeFirst, changeLength, hasText ? text : null, hasText ? feedback : null);
    }

    /// <summary>
    /// 只改反馈（XIMText 的 string 为 NULL、feedback 不为 NULL）：XIM 规范中这表示「只改变高亮」，[changeFirst, changeFirst + changeLength)
    /// 的文本保持不变（不是删除），反馈按新值替换；超出文本或 <paramref name="feedback"/> 的部分忽略。
    /// </summary>
    public void ChangeFeedback(int caret, int changeFirst, int changeLength, ulong[] feedback)
    {
        Active = true;
        int count = _cps.Count;
        int first = Clamp(changeFirst, 0, count);
        int length = Math.Min(Clamp(changeLength, 0, count - first), feedback.Length);
        for (int i = 0; i < length; i++) _feedback[first + i] = feedback[i];
        _caret = Clamp(caret, 0, _cps.Count);
    }

    public void MoveCaret(int position) => _caret = Clamp(position, 0, _cps.Count);

    static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    /// <summary>预编辑文本（UTF-16）。</summary>
    public string Text
    {
        get
        {
            var sb = new System.Text.StringBuilder(_cps.Count);
            foreach (int cp in _cps)
            {
                if (cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp));
                else sb.Append((char)cp);
            }
            return sb.ToString();
        }
    }

    int Utf16Offset(int codePoints)
    {
        int n = 0;
        for (int i = 0; i < codePoints && i < _cps.Count; i++) n += _cps[i] > 0xFFFF ? 2 : 1;
        return n;
    }

    /// <summary>预编辑内的选区（UTF-16）：反显或高亮的文节（输入法正在转换的部分）；没有时为插入点。</summary>
    public TextInputRange Selection
    {
        get
        {
            int start = -1, end = -1;
            for (int i = 0; i < _feedback.Count; i++)
            {
                if ((_feedback[i] & (XIMReverse | XIMHighlight)) != 0)
                {
                    if (start < 0) start = i;
                    end = i + 1;
                }
                else if (start >= 0) break;
            }
            if (start >= 0) return new TextInputRange(Utf16Offset(start), Utf16Offset(end));
            return TextInputRange.Caret(Utf16Offset(_caret));
        }
    }
}
