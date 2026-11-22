using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.X11;

/// <summary>XSETTINGS 条目的值类型（0 = 整数，1 = 字符串，2 = 颜色）。</summary>
internal enum XSettingKind : byte
{
    Integer = 0,
    String = 1,
    Color = 2,
}

/// <summary>XSETTINGS 颜色：四个 16 位分量（规范不规定色彩空间，桌面通常按 sRGB 解释）。</summary>
internal readonly struct XSettingColor
{
    public XSettingColor(ushort red, ushort green, ushort blue, ushort alpha)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    public ushort Red { get; }
    public ushort Green { get; }
    public ushort Blue { get; }
    public ushort Alpha { get; }
}

/// <summary>XSETTINGS 的一个值；<see cref="Kind"/> 决定哪个成员有效。</summary>
internal readonly struct XSettingValue
{
    XSettingValue(XSettingKind kind, int integer, string? text, XSettingColor color)
    {
        Kind = kind;
        Integer = integer;
        Text = text;
        Color = color;
    }

    public XSettingKind Kind { get; }
    public int Integer { get; }
    public string? Text { get; }
    public XSettingColor Color { get; }

    public static XSettingValue FromInteger(int value) => new(XSettingKind.Integer, value, null, default);
    public static XSettingValue FromString(string value) => new(XSettingKind.String, 0, value, default);
    public static XSettingValue FromColor(XSettingColor value) => new(XSettingKind.Color, 0, null, value);
}

/// <summary>
/// XSETTINGS（freedesktop.org）属性字节流的解析，不依赖 Xlib，便于在任意系统上测试。
/// 格式见 <c>_XSETTINGS_SETTINGS</c>：1 字节字节序、3 字节填充、CARD32 序号、CARD32 条目数，
/// 随后每个条目是类型、名称、变更序号与值。遇到截断或未知类型时返回 false，不抛出异常。
/// </summary>
internal static class XSettingsParser
{
    public static bool TryParse(byte[]? data, out Dictionary<string, XSettingValue> values)
    {
        values = new Dictionary<string, XSettingValue>(StringComparer.Ordinal);
        if (data is null || data.Length < 12) return false;
        if (data[0] > 1) return false; // 0 = LSB first，1 = MSB first
        bool msb = data[0] == 1;

        uint count = ReadU32(data, 8, msb);
        if (count > (uint)(data.Length / 4)) return false; // 每个条目至少 16 字节，这里只做宽松的上限检查

        int offset = 12;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 4 > data.Length) return false;
            byte type = data[offset];
            int nameLength = ReadU16(data, offset + 2, msb);
            offset += 4;

            if (nameLength > data.Length - offset) return false;
            string name = Encoding.UTF8.GetString(data, offset, nameLength);
            offset += Pad4(nameLength);
            if (offset + 4 > data.Length) return false;
            offset += 4; // 该条目的变更序号

            switch (type)
            {
                case (byte)XSettingKind.Integer:
                    if (offset + 4 > data.Length) return false;
                    values[name] = XSettingValue.FromInteger(ReadI32(data, offset, msb));
                    offset += 4;
                    break;
                case (byte)XSettingKind.String:
                {
                    if (offset + 4 > data.Length) return false;
                    uint length = ReadU32(data, offset, msb);
                    offset += 4;
                    if (length > (uint)(data.Length - offset)) return false;
                    values[name] = XSettingValue.FromString(Encoding.UTF8.GetString(data, offset, (int)length));
                    offset += Pad4((int)length);
                    break;
                }
                case (byte)XSettingKind.Color:
                    if (offset + 8 > data.Length) return false;
                    values[name] = XSettingValue.FromColor(new XSettingColor(
                        ReadU16(data, offset, msb), ReadU16(data, offset + 2, msb),
                        ReadU16(data, offset + 4, msb), ReadU16(data, offset + 6, msb)));
                    offset += 8;
                    break;
                default:
                    return false;
            }
            if (offset > data.Length) return false;
        }
        return true;
    }

    static int Pad4(int value) => (value + 3) & ~3;

    static ushort ReadU16(byte[] data, int offset, bool msb) =>
        msb
            ? (ushort)((data[offset] << 8) | data[offset + 1])
            : (ushort)(data[offset] | (data[offset + 1] << 8));

    static uint ReadU32(byte[] data, int offset, bool msb) =>
        msb
            ? (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3])
            : (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    static int ReadI32(byte[] data, int offset, bool msb) => unchecked((int)ReadU32(data, offset, msb));
}

/// <summary>XSETTINGS 到框架偏好的映射（纯逻辑，供 <see cref="X11SettingsPreference"/> 与测试共用）。</summary>
internal static class XSettingsSemantics
{
    /// <summary>
    /// <c>Net/ThemeName</c> 是自由文本，桌面没有统一的明暗标志；名称包含 “dark” 时视为深色主题
    /// （Adwaita-dark、BreezeDark 等常见命名），否则按浅色处理。
    /// </summary>
    public static bool IsDarkTheme(string? themeName) =>
        themeName != null && themeName.IndexOf("dark", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary><c>Gtk/EnableAnimations</c> 为 0 时为减少动效；其他值或缺少该键时为正常动效。</summary>
    public static bool IsReducedMotion(int? enableAnimations) => enableAnimations == 0;
}
