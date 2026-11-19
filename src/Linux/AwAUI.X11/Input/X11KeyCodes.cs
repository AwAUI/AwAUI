using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>
/// X 硬件键码 → 物理键。现代 Xorg / XWayland 使用 evdev 键码规则：X 键码 = Linux evdev 键码（input-event-codes.h）+ 8，
/// 与布局无关。旧式 "xfree86" 键码（非 evdev 驱动）不在支持范围内。
/// </summary>
internal static class X11KeyCodes
{
    static readonly PhysicalKey[] Table = Build();

    static PhysicalKey[] Build()
    {
        var t = new PhysicalKey[256];
        void Set(int evdev, PhysicalKey key) => t[evdev + 8] = key;

        Set(1, PhysicalKey.Escape);
        Set(2, PhysicalKey.Digit1); Set(3, PhysicalKey.Digit2); Set(4, PhysicalKey.Digit3); Set(5, PhysicalKey.Digit4);
        Set(6, PhysicalKey.Digit5); Set(7, PhysicalKey.Digit6); Set(8, PhysicalKey.Digit7); Set(9, PhysicalKey.Digit8);
        Set(10, PhysicalKey.Digit9); Set(11, PhysicalKey.Digit0);
        Set(12, PhysicalKey.Minus); Set(13, PhysicalKey.Equal); Set(14, PhysicalKey.Backspace); Set(15, PhysicalKey.Tab);
        Set(16, PhysicalKey.KeyQ); Set(17, PhysicalKey.KeyW); Set(18, PhysicalKey.KeyE); Set(19, PhysicalKey.KeyR);
        Set(20, PhysicalKey.KeyT); Set(21, PhysicalKey.KeyY); Set(22, PhysicalKey.KeyU); Set(23, PhysicalKey.KeyI);
        Set(24, PhysicalKey.KeyO); Set(25, PhysicalKey.KeyP);
        Set(26, PhysicalKey.BracketLeft); Set(27, PhysicalKey.BracketRight); Set(28, PhysicalKey.Enter);
        Set(29, PhysicalKey.ControlLeft);
        Set(30, PhysicalKey.KeyA); Set(31, PhysicalKey.KeyS); Set(32, PhysicalKey.KeyD); Set(33, PhysicalKey.KeyF);
        Set(34, PhysicalKey.KeyG); Set(35, PhysicalKey.KeyH); Set(36, PhysicalKey.KeyJ); Set(37, PhysicalKey.KeyK);
        Set(38, PhysicalKey.KeyL);
        Set(39, PhysicalKey.Semicolon); Set(40, PhysicalKey.Quote); Set(41, PhysicalKey.Backquote);
        Set(42, PhysicalKey.ShiftLeft); Set(43, PhysicalKey.Backslash);
        Set(44, PhysicalKey.KeyZ); Set(45, PhysicalKey.KeyX); Set(46, PhysicalKey.KeyC); Set(47, PhysicalKey.KeyV);
        Set(48, PhysicalKey.KeyB); Set(49, PhysicalKey.KeyN); Set(50, PhysicalKey.KeyM);
        Set(51, PhysicalKey.Comma); Set(52, PhysicalKey.Period); Set(53, PhysicalKey.Slash); Set(54, PhysicalKey.ShiftRight);
        Set(56, PhysicalKey.AltLeft); Set(57, PhysicalKey.Space); Set(58, PhysicalKey.CapsLock);
        Set(59, PhysicalKey.F1); Set(60, PhysicalKey.F2); Set(61, PhysicalKey.F3); Set(62, PhysicalKey.F4);
        Set(63, PhysicalKey.F5); Set(64, PhysicalKey.F6); Set(65, PhysicalKey.F7); Set(66, PhysicalKey.F8);
        Set(67, PhysicalKey.F9); Set(68, PhysicalKey.F10); Set(87, PhysicalKey.F11); Set(88, PhysicalKey.F12);
        // 小键盘（KEY_KP*）、NumLock / ScrollLock、ISO 102 键、JIS 键、PrintScreen / Pause、F13–F24
        Set(71, PhysicalKey.Numpad7); Set(72, PhysicalKey.Numpad8); Set(73, PhysicalKey.Numpad9); Set(74, PhysicalKey.NumpadSubtract);
        Set(75, PhysicalKey.Numpad4); Set(76, PhysicalKey.Numpad5); Set(77, PhysicalKey.Numpad6); Set(78, PhysicalKey.NumpadAdd);
        Set(79, PhysicalKey.Numpad1); Set(80, PhysicalKey.Numpad2); Set(81, PhysicalKey.Numpad3); Set(82, PhysicalKey.Numpad0);
        Set(83, PhysicalKey.NumpadDecimal); Set(96, PhysicalKey.NumpadEnter); Set(98, PhysicalKey.NumpadDivide);
        Set(55, PhysicalKey.NumpadMultiply); Set(117, PhysicalKey.NumpadEqual); Set(121, PhysicalKey.NumpadComma);
        Set(69, PhysicalKey.NumLock); Set(70, PhysicalKey.ScrollLock);
        Set(86, PhysicalKey.IntlBackslash); Set(89, PhysicalKey.IntlRo); Set(124, PhysicalKey.IntlYen);
        Set(99, PhysicalKey.PrintScreen); Set(119, PhysicalKey.Pause);
        for (int i = 0; i < 12; i++) Set(183 + i, (PhysicalKey)((int)PhysicalKey.F13 + i));
        Set(97, PhysicalKey.ControlRight); Set(100, PhysicalKey.AltRight);
        Set(102, PhysicalKey.Home); Set(103, PhysicalKey.ArrowUp); Set(104, PhysicalKey.PageUp);
        Set(105, PhysicalKey.ArrowLeft); Set(106, PhysicalKey.ArrowRight); Set(107, PhysicalKey.End);
        Set(108, PhysicalKey.ArrowDown); Set(109, PhysicalKey.PageDown); Set(110, PhysicalKey.Insert); Set(111, PhysicalKey.Delete);
        Set(125, PhysicalKey.MetaLeft); Set(126, PhysicalKey.MetaRight); Set(127, PhysicalKey.ContextMenu); // KEY_COMPOSE = Menu 键
        return t;
    }

    public static PhysicalKey Get(uint keycode) => keycode < 256 ? Table[keycode] : PhysicalKey.Unknown;
}
