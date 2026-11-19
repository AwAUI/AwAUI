

namespace AwAUI.UI.UnitTests;

/// <summary>DevTools 写入的声明文本校验：以完整注释结尾是合法的，末尾的 '/' 记号仍会被拒绝（会与拼接的 '*' 组成注释）。</summary>
public class StyleTextValidationTests
{
    [Theory]
    [InlineData("color: red /* note */")]
    [InlineData("color: red; /* a */ /* b */  ")]
    [InlineData("grid-area: 1 / 2")]
    public void Accepts_well_formed_text(string text) => ElementStyleInspector.ValidateDeclarationText(text, "Declaration text");

    [Theory]
    [InlineData("color: red /")]
    [InlineData("color: red / ")]
    [InlineData("color: red */")]   // "*/" 在注释外：'*' 与 '/' 是两个记号
    [InlineData("color: red \\")]
    [InlineData("color: red /* open")]
    public void Rejects_text_that_could_change_the_surrounding_rule(string text) =>
        Assert.Throws<FormatException>(() => ElementStyleInspector.ValidateDeclarationText(text, "Declaration text"));
}
