using System.Globalization;
using AwAUI.Controls.Primitives;

namespace AwAUI.Controls.UnitTests;

/// <summary>Mask parsing, culture literals, value operations and formatted-index mapping for <see cref="MaskedTextProvider"/>.</summary>
public class MaskedTextProviderTests
{
    [Fact]
    public void Mask_renders_literals_and_prompt_characters()
    {
        var provider = new MaskedTextProvider("(000) 000-0000");

        Assert.Equal("(___) ___-____", provider.Text);
        Assert.Equal(14, provider.Length);
        Assert.True(provider.IsEmpty);
        Assert.False(provider.MaskCompleted);
        Assert.False(provider.MaskFull);
    }

    [Fact]
    public void Set_fills_editable_positions_sequentially_and_skips_literals()
    {
        var provider = new MaskedTextProvider("(000) 000-0000");

        provider.Set("5551234567");

        Assert.Equal("(555) 123-4567", provider.Text);
        Assert.False(provider.IsEmpty);
        Assert.True(provider.MaskCompleted);
        Assert.True(provider.MaskFull);
    }

    [Fact]
    public void Set_accepts_an_already_formatted_value()
    {
        var provider = new MaskedTextProvider("(000) 000-0000");

        provider.Set("(555) 123-4567");

        Assert.Equal("(555) 123-4567", provider.Text);
    }

    [Fact]
    public void Set_ignores_characters_that_do_not_match_the_position()
    {
        var provider = new MaskedTextProvider("LL-00");

        provider.Set("ab");
        Assert.Equal("ab-__", provider.Text);

        provider.Set("!?-");
        Assert.Equal("__-__", provider.Text);

        provider.Set("ab99");
        Assert.Equal("ab-99", provider.Text);
    }

    [Fact]
    public void Required_and_optional_positions_drive_completion()
    {
        var provider = new MaskedTextProvider("00-99");

        Assert.False(provider.MaskCompleted);
        Assert.False(provider.MaskFull);

        provider.Set("12");
        Assert.True(provider.MaskCompleted);
        Assert.False(provider.MaskFull);

        provider.Set("1234");
        Assert.Equal("12-34", provider.Text);
        Assert.True(provider.MaskFull);
    }

    [Fact]
    public void Optional_directive_makes_only_the_next_position_optional()
    {
        var provider = new MaskedTextProvider("0|0");

        Assert.False(provider.MaskCompleted);
        provider.Set("5");
        Assert.Equal("5_", provider.Text);
        Assert.True(provider.MaskCompleted);
        provider.Set("59");
        Assert.True(provider.MaskFull);
    }

    [Fact]
    public void Case_directives_convert_following_characters()
    {
        var provider = new MaskedTextProvider(">LL-<LL");

        provider.Set("abcd");

        Assert.Equal("AB-cd", provider.Text);
    }

    [Fact]
    public void Escaped_characters_are_literals()
    {
        var provider = new MaskedTextProvider("\\-00");

        Assert.Equal("-__", provider.Text);
        provider.Set("12");
        Assert.Equal("-12", provider.Text);
    }

    [Fact]
    public void Culture_expands_separators_and_currency_symbols()
    {
        var de = new CultureInfo("de-DE");
        var decimalMask = new MaskedTextProvider("00.00", de);
        Assert.Equal("__,__", decimalMask.Text); // de-DE decimal separator is ","
        decimalMask.Set("1234");
        Assert.Equal("12,34", decimalMask.Text);

        var grouped = new MaskedTextProvider("0,000", de);
        Assert.Equal("_.___", grouped.Text); // de-DE group separator is "."
        grouped.Set("1234");
        Assert.Equal("1.234", grouped.Text);

        var currency = new MaskedTextProvider("$ 000.00", de);
        Assert.Equal("€ ___,__", currency.Text);
        currency.Set("12345");
        Assert.Equal("€ 123,45", currency.Text);
    }

    [Fact]
    public void Culture_separators_can_be_escaped()
    {
        var de = new CultureInfo("de-DE");
        var provider = new MaskedTextProvider("00\\.00", de);

        Assert.Equal("__.__", provider.Text); // the escaped dot stays a dot
        provider.Set("1234");
        Assert.Equal("12.34", provider.Text);
    }

    [Fact]
    public void IncludeLiterals_and_IncludePrompt_control_rendering()
    {
        var provider = new MaskedTextProvider("00-00");
        provider.Set("1234");
        Assert.Equal("12-34", provider.Text);

        provider.IncludeLiterals = false;
        Assert.Equal("1234", provider.Text);
        Assert.Equal(4, provider.Length);

        provider.IncludePrompt = false;
        provider.Clear();
        Assert.Equal("", provider.Text);
    }

    [Fact]
    public void Custom_prompt_character_is_rendered_and_skipped_on_input()
    {
        var provider = new MaskedTextProvider("00", promptChar: '*');

        Assert.Equal("**", provider.Text);
        provider.Set("5*");
        Assert.Equal("5*", provider.Text);
    }

    [Fact]
    public void Prompt_character_cannot_be_a_mask_character()
    {
        Assert.Throws<ArgumentException>(() => new MaskedTextProvider("00", promptChar: '0'));
        Assert.Throws<ArgumentException>(() => new MaskedTextProvider("00", promptChar: '\0'));
    }

    [Fact]
    public void Replace_insert_and_remove_operate_on_mask_positions()
    {
        var provider = new MaskedTextProvider("00-00");
        provider.Set("12");
        Assert.Equal("12-__", provider.Text);

        Assert.True(provider.InsertAt('9', 0));
        Assert.Equal("91-2_", provider.Text);

        Assert.True(provider.RemoveAt(0));
        Assert.Equal("_1-2_", provider.Text);

        Assert.True(provider.Replace('5', 3));
        Assert.Equal("_1-5_", provider.Text);

        Assert.False(provider.Replace('7', 2)); // literal position
        Assert.False(provider.Replace('x', 0)); // does not match a digit
        Assert.False(provider.InsertAt('7', 9)); // out of range
    }

    [Fact]
    public void InsertAt_shifts_following_values_and_fails_when_full()
    {
        var provider = new MaskedTextProvider("0000");
        provider.Set("123");

        Assert.True(provider.InsertAt('9', 0));
        Assert.Equal("9123", provider.Text);

        Assert.False(provider.InsertAt('8', 0)); // all four editable positions are full
    }

    [Fact]
    public void Replace_with_prompt_character_clears_the_position()
    {
        var provider = new MaskedTextProvider("00-00");
        provider.Set("1234");

        Assert.True(provider.Replace('_', 0));
        Assert.Equal("_2-34", provider.Text);
    }

    [Fact]
    public void RemoveRange_clears_editable_positions_in_a_formatted_range()
    {
        var provider = new MaskedTextProvider("00-00");
        provider.Set("1234");

        int removed = provider.RemoveRange(1, 3);

        Assert.Equal(2, removed); // formatted indices 1 and 3 (the "-" at 2 is a literal)
        Assert.Equal("1_-_4", provider.Text);
    }

    [Fact]
    public void Formatted_indices_map_to_and_from_editable_positions()
    {
        var provider = new MaskedTextProvider("(000) 000");

        Assert.Equal(1, provider.FindEditPositionFrom(0, forward: true));
        Assert.Equal(6, provider.FindEditPositionFrom(4, forward: true));
        Assert.Equal(3, provider.FindEditPositionFrom(5, forward: false));
        Assert.Equal(-1, provider.FindEditPositionFrom(9, forward: true));
        Assert.Equal(-1, provider.GetEditPosition(0)); // "(" literal
        Assert.Equal(1, provider.GetEditPosition(1));
        Assert.Equal(3, provider.GetEditPosition(3));
        Assert.Equal(-1, provider.GetEditPosition(4)); // ")" literal
        Assert.Equal(0, provider.GetFormattedIndex(0));
        Assert.Equal(6, provider.GetFormattedIndex(6));
    }
}
