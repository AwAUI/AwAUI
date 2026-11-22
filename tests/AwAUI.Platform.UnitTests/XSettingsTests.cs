using System.Text;
using AwAUI.X11;

namespace AwAUI.Platform.UnitTests;

/// <summary>
/// XSETTINGS 属性字节流解析（<c>_XSETTINGS_SETTINGS</c>）与「主题名 / 动画开关 → 框架偏好」的映射。
/// 原生部分（选择所有者、属性通知）只能在 Linux 上运行，这里只覆盖可用样例数据验证的纯逻辑。
/// </summary>
public class XSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_entries_are_parsed_with_names_padding_and_values(bool bigEndian)
    {
        var data = Build(bigEndian,
            ("Net/ThemeName", (object)"Adwaita-dark"),
            ("Gtk/EnableAnimations", 0),
            ("Net/IconThemeName", "Papirus"),
            ("Gtk/ColorPalette", new ushort[] { 0x1000, 0x2000, 0x3000, 0xFFFF }));

        Assert.True(XSettingsParser.TryParse(data, out var values));
        Assert.Equal(4, values.Count);
        Assert.Equal(XSettingKind.String, values["Net/ThemeName"].Kind);
        Assert.Equal("Adwaita-dark", values["Net/ThemeName"].Text);
        Assert.Equal(XSettingKind.Integer, values["Gtk/EnableAnimations"].Kind);
        Assert.Equal(0, values["Gtk/EnableAnimations"].Integer);
        Assert.Equal("Papirus", values["Net/IconThemeName"].Text);
        Assert.Equal(XSettingKind.Color, values["Gtk/ColorPalette"].Kind);
        Assert.Equal(0x1000, values["Gtk/ColorPalette"].Color.Red);
        Assert.Equal(0xFFFF, values["Gtk/ColorPalette"].Color.Alpha);
    }

    [Fact]
    public void Empty_settings_are_valid()
    {
        Assert.True(XSettingsParser.TryParse(Build(false), out var values));
        Assert.Empty(values);
    }

    [Fact]
    public void Truncated_settings_are_rejected()
    {
        var data = Build(false, ("Net/ThemeName", (object)"BreezeDark"));
        Assert.False(XSettingsParser.TryParse(data.AsSpan(0, data.Length - 1).ToArray(), out _));
    }

    [Fact]
    public void Unknown_value_types_are_rejected()
    {
        var data = Build(false, ("Net/ThemeName", (object)"Adwaita"));
        data[12] = 9; // 第一个条目的类型字节
        Assert.False(XSettingsParser.TryParse(data, out _));
    }

    [Fact]
    public void Invalid_byte_order_is_rejected()
    {
        var data = Build(false, ("Net/ThemeName", (object)"Adwaita"));
        data[0] = 2;
        Assert.False(XSettingsParser.TryParse(data, out _));
    }

    [Theory]
    [InlineData("Adwaita-dark", true)]
    [InlineData("BreezeDark", true)]
    [InlineData("Adwaita", false)]
    [InlineData(null, false)]
    public void Theme_names_map_to_the_dark_appearance(string? theme, bool dark) =>
        Assert.Equal(dark, XSettingsSemantics.IsDarkTheme(theme));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public void Disabled_animations_map_to_reduced_motion(int? enabled, bool reduced) =>
        Assert.Equal(reduced, XSettingsSemantics.IsReducedMotion(enabled));

    /// <summary>按 XSETTINGS 规范写出属性字节流：字节序、3 字节填充、序号、条目数，以及各条目的类型、名称、变更序号和值。</summary>
    static byte[] Build(bool bigEndian, params (string Name, object Value)[] entries)
    {
        var bytes = new List<byte>();
        void U16(int value)
        {
            if (bigEndian)
            {
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
            }
            else
            {
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
            }
        }
        void U32(uint value)
        {
            if (bigEndian)
            {
                bytes.Add((byte)(value >> 24));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
            }
            else
            {
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 24));
            }
        }
        void Pad()
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
        }

        bytes.Add((byte)(bigEndian ? 1 : 0));
        bytes.Add(0);
        bytes.Add(0);
        bytes.Add(0);
        U32(42); // 序号
        U32((uint)entries.Length);
        foreach (var (name, value) in entries)
        {
            byte type = value switch
            {
                int => (byte)XSettingKind.Integer,
                string => (byte)XSettingKind.String,
                ushort[] => (byte)XSettingKind.Color,
                _ => throw new ArgumentException("unsupported test value", nameof(entries)),
            };
            bytes.Add(type);
            bytes.Add(0);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            U16(nameBytes.Length);
            bytes.AddRange(nameBytes);
            Pad();
            U32(7); // 该条目的变更序号
            switch (value)
            {
                case int integer:
                    U32(unchecked((uint)integer));
                    break;
                case string text:
                    var textBytes = Encoding.UTF8.GetBytes(text);
                    U32((uint)textBytes.Length);
                    bytes.AddRange(textBytes);
                    Pad();
                    break;
                case ushort[] color:
                    foreach (ushort component in color) U16(component);
                    break;
            }
        }
        return bytes.ToArray();
    }
}
