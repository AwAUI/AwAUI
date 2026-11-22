using System.Globalization;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Mask formatting, filtering, completion reporting and editing for <see cref="MaskedTextBox"/>.</summary>
public class MaskedTextBoxTests
{
    static InputElement Input(UIHarness h) =>
        Assert.IsType<InputElement>(h.FindAll(e => e.GetAttribute("part") == "input").Single());

    [Fact]
    public void Mask_renders_prompt_characters_for_empty_positions()
    {
        var h = new UIHarness(() => new MaskedTextBox { Mask = "(000) 000-0000" });
        Assert.Equal("(___) ___-____", Input(h).Value);
    }

    [Fact]
    public void Typing_fills_positions_and_literals_are_inserted()
    {
        var h = new UIHarness(() => new MaskedTextBox { Mask = "(000) 000-0000" });
        var input = Input(h);
        input.Focus();

        h.TypeText("5551234567");
        Assert.Equal("(555) 123-4567", input.Value);
    }

    [Fact]
    public void Characters_that_do_not_match_the_mask_are_ignored()
    {
        var h = new UIHarness(() => new MaskedTextBox { Mask = "LL-00" });
        var input = Input(h);
        input.Focus();

        h.TypeText("ab");
        Assert.Equal("ab-__", input.Value);

        h.TypeText("!?");
        Assert.Equal("ab-__", input.Value);

        h.TypeText("99");
        Assert.Equal("ab-99", input.Value);
    }

    [Fact]
    public void Backspace_clears_the_last_filled_position()
    {
        var h = new UIHarness(() => new MaskedTextBox { Mask = "000" });
        var input = Input(h);
        input.Focus();
        h.TypeText("123");
        Assert.Equal("123", input.Value);

        h.Key(PhysicalKey.Backspace, "Backspace");
        Assert.Equal("12_", input.Value);
    }

    [Fact]
    public void Optional_positions_report_full_but_not_required_completion()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "00-99" });
        var input = Input(h);
        input.Focus();

        Assert.False(box!.MaskCompleted);
        Assert.False(box.MaskFull);

        h.TypeText("12");
        Assert.True(box.MaskCompleted);
        Assert.False(box.MaskFull);

        h.TypeText("34");
        Assert.True(box.MaskFull);
    }

    [Fact]
    public void Text_is_formatted_and_reported_through_text_changed()
    {
        string? reported = null;
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox
        {
            Mask = "(000)",
            DefaultText = "555",
            TextChanged = value => reported = value,
        });
        Assert.Equal("(555)", Input(h).Value);

        var input = Input(h);
        input.Focus();
        h.TypeText("9");
        Assert.Equal("(555)", input.Value); // only three digits fit
        Assert.Equal("(555)", reported);    // the callback always sees the formatted text
    }

    [Fact]
    public void Empty_mask_behaves_like_a_plain_text_box()
    {
        var h = new UIHarness(() => new MaskedTextBox());
        var input = Input(h);
        input.Focus();
        h.TypeText("anything");
        Assert.Equal("anything", input.Value);
    }

    [Fact]
    public void Case_directives_convert_following_characters()
    {
        var h = new UIHarness(() => new MaskedTextBox { Mask = ">LL-<LL" });
        var input = Input(h);
        input.Focus();

        h.TypeText("abcd");
        Assert.Equal("AB-cd", input.Value);
    }

    [Fact]
    public void Optional_directive_makes_only_the_next_position_optional()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "0|0" });
        var input = Input(h);
        input.Focus();

        Assert.False(box!.MaskCompleted);
        h.TypeText("5");
        Assert.Equal("5_", input.Value);
        Assert.True(box.MaskCompleted); // 第二个位置是可选的
        h.TypeText("9");
        Assert.Equal("59", input.Value);
        Assert.True(box.MaskFull);
    }

    [Fact]
    public void Typing_moves_the_caret_to_the_next_empty_edit_position()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "(000) 000" });
        var input = Input(h);
        input.Focus();

        h.TypeText("12");

        Assert.Equal("(12_) ___", input.Value);
        Assert.Equal(3, box!.CaretIndex);
        Assert.Equal(3, box.SelectionStart);
        Assert.Equal(3, box.SelectionEnd);
        Assert.Equal(0, box.SelectionLength);
        Assert.Equal(3, input.SelectionFocus);
    }

    [Fact]
    public void Typing_into_the_middle_overwrites_the_position_and_advances_the_caret()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "(000) 000" });
        var input = Input(h);
        input.Focus();
        h.TypeText("123");

        input.SetSelectionRange(2, 2);
        h.TypeText("9");

        Assert.Equal("(193) ___", input.Value);
        Assert.Equal(6, box!.CaretIndex); // 下一处空槽在第二组数字
    }

    [Fact]
    public void Typing_replaces_the_selected_formatted_range()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "(000) 000" });
        var input = Input(h);
        input.Focus();
        h.TypeText("123");

        input.SetSelectionRange(1, 3); // selects the first two digits
        h.TypeText("9");

        Assert.Equal("(9_3) ___", input.Value);
        Assert.Equal(2, box!.CaretIndex);
    }

    [Fact]
    public void Delete_clears_the_next_editable_position_across_a_literal()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "(000) 000-0000" });
        var input = Input(h);
        input.Focus();
        h.TypeText("5551234567");
        Assert.Equal("(555) 123-4567", input.Value);

        input.SetSelectionRange(9, 9); // the "-" separator
        h.Key(PhysicalKey.Delete, "Delete");

        Assert.Equal("(555) 123-_567", input.Value); // "4" after the separator is cleared
        Assert.Equal(10, box!.CaretIndex);
    }

    [Fact]
    public void Backspace_clears_the_previous_editable_position_across_a_literal()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "(000) 000-0000" });
        var input = Input(h);
        input.Focus();
        h.TypeText("5551234567");

        input.SetSelectionRange(10, 10); // just after the "-" separator
        h.Key(PhysicalKey.Backspace, "Backspace");

        Assert.Equal("(555) 12_-4567", input.Value); // "3" before the separator is cleared
        Assert.Equal(8, box!.CaretIndex);
    }

    [Fact]
    public void Selection_properties_track_the_underlying_input()
    {
        MaskedTextBox? box = null;
        var h = new UIHarness(() => box = new MaskedTextBox { Mask = "(000) 000" });
        var input = Input(h);
        input.Focus();
        h.TypeText("123");

        input.SetSelectionRange(1, 4);

        Assert.Equal(1, box!.SelectionStart);
        Assert.Equal(4, box.SelectionEnd);
        Assert.Equal(3, box.SelectionLength);
        Assert.Equal(4, box.CaretIndex);
    }

    [Fact]
    public void Culture_date_mask_expands_the_culture_date_separator()
    {
        var h = new UIHarness(() => new MaskedTextBox { Mask = "00/00/0000", Culture = new CultureInfo("de-DE") });
        var input = Input(h);
        Assert.Equal("__.__.____", input.Value);

        input.Focus();
        h.TypeText("01022024");

        Assert.Equal("01.02.2024", input.Value);
    }
}
