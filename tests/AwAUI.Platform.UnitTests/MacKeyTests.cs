using AwAUI.MacOS;
using AwAUI.Platform;

namespace AwAUI.Platform.UnitTests;

/// <summary>macOS 后端的按键翻译（键码表与逻辑键）。NSTextInputClient 本身由 macOS 上的探针验证（AppKit 需要主线程，见 design/07）。</summary>
public class MacKeyTests
{
    [Theory]
    [InlineData(0x53, PhysicalKey.Numpad1)]
    [InlineData(0x52, PhysicalKey.Numpad0)]
    [InlineData(0x5C, PhysicalKey.Numpad9)]
    [InlineData(0x4C, PhysicalKey.NumpadEnter)]
    [InlineData(0x24, PhysicalKey.Enter)]
    [InlineData(0x47, PhysicalKey.NumLock)]
    [InlineData(0x0A, PhysicalKey.IntlBackslash)]
    [InlineData(0x5D, PhysicalKey.IntlYen)]
    [InlineData(0x69, PhysicalKey.F13)]
    public void Key_codes(int code, PhysicalKey expected) => Assert.Equal(expected, KeyMap.Get((ushort)code));

    [Theory]
    [InlineData(PhysicalKey.KeyA, "a", "a", KeyModifiers.None, "a")]
    [InlineData(PhysicalKey.KeyA, "A", "A", KeyModifiers.Shift, "A")]
    [InlineData(PhysicalKey.KeyA, "å", "a", KeyModifiers.Alt, "å")]               // Option 组合出的字符
    [InlineData(PhysicalKey.KeyE, "", "e", KeyModifiers.Alt, "Dead")]              // Option+E：死键
    [InlineData(PhysicalKey.KeyZ, "\u001A", "Z", KeyModifiers.Meta | KeyModifiers.Shift, "Z")] // ⌘ 组合：不含 Ctrl 的字符
    [InlineData(PhysicalKey.KeyC, "\u0003", "c", KeyModifiers.Control, "c")]
    [InlineData(PhysicalKey.ArrowLeft, "", "", KeyModifiers.None, "ArrowLeft")]
    [InlineData(PhysicalKey.F1, "", "", KeyModifiers.None, "F1")]
    [InlineData(PhysicalKey.NumpadEnter, "\u0003", "\u0003", KeyModifiers.None, "Enter")]
    [InlineData(PhysicalKey.Numpad1, "1", "1", KeyModifiers.None, "1")]
    [InlineData(PhysicalKey.Backspace, "\u007F", "\u007F", KeyModifiers.None, "Backspace")]
    [InlineData(PhysicalKey.KeyQ, null, null, KeyModifiers.None, "q")]             // 取不到字符：按物理键推断
    public void Logical_keys(PhysicalKey key, string? characters, string? ignoring, KeyModifiers mods, string expected) =>
        Assert.Equal(expected, KeyMap.LogicalKey(key, characters, ignoring, mods));

    [Fact]
    public void Modifier_flags() =>
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Meta, KeyMap.Modifiers((1UL << 17) | (1UL << 20)));
}
