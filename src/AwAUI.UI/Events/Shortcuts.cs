using System;
using System.Runtime.InteropServices;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>快捷键的平台约定（design/05「Ctrl ↔ Cmd 自动映射」）。</summary>
public static class Shortcuts
{
    static int s_macOverride = -1;

    /// <summary>是否采用 macOS 约定（主修饰键为 Cmd）。默认按运行平台判断，测试或远程场景可覆盖。</summary>
    public static bool UseMacConventions
    {
        get => s_macOverride < 0 ? RuntimeInformation.IsOSPlatform(OSPlatform.OSX) : s_macOverride == 1;
        set => s_macOverride = value ? 1 : 0;
    }

    /// <summary>恢复按运行平台判断。</summary>
    public static void ResetPlatformConventions() => s_macOverride = -1;

    /// <summary>平台的主快捷键修饰键：macOS 为 <see cref="KeyModifiers.Meta"/>，其他平台为 <see cref="KeyModifiers.Control"/>。</summary>
    public static KeyModifiers PrimaryModifier => UseMacConventions ? KeyModifiers.Meta : KeyModifiers.Control;

    public static bool HasPrimaryModifier(KeyModifiers modifiers) => (modifiers & PrimaryModifier) != 0;

    /// <summary>
    /// 只按下平台主修饰键（可另加 Shift）：Ctrl/⌘ + 键的编辑快捷键（全选、复制、剪切、粘贴）用它判断。
    /// 带 Alt 或 <see cref="KeyModifiers.AltGraph"/> 的组合不是这类快捷键——AltGr 产生的字符（德语 AltGr+Q = @）不会触发 Ctrl 快捷键。
    /// </summary>
    public static bool IsPrimaryShortcut(KeyModifiers modifiers) => (modifiers & ~KeyModifiers.Shift) == PrimaryModifier;

    /// <summary>
    /// 快捷键的主键：按逻辑键（当前布局上的字母）匹配字母快捷键——德语 QWERTZ 上 Ctrl+Z 是物理 KeyY、法语 AZERTY 上 Ctrl+A 是物理 KeyQ；
    /// 逻辑键不是 ASCII 字母时（俄语等非拉丁布局、数字与标点键、平台未提供逻辑键）退回物理键，与浏览器和主流桌面程序一致。
    /// </summary>
    public static PhysicalKey ShortcutKey(PhysicalKey physical, string? logicalKey)
    {
        if (logicalKey is { Length: 1 })
        {
            char c = logicalKey[0];
            if (c >= 'a' && c <= 'z') return (PhysicalKey)((int)PhysicalKey.KeyA + (c - 'a'));
            if (c >= 'A' && c <= 'Z') return (PhysicalKey)((int)PhysicalKey.KeyA + (c - 'A'));
        }
        return physical;
    }

    /// <summary>
    /// 把「Ctrl/Cmd 语义」规范化：把平台主修饰键映射为 <see cref="KeyModifiers.Control"/>，
    /// macOS 上的物理 Ctrl 映射为 <see cref="KeyModifiers.Meta"/>。应用代码因此可以统一按 Ctrl 书写快捷键。
    /// </summary>
    public static KeyModifiers Normalize(KeyModifiers modifiers)
    {
        if (!UseMacConventions) return modifiers;
        var result = modifiers & ~(KeyModifiers.Control | KeyModifiers.Meta);
        if ((modifiers & KeyModifiers.Meta) != 0) result |= KeyModifiers.Control;
        if ((modifiers & KeyModifiers.Control) != 0) result |= KeyModifiers.Meta;
        return result;
    }
}

/// <summary>
/// 快捷键描述：<c>KeyGesture.Parse("Mod+Shift+Z")</c>。<c>Mod</c> 表示平台主修饰键（macOS 为 Cmd，其他为 Ctrl）；
/// 也可写 <c>Ctrl</c>、<c>Cmd</c>/<c>Meta</c>、<c>Alt</c>/<c>Option</c>、<c>Shift</c>。键名为 <see cref="PhysicalKey"/> 名称，
/// 或单个字母/数字（<c>S</c> → <c>KeyS</c>）。字母按当前布局的逻辑键匹配（<see cref="Shortcuts.ShortcutKey"/>）。
/// </summary>
public readonly struct KeyGesture : IEquatable<KeyGesture>
{
    public KeyGesture(PhysicalKey key, KeyModifiers modifiers, bool usesPrimary = false)
    {
        Key = key;
        Modifiers = modifiers;
        UsesPrimaryModifier = usesPrimary;
    }

    public PhysicalKey Key { get; }

    /// <summary>除 <c>Mod</c> 外显式要求的修饰键。</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>是否要求平台主修饰键（<c>Mod</c>）。</summary>
    public bool UsesPrimaryModifier { get; }

    /// <summary>在当前平台上要求的完整修饰键集合。</summary>
    public KeyModifiers EffectiveModifiers => UsesPrimaryModifier ? Modifiers | Shortcuts.PrimaryModifier : Modifiers;

    /// <summary>字母键按逻辑键匹配（见 <see cref="Shortcuts.ShortcutKey"/>），其余按物理键；修饰键必须完全相同。</summary>
    public bool Matches(KeyEvent e) => Shortcuts.ShortcutKey(e.PhysicalKey, e.Key) == Key && e.Modifiers == EffectiveModifiers;

    public bool Matches(PhysicalKey key, KeyModifiers modifiers) => key == Key && modifiers == EffectiveModifiers;

    public static KeyGesture Parse(string text)
    {
        if (!TryParse(text, out var g)) throw new FormatException("无效的快捷键描述：" + text);
        return g;
    }

    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text!.Split('+');
        var mods = KeyModifiers.None;
        bool primary = false;
        PhysicalKey key = PhysicalKey.Unknown;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0) return false;
            bool last = i == parts.Length - 1;
            if (!last || IsModifierName(p))
            {
                switch (p.ToLowerInvariant())
                {
                    case "mod": primary = true; break;
                    case "ctrl": case "control": mods |= KeyModifiers.Control; break;
                    case "cmd": case "command": case "meta": case "win": case "super": mods |= KeyModifiers.Meta; break;
                    case "alt": case "option": case "opt": mods |= KeyModifiers.Alt; break;
                    case "shift": mods |= KeyModifiers.Shift; break;
                    default: return false;
                }
                if (last) return false;
                continue;
            }
            if (!TryParseKey(p, out key)) return false;
        }
        if (key == PhysicalKey.Unknown) return false;
        gesture = new KeyGesture(key, mods, primary);
        return true;
    }

    static bool IsModifierName(string p)
    {
        switch (p.ToLowerInvariant())
        {
            case "mod": case "ctrl": case "control": case "cmd": case "command": case "meta": case "win": case "super":
            case "alt": case "option": case "opt": case "shift":
                return true;
            default:
                return false;
        }
    }

    static bool TryParseKey(string p, out PhysicalKey key)
    {
        key = PhysicalKey.Unknown;
        if (p.Length == 1)
        {
            char c = char.ToUpperInvariant(p[0]);
            if (c >= 'A' && c <= 'Z') { key = (PhysicalKey)((int)PhysicalKey.KeyA + (c - 'A')); return true; }
            if (c >= '0' && c <= '9') { key = (PhysicalKey)((int)PhysicalKey.Digit0 + (c - '0')); return true; }
            switch (c)
            {
                case '-': key = PhysicalKey.Minus; return true;
                case '=': key = PhysicalKey.Equal; return true;
                case ',': key = PhysicalKey.Comma; return true;
                case '.': key = PhysicalKey.Period; return true;
                case '/': key = PhysicalKey.Slash; return true;
                case ';': key = PhysicalKey.Semicolon; return true;
                case '[': key = PhysicalKey.BracketLeft; return true;
                case ']': key = PhysicalKey.BracketRight; return true;
            }
            return false;
        }
        switch (p.ToLowerInvariant())
        {
            case "esc": key = PhysicalKey.Escape; return true;
            case "del": key = PhysicalKey.Delete; return true;
            case "left": key = PhysicalKey.ArrowLeft; return true;
            case "right": key = PhysicalKey.ArrowRight; return true;
            case "up": key = PhysicalKey.ArrowUp; return true;
            case "down": key = PhysicalKey.ArrowDown; return true;
            case "return": key = PhysicalKey.Enter; return true;
        }
        // 只接受名称（拒绝数字形式），并限定在已定义范围内
        if (char.IsDigit(p[0])) return false;
        return Enum.TryParse(p, true, out key) && key > PhysicalKey.Unknown && key <= PhysicalKeys.Last;
    }

    public bool Equals(KeyGesture other) => Key == other.Key && Modifiers == other.Modifiers && UsesPrimaryModifier == other.UsesPrimaryModifier;

    public override bool Equals(object? obj) => obj is KeyGesture g && Equals(g);

    public override int GetHashCode() => HashCodeCompat.Combine((int)Key, (int)Modifiers, UsesPrimaryModifier ? 1 : 0);

    public override string ToString()
    {
        var s = "";
        if (UsesPrimaryModifier) s += "Mod+";
        if ((Modifiers & KeyModifiers.Control) != 0) s += "Ctrl+";
        if ((Modifiers & KeyModifiers.Meta) != 0) s += "Meta+";
        if ((Modifiers & KeyModifiers.Alt) != 0) s += "Alt+";
        if ((Modifiers & KeyModifiers.Shift) != 0) s += "Shift+";
        return s + Key.ToString();
    }
}
