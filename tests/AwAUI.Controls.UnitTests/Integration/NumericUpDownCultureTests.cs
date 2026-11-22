using System.Globalization;
using AwAUI.Platform;
using AwAUI.UI;
using Xunit;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>NumericUpDown 的区域性编辑（design/9c 遗留限制）：显示、解析、分组分隔符与 ARIA 值。</summary>
public class NumericUpDownCultureTests
{
    static InputElement Input(UIHarness h) =>
        Assert.IsType<InputElement>(h.FindAll(e => e.GetAttribute("part") == "input").Single());

    static void Retype(UIHarness h, string text)
    {
        var input = Input(h);
        input.Focus();
        input.SetSelectionRange(0, input.Value.Length);
        h.TypeText(text);
    }

    [Fact]
    public void Value_is_formatted_with_the_configured_culture()
    {
        var h = new UIHarness(() => new NumericUpDown
        {
            Culture = new CultureInfo("de-DE"),
            DefaultValue = 1234.5,
            FormatString = "N1",
        });

        var input = Input(h);
        Assert.Equal("1.234,5", input.Value);
        // ARIA 值必须保持不变区域性，辅助技术才能解析
        Assert.Equal("1234.5", input.GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void Typed_text_is_parsed_with_the_configured_culture()
    {
        double? committed = null;
        var h = new UIHarness(() => new NumericUpDown
        {
            Culture = new CultureInfo("de-DE"),
            ValueChanged = value => committed = value,
        });

        Retype(h, "2,25");
        Assert.Equal(2.25, committed);

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("2,25", Input(h).Value);
    }

    [Fact]
    public void Group_separators_follow_the_culture()
    {
        double? committed = null;
        var h = new UIHarness(() => new NumericUpDown
        {
            Culture = new CultureInfo("de-DE"),
            ValueChanged = value => committed = value,
        });

        Retype(h, "1.234,5");
        Assert.Equal(1234.5, committed);

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("1234,5", Input(h).Value);
    }

    [Fact]
    public void Invariant_culture_keeps_the_plain_decimal_point()
    {
        var h = new UIHarness(() => new NumericUpDown
        {
            Culture = CultureInfo.InvariantCulture,
            DefaultValue = 1.5,
        });

        Assert.Equal("1.5", Input(h).Value);
    }
}
