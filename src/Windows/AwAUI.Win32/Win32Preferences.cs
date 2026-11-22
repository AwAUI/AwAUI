using System;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// 系统浅色 / 深色外观：读取「个性化 → 颜色 → 应用模式」的用户设置（<c>AppsUseLightTheme</c>），
/// 随 <see cref="Const.WM_SETTINGCHANGE"/> / <see cref="Const.WM_THEMECHANGED"/> 刷新。所有窗口共用一个实例。
/// </summary>
internal sealed unsafe class Win32AppearancePreference : IAppearancePreference
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public bool PrefersDark { get; private set; }

    public event Action<bool>? PrefersDarkChanged;

    public void Refresh()
    {
        bool value = ReadAppsUseLightTheme() == 0;
        if (value == PrefersDark) return;
        PrefersDark = value;
        PrefersDarkChanged?.Invoke(value);
    }

    /// <summary>0 = 深色，1 = 浅色；读取失败时按浅色处理（Windows 的默认值）。</summary>
    static uint ReadAppsUseLightTheme()
    {
        fixed (char* subKey = PersonalizeKey)
        {
            if (Advapi32.RegOpenKeyExW(Advapi32.HKEY_CURRENT_USER, subKey, 0, Advapi32.KEY_READ, out var key) != 0)
                return 1;
            try
            {
                fixed (char* name = "AppsUseLightTheme")
                {
                    uint size = sizeof(uint), type, value;
                    if (Advapi32.RegQueryValueExW(key, name, IntPtr.Zero, out type, (byte*)&value, ref size) != 0
                        || type != Advapi32.REG_DWORD || size != sizeof(uint))
                        return 1;
                    return value;
                }
            }
            finally
            {
                Advapi32.RegCloseKey(key);
            }
        }
    }
}

/// <summary>辅助功能「显示动画」的反向偏好：关闭动画即减少动态效果（<c>prefers-reduced-motion</c>）。</summary>
internal sealed unsafe class Win32ReducedMotionPreference : IReducedMotionPreference
{
    public bool PrefersReducedMotion { get; private set; }

    public event Action<bool>? PrefersReducedMotionChanged;

    public void Refresh()
    {
        int animations = 1;
        if (User32.SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) == 0) animations = 1;
        bool value = animations == 0;
        if (value == PrefersReducedMotion) return;
        PrefersReducedMotion = value;
        PrefersReducedMotionChanged?.Invoke(value);
    }
}
