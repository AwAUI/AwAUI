using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>
/// KeyboardEvent.code（W3C UI Events 物理键码，与布局无关）→ <see cref="PhysicalKey"/>。
/// <see cref="PhysicalKey"/> 的成员名本来就按 KeyboardEvent.code 命名，这里仍逐项列出而不是反射枚举名：
/// 保持 AOT/裁剪安全，也让枚举改名时在测试里暴露出来。
/// </summary>
internal static class DomKeyMap
{
    /// <summary>KeyboardEvent.code 的合法值都是短 ASCII 标识符；更长的输入直接视为未知，不做查表。</summary>
    public const int MaxCodeLength = 32;

    static readonly Dictionary<string, PhysicalKey> Map = Build();

    static Dictionary<string, PhysicalKey> Build()
    {
        var m = new Dictionary<string, PhysicalKey>(StringComparer.Ordinal);
        void Add(PhysicalKey k, string code) => m.Add(code, k);

        // 字母与数字
        Add(PhysicalKey.KeyA, "KeyA"); Add(PhysicalKey.KeyB, "KeyB"); Add(PhysicalKey.KeyC, "KeyC"); Add(PhysicalKey.KeyD, "KeyD");
        Add(PhysicalKey.KeyE, "KeyE"); Add(PhysicalKey.KeyF, "KeyF"); Add(PhysicalKey.KeyG, "KeyG"); Add(PhysicalKey.KeyH, "KeyH");
        Add(PhysicalKey.KeyI, "KeyI"); Add(PhysicalKey.KeyJ, "KeyJ"); Add(PhysicalKey.KeyK, "KeyK"); Add(PhysicalKey.KeyL, "KeyL");
        Add(PhysicalKey.KeyM, "KeyM"); Add(PhysicalKey.KeyN, "KeyN"); Add(PhysicalKey.KeyO, "KeyO"); Add(PhysicalKey.KeyP, "KeyP");
        Add(PhysicalKey.KeyQ, "KeyQ"); Add(PhysicalKey.KeyR, "KeyR"); Add(PhysicalKey.KeyS, "KeyS"); Add(PhysicalKey.KeyT, "KeyT");
        Add(PhysicalKey.KeyU, "KeyU"); Add(PhysicalKey.KeyV, "KeyV"); Add(PhysicalKey.KeyW, "KeyW"); Add(PhysicalKey.KeyX, "KeyX");
        Add(PhysicalKey.KeyY, "KeyY"); Add(PhysicalKey.KeyZ, "KeyZ");
        Add(PhysicalKey.Digit0, "Digit0"); Add(PhysicalKey.Digit1, "Digit1"); Add(PhysicalKey.Digit2, "Digit2"); Add(PhysicalKey.Digit3, "Digit3");
        Add(PhysicalKey.Digit4, "Digit4"); Add(PhysicalKey.Digit5, "Digit5"); Add(PhysicalKey.Digit6, "Digit6"); Add(PhysicalKey.Digit7, "Digit7");
        Add(PhysicalKey.Digit8, "Digit8"); Add(PhysicalKey.Digit9, "Digit9");

        // 书写区的其余键
        Add(PhysicalKey.Enter, "Enter"); Add(PhysicalKey.Escape, "Escape"); Add(PhysicalKey.Backspace, "Backspace");
        Add(PhysicalKey.Tab, "Tab"); Add(PhysicalKey.Space, "Space"); Add(PhysicalKey.Minus, "Minus"); Add(PhysicalKey.Equal, "Equal");
        Add(PhysicalKey.BracketLeft, "BracketLeft"); Add(PhysicalKey.BracketRight, "BracketRight"); Add(PhysicalKey.Backslash, "Backslash");
        Add(PhysicalKey.Semicolon, "Semicolon"); Add(PhysicalKey.Quote, "Quote"); Add(PhysicalKey.Backquote, "Backquote");
        Add(PhysicalKey.Comma, "Comma"); Add(PhysicalKey.Period, "Period"); Add(PhysicalKey.Slash, "Slash"); Add(PhysicalKey.CapsLock, "CapsLock");

        // 功能键
        Add(PhysicalKey.F1, "F1"); Add(PhysicalKey.F2, "F2"); Add(PhysicalKey.F3, "F3"); Add(PhysicalKey.F4, "F4");
        Add(PhysicalKey.F5, "F5"); Add(PhysicalKey.F6, "F6"); Add(PhysicalKey.F7, "F7"); Add(PhysicalKey.F8, "F8");
        Add(PhysicalKey.F9, "F9"); Add(PhysicalKey.F10, "F10"); Add(PhysicalKey.F11, "F11"); Add(PhysicalKey.F12, "F12");

        // 编辑与方向键
        Add(PhysicalKey.Insert, "Insert"); Add(PhysicalKey.Delete, "Delete"); Add(PhysicalKey.Home, "Home"); Add(PhysicalKey.End, "End");
        Add(PhysicalKey.PageUp, "PageUp"); Add(PhysicalKey.PageDown, "PageDown");
        Add(PhysicalKey.ArrowLeft, "ArrowLeft"); Add(PhysicalKey.ArrowRight, "ArrowRight");
        Add(PhysicalKey.ArrowUp, "ArrowUp"); Add(PhysicalKey.ArrowDown, "ArrowDown");

        // 修饰键
        Add(PhysicalKey.ShiftLeft, "ShiftLeft"); Add(PhysicalKey.ShiftRight, "ShiftRight");
        Add(PhysicalKey.ControlLeft, "ControlLeft"); Add(PhysicalKey.ControlRight, "ControlRight");
        Add(PhysicalKey.AltLeft, "AltLeft"); Add(PhysicalKey.AltRight, "AltRight");
        Add(PhysicalKey.MetaLeft, "MetaLeft"); Add(PhysicalKey.MetaRight, "MetaRight");
        Add(PhysicalKey.ContextMenu, "ContextMenu");

        // 小键盘
        Add(PhysicalKey.NumLock, "NumLock");
        Add(PhysicalKey.Numpad0, "Numpad0"); Add(PhysicalKey.Numpad1, "Numpad1"); Add(PhysicalKey.Numpad2, "Numpad2");
        Add(PhysicalKey.Numpad3, "Numpad3"); Add(PhysicalKey.Numpad4, "Numpad4"); Add(PhysicalKey.Numpad5, "Numpad5");
        Add(PhysicalKey.Numpad6, "Numpad6"); Add(PhysicalKey.Numpad7, "Numpad7"); Add(PhysicalKey.Numpad8, "Numpad8");
        Add(PhysicalKey.Numpad9, "Numpad9");
        Add(PhysicalKey.NumpadAdd, "NumpadAdd"); Add(PhysicalKey.NumpadSubtract, "NumpadSubtract");
        Add(PhysicalKey.NumpadMultiply, "NumpadMultiply"); Add(PhysicalKey.NumpadDivide, "NumpadDivide");
        Add(PhysicalKey.NumpadDecimal, "NumpadDecimal"); Add(PhysicalKey.NumpadEnter, "NumpadEnter");
        Add(PhysicalKey.NumpadEqual, "NumpadEqual"); Add(PhysicalKey.NumpadComma, "NumpadComma");

        // ISO / JIS 布局的额外键与其余功能键
        Add(PhysicalKey.IntlBackslash, "IntlBackslash"); Add(PhysicalKey.IntlRo, "IntlRo"); Add(PhysicalKey.IntlYen, "IntlYen");
        Add(PhysicalKey.PrintScreen, "PrintScreen"); Add(PhysicalKey.ScrollLock, "ScrollLock"); Add(PhysicalKey.Pause, "Pause");
        Add(PhysicalKey.F13, "F13"); Add(PhysicalKey.F14, "F14"); Add(PhysicalKey.F15, "F15"); Add(PhysicalKey.F16, "F16");
        Add(PhysicalKey.F17, "F17"); Add(PhysicalKey.F18, "F18"); Add(PhysicalKey.F19, "F19"); Add(PhysicalKey.F20, "F20");
        Add(PhysicalKey.F21, "F21"); Add(PhysicalKey.F22, "F22"); Add(PhysicalKey.F23, "F23"); Add(PhysicalKey.F24, "F24");

        // 别名：旧版 Firefox / Chrome 把 Meta 键报告为 OSLeft / OSRight
        Add(PhysicalKey.MetaLeft, "OSLeft"); Add(PhysicalKey.MetaRight, "OSRight");
        return m;
    }

    /// <summary>未知、空或超长的码返回 <see cref="PhysicalKey.Unknown"/>。</summary>
    public static PhysicalKey ToPhysicalKey(string? code)
    {
        if (code is null || code.Length == 0 || code.Length > MaxCodeLength) return PhysicalKey.Unknown;
        return Map.TryGetValue(code, out var k) ? k : PhysicalKey.Unknown;
    }

    /// <summary>表中全部 (code, key) 对（测试用）。</summary>
    public static IEnumerable<KeyValuePair<string, PhysicalKey>> Entries => Map;

    /// <summary>
    /// 修饰键位 → <see cref="KeyModifiers"/>（指针事件与不产生字符的按键）。AltGraph 位被忽略：它只在 AltGr 真的产生字符时有意义，
    /// 见 <see cref="ToKeyModifiers"/>。
    /// </summary>
    public static KeyModifiers ToModifiers(int mask)
    {
        var m = KeyModifiers.None;
        if ((mask & DomProtocol.ModShift) != 0) m |= KeyModifiers.Shift;
        if ((mask & DomProtocol.ModControl) != 0) m |= KeyModifiers.Control;
        if ((mask & DomProtocol.ModAlt) != 0) m |= KeyModifiers.Alt;
        if ((mask & DomProtocol.ModMeta) != 0) m |= KeyModifiers.Meta;
        return m;
    }

    /// <summary>
    /// 按键事件的修饰键。AltGr 输入字符时（<c>getModifierState("AltGraph")</c> 或 Windows 上报告的 Ctrl+Alt，且
    /// <c>KeyboardEvent.key</c> 是字符或死键、未按 Meta）报告 <see cref="KeyModifiers.AltGraph"/>，并去掉由它模拟出来的 Ctrl 与 Alt，
    /// 框架因此不会把 AltGr+Q（@）当成 Ctrl+Alt+Q 快捷键。不产生字符的组合（AltGr+方向键等）保持浏览器报告的修饰键。
    /// </summary>
    public static KeyModifiers ToKeyModifiers(int mask, string? keyValue)
    {
        var m = ToModifiers(mask);
        if (!KeyboardPolicy.IsAltGraphText(m, keyValue, (mask & DomProtocol.ModAltGraph) != 0)) return m;
        const KeyModifiers ctrlAlt = KeyModifiers.Control | KeyModifiers.Alt;
        // Windows：AltGr = Ctrl+Alt（两者都去掉）；macOS 的某些浏览器把 Option 报告为 AltGraph + Alt（去掉 Alt）
        if ((m & ctrlAlt) == ctrlAlt) m &= ~ctrlAlt;
        else m &= ~KeyModifiers.Alt;
        return m | KeyModifiers.AltGraph;
    }

    /// <summary>KeyboardEvent.key → 逻辑键：空或超长（不可信的页面脚本）为 null。</summary>
    public static string? ToLogicalKey(string? keyValue)
    {
        if (string.IsNullOrEmpty(keyValue) || keyValue!.Length > KeyValues.MaxLength) return null;
        foreach (char c in keyValue)
            if (c < 0x20 || c == 0x7F) return null;
        return keyValue;
    }
}

/// <summary>
/// 键盘事件在浏览器侧的处理策略：哪些按键交给框架、哪些阻止浏览器默认行为。
/// <para>
/// 文本一律经隐藏的代理 &lt;textarea&gt; 的 beforeinput / composition 事件进入（design/04「不从按键推断字符」），
/// 所以产生文本的按键绝不能 preventDefault，否则浏览器不会再派发 beforeinput 与输入法事件。
/// </para>
/// </summary>
internal static class KeyboardPolicy
{
    /// <summary>输入法处理中的按键（KeyboardEvent.isComposing，或 keyCode 229）不交给框架：组合期间的方向键、回车属于候选窗。</summary>
    public const int ImeProcessKeyCode = 229;

    public static bool ShouldForward(bool isComposing, int keyCode) => !isComposing && keyCode != ImeProcessKeyCode;

    /// <summary>
    /// 是否阻止浏览器默认行为。阻止：焦点移出（Tab）、页面滚动（方向/翻页/Home/End）、后退等导航与编辑键、
    /// 以及 Ctrl/⌘ 组合键（保存页面、打印、查找等）。放行：会产生文本的按键、剪贴板组合键（让浏览器派发
    /// copy / cut / paste 事件）、刷新与开发者工具（F5、F12、Ctrl+R、Ctrl+Shift+I/J）。
    /// </summary>
    /// <param name="key">物理键。</param>
    /// <param name="modifiers">修饰键。</param>
    /// <param name="isComposing">KeyboardEvent.isComposing。</param>
    /// <param name="keyCode">KeyboardEvent.keyCode（229 = 输入法处理中）。</param>
    /// <param name="keyValue">KeyboardEvent.key；用于识别 AltGr 组合出的字符，未知时传 null。</param>
    /// <param name="altGraph">KeyboardEvent.getModifierState("AltGraph")。</param>
    public static bool ShouldPreventDefault(PhysicalKey key, KeyModifiers modifiers, bool isComposing, int keyCode,
        string? keyValue = null, bool altGraph = false)
    {
        if (!ShouldForward(isComposing, keyCode)) return false;
        // Windows 上浏览器把 AltGr 报告为 Ctrl+Alt：德语 AltGr+Q = @、AltGr+E = €、AltGr+7 = { 等产生字符（或死键）的组合
        // 必须放行，否则浏览器不派发 beforeinput，这些字符根本无法输入
        if (IsAltGraphText(modifiers, keyValue, altGraph)) return false;
        bool command = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        switch (key)
        {
            case PhysicalKey.F5:
            case PhysicalKey.F12:
                return false;
            case PhysicalKey.KeyC:
            case PhysicalKey.KeyV:
            case PhysicalKey.KeyX:
            case PhysicalKey.KeyR:
                return false;
            case PhysicalKey.KeyI:
            case PhysicalKey.KeyJ:
                // Ctrl+Shift+I/J（Windows/Linux）与 ⌘⌥I/J（macOS）打开开发者工具
                if (command && (modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0) return false;
                return command;
            case PhysicalKey.Tab:
            case PhysicalKey.Enter:
            case PhysicalKey.NumpadEnter:
            case PhysicalKey.Escape:
            case PhysicalKey.Backspace:
            case PhysicalKey.Delete:
            case PhysicalKey.Insert:
            case PhysicalKey.Home:
            case PhysicalKey.End:
            case PhysicalKey.PageUp:
            case PhysicalKey.PageDown:
            case PhysicalKey.ArrowLeft:
            case PhysicalKey.ArrowRight:
            case PhysicalKey.ArrowUp:
            case PhysicalKey.ArrowDown:
                return true;
            default:
                // 其余按键（含 F1–F11：F11 全屏、F6 地址栏等）不带 Ctrl/⌘ 时交给浏览器，带时归框架
                return command;
        }
    }

    /// <summary>
    /// AltGraph 状态（或 Ctrl+Alt）且 KeyboardEvent.key 是一个可打印字符或死键：这是 AltGr 输入字符，不是快捷键。
    /// 没有 AltGraph 状态、只凭 Ctrl+Alt 推断时排除 ASCII 字母与数字：浏览器对不产生字符的 Ctrl+Alt 组合报告去掉 Ctrl / Alt 后的键值
    /// （US 布局上 Ctrl+Alt+A 的 key 是 "a"），把它们当成 AltGr 文本会让 Ctrl+Alt+字母快捷键失效；AltGr 层产生的字符（@ € { \ ą ł……）
    /// 不是 ASCII 字母数字，报告 AltGraph 状态的浏览器（Chromium、Firefox）也不受这条限制。
    /// </summary>
    internal static bool IsAltGraphText(KeyModifiers modifiers, string? keyValue, bool altGraph)
    {
        if ((modifiers & KeyModifiers.Meta) != 0) return false;
        const KeyModifiers ctrlAlt = KeyModifiers.Control | KeyModifiers.Alt;
        if (!altGraph)
        {
            if ((modifiers & ctrlAlt) != ctrlAlt) return false;
            if (keyValue is { Length: 1 } && IsAsciiLetterOrDigit(keyValue[0])) return false;
        }
        return keyValue == "Dead" || IsPrintableKeyValue(keyValue);
    }

    static bool IsAsciiLetterOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    /// <summary>
    /// KeyboardEvent.key 是否为单个可打印字符（一个 UTF-16 码元或一个代理对）。命名键值（"Enter"、"F1"、"Unidentified"……）
    /// 都是至少两个 ASCII 字母数字，不会被误认。
    /// </summary>
    public static bool IsPrintableKeyValue(string? keyValue)
    {
        if (keyValue is null) return false;
        if (keyValue.Length == 1) return !char.IsControl(keyValue[0]) && !char.IsSurrogate(keyValue[0]);
        return keyValue.Length == 2 && char.IsSurrogatePair(keyValue[0], keyValue[1]);
    }
}
