using System;

namespace AwAUI.Platform;

/// <summary>
/// 逻辑键值（W3C UI Events <c>KeyboardEvent.key</c>）的常量与辅助：产生字符的键是该字符，其余是固定的键名。
/// 各平台把本机的键（虚拟键 + ToUnicode、X keysym、NSEvent 字符、DOM key）翻译成这里的拼写。
/// </summary>
public static class KeyValues
{
    /// <summary>逻辑键值的长度上限（UTF-16 码元）。合法值都很短；来自不可信来源（网页脚本）的更长值按未知处理。</summary>
    public const int MaxLength = 32;

    public const string Unidentified = "Unidentified";

    /// <summary>死键（组合用重音符等）：字符在下一个键之后经文本输入送达。</summary>
    public const string Dead = "Dead";

    public const string Enter = "Enter";
    public const string Tab = "Tab";
    public const string Space = " ";
    public const string Backspace = "Backspace";
    public const string Delete = "Delete";
    public const string Escape = "Escape";
    public const string AltGraph = "AltGraph";

    /// <summary>
    /// 是否为「字符」键值（而不是键名）：一个码点的非控制字符，或不含 ASCII 字母数字的非控制字符串（某些布局一键产生多个字符）。
    /// 键名（<c>Enter</c>、<c>F1</c>、<c>Unidentified</c>、<c>Dead</c>）都由至少两个 ASCII 字母数字组成，不会被误认。
    /// </summary>
    public static bool IsCharacter(string? key)
    {
        if (key is null || key.Length == 0 || key.Length > MaxLength) return false;
        bool asciiAlnum = false;
        int codePoints = 0;
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (char.IsControl(c)) return false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= key.Length || !char.IsLowSurrogate(key[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                asciiAlnum = true;
            }
            codePoints++;
        }
        return codePoints == 1 || !asciiAlnum;
    }

    /// <summary>
    /// 平台不提供逻辑键时的推断：按 US 布局求出键值（字母随 Shift 大小写，数字与标点按 US 的 Shift 层，小键盘数字按 NumLock 打开）。
    /// 非字符键给出键名；没有对应键名的为 <see cref="Unidentified"/>。
    /// </summary>
    public static string FromPhysicalKey(PhysicalKey key, KeyModifiers modifiers = KeyModifiers.None)
    {
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        if (key >= PhysicalKey.KeyA && key <= PhysicalKey.KeyZ)
            return ((char)((shift ? 'A' : 'a') + (key - PhysicalKey.KeyA))).ToString();
        if (key >= PhysicalKey.Digit0 && key <= PhysicalKey.Digit9)
        {
            int d = key - PhysicalKey.Digit0;
            return shift ? ")!@#$%^&*(".Substring(d, 1) : ((char)('0' + d)).ToString();
        }
        if (key >= PhysicalKey.Numpad0 && key <= PhysicalKey.Numpad9) return ((char)('0' + (key - PhysicalKey.Numpad0))).ToString();
        if (key >= PhysicalKey.F1 && key <= PhysicalKey.F12) return "F" + (key - PhysicalKey.F1 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (key >= PhysicalKey.F13 && key <= PhysicalKey.F24) return "F" + (key - PhysicalKey.F13 + 13).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return key switch
        {
            PhysicalKey.Enter or PhysicalKey.NumpadEnter => Enter,
            PhysicalKey.Escape => Escape,
            PhysicalKey.Backspace => Backspace,
            PhysicalKey.Tab => Tab,
            PhysicalKey.Space => Space,
            PhysicalKey.Minus => shift ? "_" : "-",
            PhysicalKey.Equal => shift ? "+" : "=",
            PhysicalKey.BracketLeft => shift ? "{" : "[",
            PhysicalKey.BracketRight => shift ? "}" : "]",
            PhysicalKey.Backslash => shift ? "|" : "\\",
            PhysicalKey.Semicolon => shift ? ":" : ";",
            PhysicalKey.Quote => shift ? "\"" : "'",
            PhysicalKey.Backquote => shift ? "~" : "`",
            PhysicalKey.Comma => shift ? "<" : ",",
            PhysicalKey.Period => shift ? ">" : ".",
            PhysicalKey.Slash => shift ? "?" : "/",
            PhysicalKey.IntlBackslash => shift ? ">" : "<",
            PhysicalKey.NumpadAdd => "+",
            PhysicalKey.NumpadSubtract => "-",
            PhysicalKey.NumpadMultiply => "*",
            PhysicalKey.NumpadDivide => "/",
            PhysicalKey.NumpadDecimal => ".",
            PhysicalKey.NumpadEqual => "=",
            PhysicalKey.NumpadComma => ",",
            PhysicalKey.CapsLock => "CapsLock",
            PhysicalKey.NumLock => "NumLock",
            PhysicalKey.ScrollLock => "ScrollLock",
            PhysicalKey.Pause => "Pause",
            PhysicalKey.PrintScreen => "PrintScreen",
            PhysicalKey.Insert => "Insert",
            PhysicalKey.Delete => Delete,
            PhysicalKey.Home => "Home",
            PhysicalKey.End => "End",
            PhysicalKey.PageUp => "PageUp",
            PhysicalKey.PageDown => "PageDown",
            PhysicalKey.ArrowLeft => "ArrowLeft",
            PhysicalKey.ArrowRight => "ArrowRight",
            PhysicalKey.ArrowUp => "ArrowUp",
            PhysicalKey.ArrowDown => "ArrowDown",
            PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight => "Shift",
            PhysicalKey.ControlLeft or PhysicalKey.ControlRight => "Control",
            PhysicalKey.AltLeft or PhysicalKey.AltRight => "Alt",
            PhysicalKey.MetaLeft or PhysicalKey.MetaRight => "Meta",
            PhysicalKey.ContextMenu => "ContextMenu",
            _ => Unidentified,
        };
    }

    /// <summary>
    /// 非字符键的键名（与布局无关的那部分：方向、编辑、功能、修饰键……）；字符键返回 null。
    /// 平台用它给出导航键与功能键的逻辑键，字符键另按布局求出。小键盘数字与运算键按字符处理（返回 null）。
    /// </summary>
    public static string? NamedKey(PhysicalKey key)
    {
        if ((key >= PhysicalKey.KeyA && key <= PhysicalKey.KeyZ) || (key >= PhysicalKey.Digit0 && key <= PhysicalKey.Digit9)) return null;
        if (key >= PhysicalKey.Numpad0 && key <= PhysicalKey.NumpadComma && key != PhysicalKey.NumpadEnter) return null;
        switch (key)
        {
            case PhysicalKey.Space: case PhysicalKey.Minus: case PhysicalKey.Equal: case PhysicalKey.BracketLeft:
            case PhysicalKey.BracketRight: case PhysicalKey.Backslash: case PhysicalKey.Semicolon: case PhysicalKey.Quote:
            case PhysicalKey.Backquote: case PhysicalKey.Comma: case PhysicalKey.Period: case PhysicalKey.Slash:
            case PhysicalKey.IntlBackslash: case PhysicalKey.IntlRo: case PhysicalKey.IntlYen: case PhysicalKey.Unknown:
                return null;
        }
        var name = FromPhysicalKey(key);
        return name == Unidentified ? null : name;
    }
}
