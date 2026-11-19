using AwAUI.Css.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Values;

internal static class ValueTestHelper
{
    /// <summary>在根元素上声明 <c>property: value</c>，返回计算样式。</summary>
    public static ComputedStyle Compute(string property, string value, CssEnvironment? env = null, string extra = "")
    {
        var e = new StyleEngine(env);
        e.AddStyleSheet(StyleSheet.Parse($"div {{ {extra} {property}: {value} }}"));
        return e.Resolve(new El("div"), null);
    }

    /// <summary>计算值文本；值无效时得到初始值的文本。</summary>
    public static string Text(string property, string value, CssEnvironment? env = null, string extra = "")
        => Compute(property, value, env, extra).GetPropertyValueText(property)!;

    /// <summary>声明是否有效（不产生 AW2003/AW2002）。</summary>
    public static bool IsValid(string property, string value)
    {
        var diags = new List<CssDiagnostic>();
        var decls = StyleSheet.ParseDeclarations($"{property}: {value}");
        var block = PropertyDeclarationBlock.Parse(decls, diags);
        return diags.Count == 0 && !block.IsEmpty;
    }

    public static List<CssDiagnostic> Diagnostics(string declarations)
    {
        var diags = new List<CssDiagnostic>();
        PropertyDeclarationBlock.Parse(StyleSheet.ParseDeclarations(declarations), diags);
        return diags;
    }
}
