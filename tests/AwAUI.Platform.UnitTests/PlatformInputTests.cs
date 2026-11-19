using AwAUI.Platform;

namespace AwAUI.Platform.UnitTests;

/// <summary>AwAUI.Platform 的输入类型：逻辑键值、按键位置、文本输入范围、剪贴板辅助。</summary>
public class PlatformInputTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("@", true)]
    [InlineData(" ", true)]
    [InlineData("€", true)]
    [InlineData("😀", true)]
    [InlineData("क्ष", true)]       // 一键多字符（非拉丁）
    [InlineData("Enter", false)]
    [InlineData("F1", false)]
    [InlineData("Dead", false)]
    [InlineData("Unidentified", false)]
    [InlineData("ab", false)]
    [InlineData("\u0007", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Character_key_values(string? key, bool expected) => Assert.Equal(expected, KeyValues.IsCharacter(key));

    [Theory]
    [InlineData(PhysicalKey.KeyA, KeyModifiers.None, "a")]
    [InlineData(PhysicalKey.KeyA, KeyModifiers.Shift, "A")]
    [InlineData(PhysicalKey.Digit2, KeyModifiers.Shift, "@")]
    [InlineData(PhysicalKey.Numpad7, KeyModifiers.None, "7")]
    [InlineData(PhysicalKey.NumpadEnter, KeyModifiers.None, "Enter")]
    [InlineData(PhysicalKey.NumpadAdd, KeyModifiers.None, "+")]
    [InlineData(PhysicalKey.F13, KeyModifiers.None, "F13")]
    [InlineData(PhysicalKey.ShiftRight, KeyModifiers.None, "Shift")]
    [InlineData(PhysicalKey.IntlBackslash, KeyModifiers.None, "<")]
    [InlineData(PhysicalKey.IntlRo, KeyModifiers.None, "Unidentified")]
    public void Us_layout_fallback(PhysicalKey key, KeyModifiers mods, string expected) => Assert.Equal(expected, KeyValues.FromPhysicalKey(key, mods));

    [Fact]
    public void Lone_surrogates_are_not_characters()
    {
        Assert.False(KeyValues.IsCharacter(new string((char)0xD800, 1)));
        Assert.False(KeyValues.IsCharacter(new string((char)0xDC00, 1)));
        Assert.False(KeyValues.IsCharacter("a" + (char)0xD800));
    }

    [Fact]
    public void Named_keys_exclude_character_keys()
    {
        Assert.Null(KeyValues.NamedKey(PhysicalKey.KeyA));
        Assert.Null(KeyValues.NamedKey(PhysicalKey.Numpad1));
        Assert.Null(KeyValues.NamedKey(PhysicalKey.Space));
        Assert.Null(KeyValues.NamedKey(PhysicalKey.IntlBackslash));
        Assert.Equal("Enter", KeyValues.NamedKey(PhysicalKey.NumpadEnter));
        Assert.Equal("ArrowUp", KeyValues.NamedKey(PhysicalKey.ArrowUp));
        Assert.Equal("NumLock", KeyValues.NamedKey(PhysicalKey.NumLock));
    }

    [Theory]
    [InlineData(PhysicalKey.ShiftLeft, KeyLocation.Left)]
    [InlineData(PhysicalKey.MetaRight, KeyLocation.Right)]
    [InlineData(PhysicalKey.Numpad5, KeyLocation.Numpad)]
    [InlineData(PhysicalKey.NumpadEnter, KeyLocation.Numpad)]
    [InlineData(PhysicalKey.NumLock, KeyLocation.Standard)]
    [InlineData(PhysicalKey.Enter, KeyLocation.Standard)]
    public void Locations(PhysicalKey key, KeyLocation expected) => Assert.Equal(expected, PhysicalKeys.GetLocation(key));

    [Fact]
    public void Raw_key_events_keep_the_old_constructor_and_sanitize_logical_keys()
    {
        var old = new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyA, KeyModifiers.None, false, 0, TimeSpan.Zero);
        Assert.Null(old.LogicalKey);
        var e = new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.Numpad1, KeyModifiers.None, false, 0, TimeSpan.Zero, "1");
        Assert.Equal("1", e.LogicalKey);
        Assert.Equal(KeyLocation.Numpad, e.Location);
        Assert.Null(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyA, 0, false, 0, TimeSpan.Zero, new string('x', 100)).LogicalKey);
        Assert.Null(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyA, 0, false, 0, TimeSpan.Zero, "").LogicalKey);
        Assert.True(PhysicalKeys.IsModifier(PhysicalKey.NumLock));
        Assert.Equal(PhysicalKey.F24, PhysicalKeys.Last);
    }

    [Fact]
    public void Text_input_ranges_are_normalized()
    {
        Assert.Equal(new TextInputRange(2, 5), new TextInputRange(5, 2));
        Assert.Equal(new TextInputRange(0, 3), new TextInputRange(-4, 3));
        Assert.Equal(new TextInputRange(3, 4), new TextInputRange(3, 9).Clamp(4));
        Assert.True(TextInputRange.Caret(7).IsEmpty);
        Assert.Equal(3, new TextInputRange(1, 4).Length);
        var o = new TextInputOptions(TextInputContentType.Password);
        Assert.True(o.IsPassword);
        Assert.False(o.AutoCorrect);
    }

    [Fact]
    public void Clipboard_helpers()
    {
        Assert.Equal("a\nb\nc\n", Clipboard.NormalizeNewlines("a\r\nb\rc\n"));
        Assert.Equal("ab", Clipboard.LimitLength("abc", 2));
        Assert.Equal("a", Clipboard.LimitLength("a😀", 2)); // 不切开代理对
        Assert.Equal("x", Clipboard.LimitLength("x"));
    }
}
