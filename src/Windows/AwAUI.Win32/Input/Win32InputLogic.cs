using System;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.Win32;

// 本目录（Input/）只含与 Win32 API 无关的纯逻辑：由测试以源码链接方式在任何系统上编译运行（含 net48），
// 只能使用 netstandard2.0 可用的 API，不能引用 Interop/ 中的 P/Invoke。

/// <summary>Win32 键盘的纯逻辑：虚拟键 → 逻辑键名、AltGr 识别。</summary>
internal static class Win32Keys
{
    public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_CLEAR = 0x0C, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_PAUSE = 0x13, VK_CAPITAL = 0x14, VK_KANA = 0x15, VK_KANJI = 0x19, VK_ESCAPE = 0x1B, VK_CONVERT = 0x1C, VK_NONCONVERT = 0x1D,
        VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27,
        VK_DOWN = 0x28, VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D,
        VK_F1 = 0x70, VK_F24 = 0x87, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
        VK_PROCESSKEY = 0xE5, VK_PACKET = 0xE7;

    /// <summary>
    /// 不产生字符的虚拟键的逻辑键名；字符键（字母、数字、标点、空格、小键盘数字与运算符）返回 null，由 ToUnicodeEx 按布局求出。
    /// NumLock 关闭时小键盘送来的是导航虚拟键（VK_HOME 等），这里同样给出导航键名（与 W3C 一致：code 仍是 Numpad7，key 是 Home）。
    /// </summary>
    public static string? NamedKey(int vk, PhysicalKey physical)
    {
        if (vk >= VK_F1 && vk <= VK_F24) return "F" + (vk - VK_F1 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (vk)
        {
            case VK_BACK: return KeyValues.Backspace;
            case VK_TAB: return KeyValues.Tab;
            case VK_CLEAR: return "Clear";
            case VK_RETURN: return KeyValues.Enter;
            case VK_SHIFT: case VK_LSHIFT: case VK_RSHIFT: return "Shift";
            case VK_CONTROL: case VK_LCONTROL: case VK_RCONTROL: return "Control";
            case VK_MENU: case VK_LMENU: case VK_RMENU: return "Alt";
            case VK_PAUSE: return "Pause";
            case VK_CAPITAL: return "CapsLock";
            case VK_KANA: return "KanaMode";
            case VK_KANJI: return "KanjiMode";
            case VK_CONVERT: return "Convert";
            case VK_NONCONVERT: return "NonConvert";
            case VK_ESCAPE: return KeyValues.Escape;
            case VK_PRIOR: return "PageUp";
            case VK_NEXT: return "PageDown";
            case VK_END: return "End";
            case VK_HOME: return "Home";
            case VK_LEFT: return "ArrowLeft";
            case VK_UP: return "ArrowUp";
            case VK_RIGHT: return "ArrowRight";
            case VK_DOWN: return "ArrowDown";
            case VK_SNAPSHOT: return "PrintScreen";
            case VK_INSERT: return "Insert";
            case VK_DELETE: return KeyValues.Delete;
            case VK_LWIN: case VK_RWIN: return "Meta";
            case VK_APPS: return "ContextMenu";
            case VK_NUMLOCK: return "NumLock";
            case VK_SCROLL: return "ScrollLock";
            case VK_PROCESSKEY: return "Process";
        }
        return null;
    }

    /// <summary>
    /// 修饰键。Windows 把 AltGr 模拟成「左 Ctrl + 右 Alt」，用户也可以按 Ctrl+Alt 代替 AltGr：只要 Ctrl 与 Alt 同时按下且这一键在
    /// 该状态下按布局产生字符（<paramref name="producesTextWithCtrlAlt"/>，由 ToUnicodeEx 得到），就报告
    /// <see cref="KeyModifiers.AltGraph"/> 并去掉 Ctrl 与 Alt；否则 Ctrl+Alt 是快捷键，照常报告。
    /// </summary>
    public static KeyModifiers ResolveModifiers(bool shift, bool control, bool alt, bool meta, bool producesTextWithCtrlAlt)
    {
        var m = KeyModifiers.None;
        if (shift) m |= KeyModifiers.Shift;
        if (meta) m |= KeyModifiers.Meta;
        if (control && alt && producesTextWithCtrlAlt) return m | KeyModifiers.AltGraph;
        if (control) m |= KeyModifiers.Control;
        if (alt) m |= KeyModifiers.Alt;
        return m;
    }

    /// <summary>ToUnicodeEx 的结果 → 逻辑键：负数为死键；产生可打印字符时为该字符；否则 null（交给按物理键的推断）。</summary>
    public static string? FromToUnicode(int result, char[] buffer)
    {
        if (result < 0) return KeyValues.Dead;
        if (result == 0) return null;
        int n = Math.Min(result, buffer.Length);
        var s = new string(buffer, 0, n);
        return KeyValues.IsCharacter(s) ? s : null;
    }
}

/// <summary>
/// WM_CHAR 的 UTF-16 组装：窗口类以 W 版本注册，wParam 是一个 UTF-16 码元，增补平面字符分两条消息（高代理在前）到达。
/// 孤立的代理被丢弃；控制字符（退格、回车、Tab、Esc、Ctrl+字母的 0x01–0x1A、DEL、C1）经按键处理，不作为文本。
/// </summary>
internal struct Utf16Assembler
{
    char _high;

    /// <summary>处理一个码元；返回完整的文本（null 表示还需要下一个码元或该码元不是文本）。</summary>
    public string? Push(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _high = c;
            return null;
        }
        if (char.IsLowSurrogate(c))
        {
            if (_high == '\0') return null;
            var pair = new string(new[] { _high, c });
            _high = '\0';
            return pair;
        }
        _high = '\0';
        return char.IsControl(c) ? null : c.ToString();
    }

    /// <summary>失去焦点等场合丢弃半个代理对。</summary>
    public void Reset() => _high = '\0';
}

/// <summary>IMM32 组合字符串的解析（WM_IME_COMPOSITION）。</summary>
internal static class ImeComposition
{
    // GCS_COMPATTR 的每字符属性
    public const byte ATTR_INPUT = 0, ATTR_TARGET_CONVERTED = 1, ATTR_CONVERTED = 2, ATTR_TARGET_NOTCONVERTED = 3, ATTR_INPUT_ERROR = 4;

    /// <summary>组合字符串的长度上限（UTF-16 码元）；更长时截断（输入法不会产生这么长的预编辑）。</summary>
    public const int MaxLength = 4096;

    /// <summary>
    /// 组合中的选区：输入法正在转换的文节（属性为 TARGET_* 的连续段）；没有时为光标位置的空选区。
    /// <paramref name="cursor"/> 是 GCS_CURSORPOS（负数或越界时取末尾）。
    /// </summary>
    public static TextInputRange Selection(byte[]? attributes, int attributeCount, int cursor, int length)
    {
        if (attributes is not null)
        {
            int n = Math.Min(Math.Min(attributeCount, attributes.Length), length);
            int start = -1, end = -1;
            for (int i = 0; i < n; i++)
            {
                byte a = attributes[i];
                if (a == ATTR_TARGET_CONVERTED || a == ATTR_TARGET_NOTCONVERTED)
                {
                    if (start < 0) start = i;
                    end = i + 1;
                }
                else if (start >= 0) break;
            }
            if (start >= 0) return new TextInputRange(start, end);
        }
        if (cursor < 0 || cursor > length) cursor = length;
        return TextInputRange.Caret(cursor);
    }

    /// <summary>ISC_SHOWUICOMPOSITIONWINDOW（WM_IME_SETCONTEXT 的 lParam 位）。</summary>
    public const uint ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000;

    /// <summary>
    /// WM_IME_SETCONTEXT 的 lParam 去掉 ISC_SHOWUICOMPOSITIONWINDOW（预编辑由框架绘制）。lParam 是 32 位标志，32 位进程上
    /// 第 31 位置位的值是负数：按 32 位无符号处理，结果总能放进 32 位的 LPARAM（直接对符号扩展后的 64 位值清位会得到
    /// 超出 Int32 的负数，在 32 位进程上转换为 IntPtr 时抛 OverflowException）。
    /// </summary>
    public static long WithoutCompositionWindow(long lParam) => (uint)lParam & ~ISC_SHOWUICOMPOSITIONWINDOW;

    /// <summary>ImmGetCompositionStringW 的字节数 → 码元数（奇数字节与负数（错误码）按 0 处理），并限制在上限内。</summary>
    public static int CharCount(int bytes) => bytes <= 0 ? 0 : Math.Min(bytes / 2, MaxLength);
}

/// <summary>CF_UNICODETEXT 的转换。</summary>
internal static class Win32ClipboardText
{
    /// <summary>
    /// 从剪贴板内存（最多 <paramref name="capacityChars"/> 个码元，即 GlobalSize / 2）读出以 NUL 结尾的文本；没有 NUL 时取到容量为止，
    /// 结果不超过 <see cref="Clipboard.MaxTextLength"/>，不切开代理对。CRLF 规范为 LF。
    /// </summary>
    public static unsafe string Decode(char* data, long capacityChars)
    {
        if (data == null || capacityChars <= 0) return string.Empty;
        long max = Math.Min(capacityChars, Clipboard.MaxTextLength);
        int n = 0;
        while (n < max && data[n] != '\0') n++;
        if (n > 0 && n == Clipboard.MaxTextLength && char.IsHighSurrogate(data[n - 1])) n--;
        return Clipboard.NormalizeNewlines(new string(data, 0, n));
    }

    /// <summary>写入前把 LF / 单独的 CR 转成 Windows 的 CRLF。</summary>
    public static string ToCrLf(string text)
    {
        string lf = Clipboard.NormalizeNewlines(text);
        if (lf.IndexOf('\n') < 0) return lf;
        var sb = new StringBuilder(lf.Length + 16);
        foreach (char c in lf)
        {
            if (c == '\n') sb.Append('\r');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
