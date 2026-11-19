using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.MacOS;

/// <summary>macOS 虚拟键码（Carbon kVK_*）→ 物理键。</summary>
internal static class KeyMap
{
    static readonly Dictionary<ushort, PhysicalKey> Map = new()
    {
        [0x00] = PhysicalKey.KeyA, [0x0B] = PhysicalKey.KeyB, [0x08] = PhysicalKey.KeyC, [0x02] = PhysicalKey.KeyD,
        [0x0E] = PhysicalKey.KeyE, [0x03] = PhysicalKey.KeyF, [0x05] = PhysicalKey.KeyG, [0x04] = PhysicalKey.KeyH,
        [0x22] = PhysicalKey.KeyI, [0x26] = PhysicalKey.KeyJ, [0x28] = PhysicalKey.KeyK, [0x25] = PhysicalKey.KeyL,
        [0x2E] = PhysicalKey.KeyM, [0x2D] = PhysicalKey.KeyN, [0x1F] = PhysicalKey.KeyO, [0x23] = PhysicalKey.KeyP,
        [0x0C] = PhysicalKey.KeyQ, [0x0F] = PhysicalKey.KeyR, [0x01] = PhysicalKey.KeyS, [0x11] = PhysicalKey.KeyT,
        [0x20] = PhysicalKey.KeyU, [0x09] = PhysicalKey.KeyV, [0x0D] = PhysicalKey.KeyW, [0x07] = PhysicalKey.KeyX,
        [0x10] = PhysicalKey.KeyY, [0x06] = PhysicalKey.KeyZ,
        [0x1D] = PhysicalKey.Digit0, [0x12] = PhysicalKey.Digit1, [0x13] = PhysicalKey.Digit2, [0x14] = PhysicalKey.Digit3,
        [0x15] = PhysicalKey.Digit4, [0x17] = PhysicalKey.Digit5, [0x16] = PhysicalKey.Digit6, [0x1A] = PhysicalKey.Digit7,
        [0x1C] = PhysicalKey.Digit8, [0x19] = PhysicalKey.Digit9,
        [0x24] = PhysicalKey.Enter, [0x35] = PhysicalKey.Escape, [0x33] = PhysicalKey.Backspace, [0x30] = PhysicalKey.Tab,
        [0x31] = PhysicalKey.Space, [0x1B] = PhysicalKey.Minus, [0x18] = PhysicalKey.Equal, [0x21] = PhysicalKey.BracketLeft,
        [0x1E] = PhysicalKey.BracketRight, [0x2A] = PhysicalKey.Backslash, [0x29] = PhysicalKey.Semicolon, [0x27] = PhysicalKey.Quote,
        [0x32] = PhysicalKey.Backquote, [0x2B] = PhysicalKey.Comma, [0x2F] = PhysicalKey.Period, [0x2C] = PhysicalKey.Slash,
        [0x39] = PhysicalKey.CapsLock,
        [0x7A] = PhysicalKey.F1, [0x78] = PhysicalKey.F2, [0x63] = PhysicalKey.F3, [0x76] = PhysicalKey.F4,
        [0x60] = PhysicalKey.F5, [0x61] = PhysicalKey.F6, [0x62] = PhysicalKey.F7, [0x64] = PhysicalKey.F8,
        [0x65] = PhysicalKey.F9, [0x6D] = PhysicalKey.F10, [0x67] = PhysicalKey.F11, [0x6F] = PhysicalKey.F12,
        [0x72] = PhysicalKey.Insert, [0x75] = PhysicalKey.Delete, [0x73] = PhysicalKey.Home, [0x77] = PhysicalKey.End,
        [0x74] = PhysicalKey.PageUp, [0x79] = PhysicalKey.PageDown, [0x7B] = PhysicalKey.ArrowLeft, [0x7C] = PhysicalKey.ArrowRight,
        [0x7E] = PhysicalKey.ArrowUp, [0x7D] = PhysicalKey.ArrowDown,
        [0x38] = PhysicalKey.ShiftLeft, [0x3C] = PhysicalKey.ShiftRight, [0x3B] = PhysicalKey.ControlLeft, [0x3E] = PhysicalKey.ControlRight,
        [0x3A] = PhysicalKey.AltLeft, [0x3D] = PhysicalKey.AltRight, [0x37] = PhysicalKey.MetaLeft, [0x36] = PhysicalKey.MetaRight,
        [0x6E] = PhysicalKey.ContextMenu,

        // 小键盘（kVK_ANSI_Keypad*）；Mac 键盘小键盘左上角的 Clear 键在 W3C 中的 code 为 NumLock
        [0x52] = PhysicalKey.Numpad0, [0x53] = PhysicalKey.Numpad1, [0x54] = PhysicalKey.Numpad2, [0x55] = PhysicalKey.Numpad3,
        [0x56] = PhysicalKey.Numpad4, [0x57] = PhysicalKey.Numpad5, [0x58] = PhysicalKey.Numpad6, [0x59] = PhysicalKey.Numpad7,
        [0x5B] = PhysicalKey.Numpad8, [0x5C] = PhysicalKey.Numpad9,
        [0x41] = PhysicalKey.NumpadDecimal, [0x43] = PhysicalKey.NumpadMultiply, [0x45] = PhysicalKey.NumpadAdd,
        [0x47] = PhysicalKey.NumLock, [0x4B] = PhysicalKey.NumpadDivide, [0x4C] = PhysicalKey.NumpadEnter,
        [0x4E] = PhysicalKey.NumpadSubtract, [0x51] = PhysicalKey.NumpadEqual, [0x5F] = PhysicalKey.NumpadComma,

        // ISO / JIS 键（kVK_ISO_Section、kVK_JIS_Yen、kVK_JIS_Underscore）与 F13–F20
        [0x0A] = PhysicalKey.IntlBackslash, [0x5D] = PhysicalKey.IntlYen, [0x5E] = PhysicalKey.IntlRo,
        [0x69] = PhysicalKey.F13, [0x6B] = PhysicalKey.F14, [0x71] = PhysicalKey.F15, [0x6A] = PhysicalKey.F16,
        [0x40] = PhysicalKey.F17, [0x4F] = PhysicalKey.F18, [0x50] = PhysicalKey.F19, [0x5A] = PhysicalKey.F20,
    };

    public static PhysicalKey Get(ushort keyCode) => Map.TryGetValue(keyCode, out var k) ? k : PhysicalKey.Unknown;

    /// <summary>
    /// 逻辑键（W3C <c>KeyboardEvent.key</c>）：非字符键取键名；字符键取 NSEvent 的 <c>characters</c>（含 Shift 与 Option 的效果，
    /// 死键时为空串 → <c>"Dead"</c>），按住 Ctrl / ⌘ 时取 <c>charactersIgnoringModifiers</c>（只保留 Shift 的效果），
    /// 与 Chromium 在 macOS 上的行为一致。功能键私用区字符（U+F700–U+F8FF）与控制字符不作为键值。
    /// </summary>
    public static string LogicalKey(PhysicalKey key, string? characters, string? charactersIgnoringModifiers, KeyModifiers modifiers)
    {
        if (KeyValues.NamedKey(key) is { } named) return named;
        bool command = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        string? value = command ? charactersIgnoringModifiers : characters;
        if (!command && characters is { Length: 0 }) return KeyValues.Dead;
        if (Usable(value)) return value!;
        if (Usable(charactersIgnoringModifiers)) return charactersIgnoringModifiers!;
        return KeyValues.FromPhysicalKey(key, modifiers);
    }

    static bool Usable(string? s)
    {
        if (string.IsNullOrEmpty(s) || s!.Length > KeyValues.MaxLength) return false;
        foreach (char c in s)
            if (c < 0x20 || c == 0x7F || (c >= 0xF700 && c <= 0xF8FF)) return false;
        return true;
    }

    public static KeyModifiers Modifiers(ulong flags)
    {
        var m = KeyModifiers.None;
        if ((flags & (1 << 17)) != 0) m |= KeyModifiers.Shift;
        if ((flags & (1 << 18)) != 0) m |= KeyModifiers.Control;
        if ((flags & (1 << 19)) != 0) m |= KeyModifiers.Alt;
        if ((flags & (1 << 20)) != 0) m |= KeyModifiers.Meta;
        return m;
    }
}
