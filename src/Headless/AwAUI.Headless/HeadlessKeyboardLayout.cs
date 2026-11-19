using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Headless;

/// <summary>
/// 模拟的键盘布局：把物理键 + 修饰键翻译成逻辑键（W3C <c>KeyboardEvent.key</c>）与它产生的文本，
/// 规则与真实平台一致——Ctrl / Alt / Meta 组合是快捷键、不产生文本；<see cref="KeyModifiers.AltGraph"/> 选择第三层字符；
/// 死键的逻辑键为 <c>"Dead"</c> 且不产生文本（不模拟与下一个键的组合）。小键盘按 NumLock 打开处理。
/// 内置 <see cref="UnitedStates"/> 与 <see cref="German"/>（QWERTZ，AltGr+Q = @、AltGr+E = €、AltGr+7 = {）。
/// </summary>
public sealed class HeadlessKeyboardLayout
{
    // 每个键：基础层、Shift 层、AltGr 层（null 表示该层不产生字符）；"Dead" 表示死键
    readonly Dictionary<PhysicalKey, (string? Base, string? Shift, string? AltGr)> _keys;

    HeadlessKeyboardLayout(string name, Dictionary<PhysicalKey, (string?, string?, string?)> keys)
    {
        Name = name;
        _keys = keys;
    }

    public string Name { get; }

    public override string ToString() => Name;

    /// <summary>US（QWERTY）布局：没有 AltGr 层。</summary>
    public static HeadlessKeyboardLayout UnitedStates { get; } = CreateUs();

    /// <summary>德语（QWERTZ）布局：Y / Z 互换，AltGr 层含 @ € { [ ] } \ ~ | µ ² ³，´ 与 ^ 为死键。</summary>
    public static HeadlessKeyboardLayout German { get; } = CreateGerman();

    /// <summary>
    /// 翻译一次按键。<paramref name="logicalKey"/> 是逻辑键；<paramref name="text"/> 是它产生的文本（快捷键、控制键与死键为 null）。
    /// </summary>
    public void Translate(PhysicalKey key, KeyModifiers modifiers, out string logicalKey, out string? text)
    {
        text = null;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        bool altGr = (modifiers & KeyModifiers.AltGraph) != 0;
        if (!_keys.TryGetValue(key, out var k))
        {
            logicalKey = KeyValues.FromPhysicalKey(key, modifiers);
            if (key >= PhysicalKey.Numpad0 && key <= PhysicalKey.NumpadComma && key != PhysicalKey.NumpadEnter) text = logicalKey;
            if (key == PhysicalKey.Space) text = " ";
        }
        else
        {
            string? value = altGr ? k.AltGr : shift ? k.Shift ?? k.Base : k.Base;
            if (value is null) value = shift ? k.Shift ?? k.Base : k.Base; // 没有 AltGr 层：AltGr 不改变字符
            logicalKey = value ?? KeyValues.Unidentified;
            if (value is not null && value != KeyValues.Dead) text = value;
        }
        // Ctrl / Alt / Meta 组合是快捷键，不产生文本（AltGr 已经从修饰键中分离出来）
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) text = null;
    }

    static Dictionary<PhysicalKey, (string?, string?, string?)> Letters()
    {
        var d = new Dictionary<PhysicalKey, (string?, string?, string?)>();
        for (var k = PhysicalKey.KeyA; k <= PhysicalKey.KeyZ; k++)
        {
            char c = (char)('a' + (k - PhysicalKey.KeyA));
            d[k] = (c.ToString(), char.ToUpperInvariant(c).ToString(), null);
        }
        return d;
    }

    static HeadlessKeyboardLayout CreateUs()
    {
        var d = Letters();
        const string digits = "0123456789", shifted = ")!@#$%^&*(";
        for (int i = 0; i < 10; i++) d[(PhysicalKey)((int)PhysicalKey.Digit0 + i)] = (digits.Substring(i, 1), shifted.Substring(i, 1), null);
        void P(PhysicalKey k, string b, string s) => d[k] = (b, s, null);
        P(PhysicalKey.Minus, "-", "_"); P(PhysicalKey.Equal, "=", "+"); P(PhysicalKey.BracketLeft, "[", "{");
        P(PhysicalKey.BracketRight, "]", "}"); P(PhysicalKey.Backslash, "\\", "|"); P(PhysicalKey.Semicolon, ";", ":");
        P(PhysicalKey.Quote, "'", "\""); P(PhysicalKey.Backquote, "`", "~"); P(PhysicalKey.Comma, ",", "<");
        P(PhysicalKey.Period, ".", ">"); P(PhysicalKey.Slash, "/", "?");
        return new HeadlessKeyboardLayout("en-US", d);
    }

    static HeadlessKeyboardLayout CreateGerman()
    {
        var d = Letters();
        d[PhysicalKey.KeyY] = ("z", "Z", null);
        d[PhysicalKey.KeyZ] = ("y", "Y", null);
        d[PhysicalKey.KeyQ] = ("q", "Q", "@");
        d[PhysicalKey.KeyE] = ("e", "E", "€");
        d[PhysicalKey.KeyM] = ("m", "M", "µ");
        void P(PhysicalKey k, string b, string s, string? g = null) => d[k] = (b, s, g);
        P(PhysicalKey.Digit1, "1", "!"); P(PhysicalKey.Digit2, "2", "\"", "²"); P(PhysicalKey.Digit3, "3", "§", "³");
        P(PhysicalKey.Digit4, "4", "$"); P(PhysicalKey.Digit5, "5", "%"); P(PhysicalKey.Digit6, "6", "&");
        P(PhysicalKey.Digit7, "7", "/", "{"); P(PhysicalKey.Digit8, "8", "(", "["); P(PhysicalKey.Digit9, "9", ")", "]");
        P(PhysicalKey.Digit0, "0", "=", "}");
        P(PhysicalKey.Minus, "ß", "?", "\\"); P(PhysicalKey.Equal, KeyValues.Dead, KeyValues.Dead);
        P(PhysicalKey.BracketLeft, "ü", "Ü"); P(PhysicalKey.BracketRight, "+", "*", "~");
        P(PhysicalKey.Semicolon, "ö", "Ö"); P(PhysicalKey.Quote, "ä", "Ä"); P(PhysicalKey.Backslash, "#", "'");
        P(PhysicalKey.Backquote, KeyValues.Dead, "°"); P(PhysicalKey.Comma, ",", ";"); P(PhysicalKey.Period, ".", ":");
        P(PhysicalKey.Slash, "-", "_"); P(PhysicalKey.IntlBackslash, "<", ">", "|");
        return new HeadlessKeyboardLayout("de-DE", d);
    }
}
