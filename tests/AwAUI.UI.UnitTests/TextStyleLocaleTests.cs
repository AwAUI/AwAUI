using System.Globalization;
using AwAUI.Platform;
using AwAUI.Text;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 语言环境传播（design/19「字体回退」）：内建控件与 Avalonia 一样没有独立的 Language 属性，文本样式取
/// <see cref="CultureInfo.CurrentUICulture"/>，再传给整形（OpenType 的 locl / 汉字变体）与字体回退。
/// </summary>
public class TextStyleLocaleTests
{
    sealed class RecordingShaper : TextShaper
    {
        public List<string?> Languages { get; } = new();

        public override ShapedRun Shape(in ShapeRequest request)
        {
            Languages.Add(request.Language);
            return BasicTextShaper.Instance.Shape(request);
        }
    }

    [Fact]
    public void Text_style_carries_the_ui_culture_into_shaping()
    {
        var shaper = new RecordingShaper();
        var h = new PipelineHarness(new Div { Id = "t", Children = [new Text("ab")] }, engine: new TestTextEngine(shaper));
        h.Frame();

        string? expected = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : null;
        Assert.NotEmpty(shaper.Languages);
        Assert.All(shaper.Languages, language => Assert.Equal(expected, language));
    }

    [Fact]
    public void Chinese_typing_edits_by_utf16_code_units()
    {
        // 输入框的编辑（插入 / 退格 / 选区）以 UTF-16 码元计；汉字各占一个码元，与拉丁文一致
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Ime.Compose("你好");
        f.Ime.CommitComposition("你好");
        f.Frame();
        Assert.Equal("你好", f.Input().Value);

        f.Key(PhysicalKey.Backspace);
        Assert.Equal("你", f.Input().Value);
        Assert.Equal(1, f.Input().SelectionFocus);
    }
}
