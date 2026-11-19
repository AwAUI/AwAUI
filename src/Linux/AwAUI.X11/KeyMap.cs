using AwAUI.Platform;
using AwAUI.X11.Interop;

namespace AwAUI.X11;

/// <summary>键盘事件的物理键（表在 <see cref="X11KeyCodes"/>）与修饰键状态。</summary>
internal static class KeyMap
{
    public static PhysicalKey Get(uint keycode) => X11KeyCodes.Get(keycode);

    /// <summary>
    /// X 事件中的 state 是事件发生「之前」的修饰键状态；按下/抬起修饰键本身时据此修正，
    /// 使按下 Shift 的那个事件就带 Shift（与其他平台一致）。
    /// </summary>
    public static KeyModifiers Modifiers(uint state, PhysicalKey key = PhysicalKey.Unknown, bool? down = null)
    {
        var m = KeyModifiers.None;
        if ((state & Xlib.ShiftMask) != 0) m |= KeyModifiers.Shift;
        if ((state & Xlib.ControlMask) != 0) m |= KeyModifiers.Control;
        if ((state & Xlib.Mod1Mask) != 0) m |= KeyModifiers.Alt;
        if ((state & Xlib.Mod4Mask) != 0) m |= KeyModifiers.Meta;
        // Mod5 通常是 ISO_Level3_Shift（AltGr）：X 不把它报告成 Ctrl / Alt，AltGr 输入的字符不会被当成快捷键
        if ((state & Xlib.Mod5Mask) != 0) m |= KeyModifiers.AltGraph;
        if (down is bool d)
        {
            var own = key switch
            {
                PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight => KeyModifiers.Shift,
                PhysicalKey.ControlLeft or PhysicalKey.ControlRight => KeyModifiers.Control,
                // 右 Alt 在许多布局上是 AltGr（ISO_Level3_Shift，Mod5），不修正
                PhysicalKey.AltLeft => KeyModifiers.Alt,
                PhysicalKey.MetaLeft or PhysicalKey.MetaRight => KeyModifiers.Meta,
                _ => KeyModifiers.None,
            };
            m = d ? m | own : m & ~own;
        }
        return m;
    }
}
