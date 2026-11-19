using System.Collections.Generic;
using AwAUI.Platform;
using AwAUI.Win32.Interop;

namespace AwAUI.Win32;

/// <summary>键盘消息的扫描码与修饰键状态（扫描码 → 物理键的表在 <see cref="Win32ScanCodes"/>）。</summary>
internal static class KeyMap
{
    public static PhysicalKey Get(int scanCode) => Win32ScanCodes.Get(scanCode);

    /// <summary>
    /// 从 WM_KEYDOWN 等消息取扫描码：lParam 第 16–23 位为扫描码，第 24 位为扩展键标志。
    /// 合成输入（如 SendInput 只给虚拟键）扫描码可能为 0，此时用虚拟键反查。
    /// </summary>
    public static int ScanCode(long lParam, int virtualKey)
    {
        int sc = (int)((lParam >> 16) & 0xFF);
        if (sc == 0)
        {
            uint mapped = User32.MapVirtualKeyW((uint)virtualKey, Const.MAPVK_VK_TO_VSC_EX);
            // MAPVK_VK_TO_VSC_EX 对扩展键返回 0xE0xx（高字节 0xE0 或 0xE1）
            return (int)(mapped & 0xFFFF);
        }
        return (lParam & (1L << 24)) != 0 ? 0xE000 | sc : sc;
    }

    /// <summary>当前修饰键状态（GetKeyState 反映与当前消息同步的键盘状态，高位为 1 表示按下）。</summary>
    public static KeyModifiers Modifiers()
    {
        var m = KeyModifiers.None;
        if (User32.GetKeyState(Const.VK_SHIFT) < 0) m |= KeyModifiers.Shift;
        if (User32.GetKeyState(Const.VK_CONTROL) < 0) m |= KeyModifiers.Control;
        if (User32.GetKeyState(Const.VK_MENU) < 0) m |= KeyModifiers.Alt;
        if (User32.GetKeyState(Const.VK_LWIN) < 0 || User32.GetKeyState(Const.VK_RWIN) < 0) m |= KeyModifiers.Meta;
        return m;
    }
}
