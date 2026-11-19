using System;
using System.Collections.Generic;
using AwAUI.Css.Values;

namespace AwAUI.Css;

/// <summary>
/// 元素的内联样式（<c>Style = "padding: 8px"</c>）：声明列表 + 按需解析并缓存的属性值。
/// 内联样式属于作者来源，以「元素附着」的方式排在所有作者规则之上（CSS Cascade 5 §6.2）。
/// </summary>
public sealed class CssInlineStyle
{
    PropertyDeclarationBlock? _block;

    /// <summary>用已解析的声明列表构造（如编译期生成的代码）。</summary>
    public CssInlineStyle(IReadOnlyList<CssDeclaration> declarations)
        => Declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));

    CssInlineStyle(string text, IReadOnlyList<CssDeclaration> declarations, PropertyDeclarationBlock block)
    {
        Text = text;
        Declarations = declarations;
        _block = block;
    }

    /// <summary>
    /// 解析 style 属性文本。语法诊断与值诊断（AW2002 / AW2003）都追加到 <paramref name="diagnostics"/>。
    /// </summary>
    public static CssInlineStyle Parse(string text, List<CssDiagnostic>? diagnostics = null)
    {
        var declarations = ParseDeclarations(text, diagnostics);
        return new CssInlineStyle(text, declarations, PropertyDeclarationBlock.Parse(declarations, diagnostics));
    }

    /// <summary>把 style 属性文本解析为声明列表（只做语法层解析）。</summary>
    public static IReadOnlyList<CssDeclaration> ParseDeclarations(string text, List<CssDiagnostic>? diagnostics = null)
        => StyleSheet.ParseDeclarations(text ?? throw new ArgumentNullException(nameof(text)), diagnostics);

    /// <summary>原始文本；由声明列表构造时为 null。</summary>
    public string? Text { get; }

    public IReadOnlyList<CssDeclaration> Declarations { get; }

    /// <summary>解析后的属性值（首次访问时解析并缓存）。</summary>
    public PropertyDeclarationBlock Block => _block ??= PropertyDeclarationBlock.Parse(Declarations);

    public override string ToString() => Text ?? string.Join("; ", Declarations);
}
