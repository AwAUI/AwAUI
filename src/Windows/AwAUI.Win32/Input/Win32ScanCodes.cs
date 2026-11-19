using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Win32;

/// <summary>
/// Windows 扫描码（PS/2 Set 1，扩展键带 0xE0 前缀记为 0xE0xx）→ 物理键。
/// 扫描码与键盘布局无关，正好对应 W3C KeyboardEvent.code。
/// </summary>
internal static class Win32ScanCodes
{
    static readonly Dictionary<int, PhysicalKey> Map = new()
    {
        [0x01] = PhysicalKey.Escape,
        [0x02] = PhysicalKey.Digit1, [0x03] = PhysicalKey.Digit2, [0x04] = PhysicalKey.Digit3, [0x05] = PhysicalKey.Digit4,
        [0x06] = PhysicalKey.Digit5, [0x07] = PhysicalKey.Digit6, [0x08] = PhysicalKey.Digit7, [0x09] = PhysicalKey.Digit8,
        [0x0A] = PhysicalKey.Digit9, [0x0B] = PhysicalKey.Digit0, [0x0C] = PhysicalKey.Minus, [0x0D] = PhysicalKey.Equal,
        [0x0E] = PhysicalKey.Backspace, [0x0F] = PhysicalKey.Tab,
        [0x10] = PhysicalKey.KeyQ, [0x11] = PhysicalKey.KeyW, [0x12] = PhysicalKey.KeyE, [0x13] = PhysicalKey.KeyR,
        [0x14] = PhysicalKey.KeyT, [0x15] = PhysicalKey.KeyY, [0x16] = PhysicalKey.KeyU, [0x17] = PhysicalKey.KeyI,
        [0x18] = PhysicalKey.KeyO, [0x19] = PhysicalKey.KeyP, [0x1A] = PhysicalKey.BracketLeft, [0x1B] = PhysicalKey.BracketRight,
        [0x1C] = PhysicalKey.Enter, [0x1D] = PhysicalKey.ControlLeft,
        [0x1E] = PhysicalKey.KeyA, [0x1F] = PhysicalKey.KeyS, [0x20] = PhysicalKey.KeyD, [0x21] = PhysicalKey.KeyF,
        [0x22] = PhysicalKey.KeyG, [0x23] = PhysicalKey.KeyH, [0x24] = PhysicalKey.KeyJ, [0x25] = PhysicalKey.KeyK,
        [0x26] = PhysicalKey.KeyL, [0x27] = PhysicalKey.Semicolon, [0x28] = PhysicalKey.Quote, [0x29] = PhysicalKey.Backquote,
        [0x2A] = PhysicalKey.ShiftLeft, [0x2B] = PhysicalKey.Backslash,
        [0x2C] = PhysicalKey.KeyZ, [0x2D] = PhysicalKey.KeyX, [0x2E] = PhysicalKey.KeyC, [0x2F] = PhysicalKey.KeyV,
        [0x30] = PhysicalKey.KeyB, [0x31] = PhysicalKey.KeyN, [0x32] = PhysicalKey.KeyM, [0x33] = PhysicalKey.Comma,
        [0x34] = PhysicalKey.Period, [0x35] = PhysicalKey.Slash, [0x36] = PhysicalKey.ShiftRight,
        [0x38] = PhysicalKey.AltLeft, [0x39] = PhysicalKey.Space, [0x3A] = PhysicalKey.CapsLock,
        [0x3B] = PhysicalKey.F1, [0x3C] = PhysicalKey.F2, [0x3D] = PhysicalKey.F3, [0x3E] = PhysicalKey.F4,
        [0x3F] = PhysicalKey.F5, [0x40] = PhysicalKey.F6, [0x41] = PhysicalKey.F7, [0x42] = PhysicalKey.F8,
        [0x43] = PhysicalKey.F9, [0x44] = PhysicalKey.F10, [0x57] = PhysicalKey.F11, [0x58] = PhysicalKey.F12,

        // 扩展键（0xE0 前缀）
        [0xE01D] = PhysicalKey.ControlRight, [0xE038] = PhysicalKey.AltRight,
        [0xE047] = PhysicalKey.Home, [0xE048] = PhysicalKey.ArrowUp, [0xE049] = PhysicalKey.PageUp,
        [0xE04B] = PhysicalKey.ArrowLeft, [0xE04D] = PhysicalKey.ArrowRight,
        [0xE04F] = PhysicalKey.End, [0xE050] = PhysicalKey.ArrowDown, [0xE051] = PhysicalKey.PageDown,
        [0xE052] = PhysicalKey.Insert, [0xE053] = PhysicalKey.Delete,
        [0xE05B] = PhysicalKey.MetaLeft, [0xE05C] = PhysicalKey.MetaRight, [0xE05D] = PhysicalKey.ContextMenu,

        // 小键盘（NumLock 关闭时虚拟键变成导航键，扫描码不变）
        [0x52] = PhysicalKey.Numpad0, [0x4F] = PhysicalKey.Numpad1, [0x50] = PhysicalKey.Numpad2, [0x51] = PhysicalKey.Numpad3,
        [0x4B] = PhysicalKey.Numpad4, [0x4C] = PhysicalKey.Numpad5, [0x4D] = PhysicalKey.Numpad6, [0x47] = PhysicalKey.Numpad7,
        [0x48] = PhysicalKey.Numpad8, [0x49] = PhysicalKey.Numpad9, [0x53] = PhysicalKey.NumpadDecimal,
        [0x37] = PhysicalKey.NumpadMultiply, [0x4A] = PhysicalKey.NumpadSubtract, [0x4E] = PhysicalKey.NumpadAdd,
        [0xE035] = PhysicalKey.NumpadDivide, [0xE01C] = PhysicalKey.NumpadEnter, [0x59] = PhysicalKey.NumpadEqual,
        [0x7E] = PhysicalKey.NumpadComma,
        // NumLock 带扩展位；Pause（E1 1D 45）在消息里是不带扩展位的 0x45
        [0xE045] = PhysicalKey.NumLock, [0x45] = PhysicalKey.Pause, [0x46] = PhysicalKey.ScrollLock,
        [0xE037] = PhysicalKey.PrintScreen, [0x54] = PhysicalKey.PrintScreen, // Alt+PrintScreen 报告为 SysRq（0x54）

        // ISO / JIS 键与 F13–F24
        [0x56] = PhysicalKey.IntlBackslash, [0x73] = PhysicalKey.IntlRo, [0x7D] = PhysicalKey.IntlYen,
        [0x64] = PhysicalKey.F13, [0x65] = PhysicalKey.F14, [0x66] = PhysicalKey.F15, [0x67] = PhysicalKey.F16,
        [0x68] = PhysicalKey.F17, [0x69] = PhysicalKey.F18, [0x6A] = PhysicalKey.F19, [0x6B] = PhysicalKey.F20,
        [0x6C] = PhysicalKey.F21, [0x6D] = PhysicalKey.F22, [0x6E] = PhysicalKey.F23, [0x76] = PhysicalKey.F24,
    };

    public static PhysicalKey Get(int scanCode) => Map.TryGetValue(scanCode, out var k) ? k : PhysicalKey.Unknown;

    /// <summary>表中全部 (扫描码, 物理键)（测试用）。</summary>
    public static IEnumerable<KeyValuePair<int, PhysicalKey>> Entries => Map;
}
