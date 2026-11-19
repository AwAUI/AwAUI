using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.Syntax;

/// <summary>整个文件：指令与顶层条目按源顺序排列（含空白文本，便于保真与格式化）。</summary>
public sealed class DocumentSyntax : SyntaxNode
{
    public DocumentSyntax(TextSpan span, IReadOnlyList<SyntaxNode> items) : base(span) => Items = items;

    public IReadOnlyList<SyntaxNode> Items { get; }
    public override SyntaxKind Kind => SyntaxKind.Document;
    public override IEnumerable<SyntaxNode> ChildNodes() => Items;
}

/// <summary>文件级指令的种类。</summary>
public enum DirectiveKind
{
    Using,
    Namespace,
    Inherits,
    Implements,
    TypeParam,
    Attribute,
    /// <summary>不支持的指令（如 Razor 的 <c>@page</c>）；已报告 AW4007。</summary>
    Unknown,
}

/// <summary><c>@using X</c> 等文件级指令。<see cref="Value"/> 为去掉首尾空白（及末尾分号）后的参数文本。</summary>
public sealed class DirectiveSyntax : SyntaxNode
{
    public DirectiveSyntax(TextSpan span, DirectiveKind directiveKind, string name, TextSpan nameSpan, string value, TextSpan valueSpan)
        : base(span)
    {
        DirectiveKind = directiveKind;
        Name = name;
        NameSpan = nameSpan;
        Value = value;
        ValueSpan = valueSpan;
    }

    public DirectiveKind DirectiveKind { get; }
    /// <summary>指令名（不含 @）。</summary>
    public string Name { get; }
    public TextSpan NameSpan { get; }
    public string Value { get; }
    public TextSpan ValueSpan { get; }
    public override SyntaxKind Kind => SyntaxKind.Directive;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary><c>@code { … }</c>：类成员，原样搬入生成的类。</summary>
public sealed class CodeBlockSyntax : SyntaxNode
{
    public CodeBlockSyntax(TextSpan span, string code, TextSpan codeSpan, bool isClosed) : base(span)
    {
        Code = code;
        CodeSpan = codeSpan;
        IsClosed = isClosed;
    }

    /// <summary>花括号之间的原始 C# 文本。</summary>
    public string Code { get; }
    public TextSpan CodeSpan { get; }
    public bool IsClosed { get; }
    public override SyntaxKind Kind => SyntaxKind.CodeBlock;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary><c>&lt;style&gt;</c> / <c>&lt;style global&gt;</c>：内容原样保留，交给 CSS 管线。</summary>
public sealed class StyleBlockSyntax : SyntaxNode
{
    public StyleBlockSyntax(TextSpan span, bool isGlobal, IReadOnlyList<AttributeSyntax> attributes, string content, TextSpan contentSpan, bool isClosed,
        bool isSvg = false)
        : base(span)
    {
        IsGlobal = isGlobal;
        Attributes = attributes;
        Content = content;
        ContentSpan = contentSpan;
        IsClosed = isClosed;
        IsSvg = isSvg;
    }

    public bool IsGlobal { get; }

    /// <summary>
    /// 内联 <c>&lt;svg&gt;</c> 子树中的 <c>&lt;style&gt;</c>：属于 SVG 文档（随文档在编译期序列化，由 AwAUI.Svg 应用于文档内部），
    /// 不是组件的样式块；其属性（如 <c>type</c>、<c>media</c>）原样写入 SVG。
    /// </summary>
    public bool IsSvg { get; }
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public string Content { get; }
    public TextSpan ContentSpan { get; }
    public bool IsClosed { get; }
    public override SyntaxKind Kind => SyntaxKind.StyleBlock;
    public override IEnumerable<SyntaxNode> ChildNodes() => Attributes;
}

/// <summary>注释：<c>@* … *@</c>，或 HTML 注释 <c>&lt;!-- … --&gt;</c>（<see cref="IsHtml"/>）。两者都不产生任何输出。</summary>
public sealed class CommentSyntax : SyntaxNode
{
    public CommentSyntax(TextSpan span, string content, bool isClosed, bool isHtml = false) : base(span)
    {
        Content = content;
        IsClosed = isClosed;
        IsHtml = isHtml;
    }

    public string Content { get; }
    public bool IsClosed { get; }

    /// <summary>是否是 HTML 注释 <c>&lt;!-- … --&gt;</c>（否则为 <c>@* … *@</c>）。</summary>
    public bool IsHtml { get; }
    public override SyntaxKind Kind => SyntaxKind.Comment;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary>
/// 标记中的文本。<see cref="Text"/> 是源写法（实体与 <c>@@</c> 未解码），<see cref="Value"/> 是解码后的值。
/// 空白规范化之后两者都是规范化后的结果。
/// </summary>
public sealed class TextSyntax : SyntaxNode
{
    public TextSyntax(TextSpan span, string text, string value) : base(span)
    {
        Text = text;
        Value = value;
    }

    public string Text { get; }
    public string Value { get; }
    public override SyntaxKind Kind => SyntaxKind.Text;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary>C# 表达式：隐式 <c>@a.b(c)</c> 或显式 <c>@( … )</c>。</summary>
public sealed class ExpressionSyntax : SyntaxNode
{
    public ExpressionSyntax(TextSpan span, string code, TextSpan codeSpan, bool isExplicit) : base(span)
    {
        Code = code;
        CodeSpan = codeSpan;
        IsExplicit = isExplicit;
    }

    /// <summary>表达式的 C# 文本（显式形式不含外层括号）。</summary>
    public string Code { get; }
    public TextSpan CodeSpan { get; }
    public bool IsExplicit { get; }
    public override SyntaxKind Kind => SyntaxKind.Expression;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary>标签种类：小写开头为内建元素，大写开头或带限定名为组件（或插槽）。</summary>
public enum ElementKind
{
    Intrinsic,
    Component,
}

/// <summary>元素或组件。</summary>
public sealed class ElementSyntax : SyntaxNode
{
    public ElementSyntax(
        TextSpan span, string name, TextSpan nameSpan, string? typeArguments,
        IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<SyntaxNode> children,
        bool isSelfClosing, TextSpan startTagSpan, TextSpan? endTagSpan)
        : base(span)
    {
        Name = name;
        NameSpan = nameSpan;
        TypeArguments = typeArguments;
        Attributes = attributes;
        Children = children;
        IsSelfClosing = isSelfClosing;
        StartTagSpan = startTagSpan;
        EndTagSpan = endTagSpan;
    }

    public string Name { get; }
    public TextSpan NameSpan { get; }
    /// <summary>泛型组件标签上的类型实参文本（<c>&lt;Grid&lt;int&gt;&gt;</c> 中的 <c>int</c>），没有时为 null。</summary>
    public string? TypeArguments { get; }
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public IReadOnlyList<SyntaxNode> Children { get; }
    public bool IsSelfClosing { get; }
    public TextSpan StartTagSpan { get; }
    /// <summary>结束标签的区间；自闭合或缺失时为 null。</summary>
    public TextSpan? EndTagSpan { get; }
    /// <summary>自闭合，或有匹配的结束标签。</summary>
    public bool IsClosed => IsSelfClosing || EndTagSpan.HasValue;
    public ElementKind ElementKind => Classify(Name);
    public override SyntaxKind Kind => SyntaxKind.Element;

    public override IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (var a in Attributes) yield return a;
        foreach (var c in Children) yield return c;
    }

    public AttributeSyntax? FindAttribute(string fullName)
    {
        foreach (var a in Attributes)
            if (a.FullName == fullName) return a;
        return null;
    }

    internal static ElementKind Classify(string name) =>
        name.Length > 0 && (char.IsUpper(name[0]) || name.IndexOf('.') >= 0) ? ElementKind.Component : ElementKind.Intrinsic;
}

/// <summary>片段 <c>&lt;&gt;…&lt;/&gt;</c>。</summary>
public sealed class FragmentSyntax : SyntaxNode
{
    public FragmentSyntax(TextSpan span, IReadOnlyList<SyntaxNode> children, TextSpan startTagSpan, TextSpan? endTagSpan) : base(span)
    {
        Children = children;
        StartTagSpan = startTagSpan;
        EndTagSpan = endTagSpan;
    }

    public IReadOnlyList<SyntaxNode> Children { get; }
    public TextSpan StartTagSpan { get; }
    public TextSpan? EndTagSpan { get; }
    public bool IsClosed => EndTagSpan.HasValue;
    public override SyntaxKind Kind => SyntaxKind.Fragment;
    public override IEnumerable<SyntaxNode> ChildNodes() => Children;
}

/// <summary>
/// 属性。普通属性如 <c>class="a"</c>；指令属性以 @ 开头：<c>@onclick</c>、<c>@bind</c>、<c>@bind:event</c>、
/// <c>@bind-Value</c>、<c>@key</c>、<c>@ref</c>、<c>@attributes</c>。
/// </summary>
public sealed class AttributeSyntax : SyntaxNode
{
    public AttributeSyntax(TextSpan span, string name, TextSpan nameSpan, bool isDirective, string? parameter, AttributeValueSyntax? value)
        : base(span)
    {
        Name = name;
        NameSpan = nameSpan;
        IsDirective = isDirective;
        Parameter = parameter;
        Value = value;
    }

    /// <summary>属性名；指令属性不含 @ 与 <c>:参数</c>（<c>@bind:event</c> 的 Name 为 <c>bind</c>）。</summary>
    public string Name { get; }
    /// <summary>名称（含 @ 与 :参数）在源中的区间。</summary>
    public TextSpan NameSpan { get; }
    public bool IsDirective { get; }
    /// <summary>指令参数，如 <c>@bind:event</c> 中的 <c>event</c>。</summary>
    public string? Parameter { get; }
    /// <summary>值；只写名称的属性（布尔写法）为 null。</summary>
    public AttributeValueSyntax? Value { get; }

    /// <summary>源中的完整名称写法：<c>@bind:event</c>、<c>class</c>。</summary>
    public string FullName => (IsDirective ? "@" : "") + Name + (Parameter != null ? ":" + Parameter : "");

    public override SyntaxKind Kind => SyntaxKind.Attribute;
    public override IEnumerable<SyntaxNode> ChildNodes() => Value != null ? new SyntaxNode[] { Value } : None;
}

/// <summary>属性值：由字面量片段与表达式片段组成。无引号写法的 <see cref="Quote"/> 为 '\0'。</summary>
public sealed class AttributeValueSyntax : SyntaxNode
{
    public AttributeValueSyntax(TextSpan span, char quote, IReadOnlyList<SyntaxNode> parts) : base(span)
    {
        Quote = quote;
        Parts = parts;
    }

    public char Quote { get; }
    /// <summary><see cref="AttributeLiteralSyntax"/> 与 <see cref="ExpressionSyntax"/> 的序列。</summary>
    public IReadOnlyList<SyntaxNode> Parts { get; }
    public override SyntaxKind Kind => SyntaxKind.AttributeValue;
    public override IEnumerable<SyntaxNode> ChildNodes() => Parts;

    /// <summary>值是否恰好是一个表达式（忽略引号）。</summary>
    public ExpressionSyntax? SingleExpression => Parts.Count == 1 ? Parts[0] as ExpressionSyntax : null;

    /// <summary>值是否只有字面量；是则返回解码后的文本（空值返回空串）。</summary>
    public string? LiteralValue
    {
        get
        {
            if (Parts.Count == 0) return "";
            if (Parts.Count == 1 && Parts[0] is AttributeLiteralSyntax l) return l.Value;
            return null;
        }
    }

    /// <summary>值是否只有字面量；是则返回源写法（未解码，供 C# 类别的属性使用）。</summary>
    public string? LiteralText
    {
        get
        {
            if (Parts.Count == 0) return "";
            if (Parts.Count == 1 && Parts[0] is AttributeLiteralSyntax l) return l.Text;
            return null;
        }
    }
}

/// <summary>属性值中的字面量片段。<see cref="Text"/> 为源写法，<see cref="Value"/> 为解码后的值。</summary>
public sealed class AttributeLiteralSyntax : SyntaxNode
{
    public AttributeLiteralSyntax(TextSpan span, string text, string value) : base(span)
    {
        Text = text;
        Value = value;
    }

    public string Text { get; }
    public string Value { get; }
    public override SyntaxKind Kind => SyntaxKind.AttributeLiteral;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary><c>@{ … }</c> 语句块。</summary>
public sealed class StatementBlockSyntax : SyntaxNode
{
    public StatementBlockSyntax(TextSpan span, CodeBodySyntax body) : base(span) => Body = body;

    public CodeBodySyntax Body { get; }
    public override SyntaxKind Kind => SyntaxKind.StatementBlock;
    public override IEnumerable<SyntaxNode> ChildNodes() => new SyntaxNode[] { Body };
}

/// <summary>代码模式下的一段原始 C#（语句片段）。</summary>
public sealed class CSharpCodeSyntax : SyntaxNode
{
    public CSharpCodeSyntax(TextSpan span, string code) : base(span) => Code = code;

    public string Code { get; }
    public override SyntaxKind Kind => SyntaxKind.CSharpCode;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary>
/// 花括号包围的代码体：C# 片段与标记节点交错。用于 <c>@{ }</c> 与控制流的语句体。
/// 缺少左花括号时 <see cref="HasOpenBrace"/> 为 false、内容为空。
/// </summary>
public sealed class CodeBodySyntax : SyntaxNode
{
    public CodeBodySyntax(TextSpan span, IReadOnlyList<SyntaxNode> nodes, bool hasOpenBrace, bool isClosed) : base(span)
    {
        Nodes = nodes;
        HasOpenBrace = hasOpenBrace;
        IsClosed = isClosed;
    }

    public IReadOnlyList<SyntaxNode> Nodes { get; }
    public bool HasOpenBrace { get; }
    public bool IsClosed { get; }
    public override SyntaxKind Kind => SyntaxKind.CodeBody;
    public override IEnumerable<SyntaxNode> ChildNodes() => Nodes;
}

/// <summary><c>@if … else if … else …</c>。</summary>
public sealed class IfSyntax : SyntaxNode
{
    public IfSyntax(TextSpan span, IReadOnlyList<IfBranchSyntax> branches) : base(span) => Branches = branches;

    public IReadOnlyList<IfBranchSyntax> Branches { get; }
    public override SyntaxKind Kind => SyntaxKind.If;
    public override IEnumerable<SyntaxNode> ChildNodes() => Branches;
}

public enum IfBranchKind
{
    If,
    ElseIf,
    Else,
}

/// <summary>if 的一个分支。else 分支的 <see cref="Condition"/> 为 null。</summary>
public sealed class IfBranchSyntax : SyntaxNode
{
    public IfBranchSyntax(TextSpan span, IfBranchKind branchKind, string? condition, TextSpan conditionSpan, CodeBodySyntax body) : base(span)
    {
        BranchKind = branchKind;
        Condition = condition;
        ConditionSpan = conditionSpan;
        Body = body;
    }

    public IfBranchKind BranchKind { get; }
    public string? Condition { get; }
    public TextSpan ConditionSpan { get; }
    public CodeBodySyntax Body { get; }
    public override SyntaxKind Kind => SyntaxKind.IfBranch;
    public override IEnumerable<SyntaxNode> ChildNodes() => new SyntaxNode[] { Body };
}

public enum LoopKind
{
    Foreach,
    For,
    While,
}

/// <summary><c>@foreach</c>、<c>@for</c>、<c>@while</c>。</summary>
public sealed class LoopSyntax : SyntaxNode
{
    public LoopSyntax(TextSpan span, LoopKind loopKind, string header, TextSpan headerSpan, CodeBodySyntax body) : base(span)
    {
        LoopKind = loopKind;
        Header = header;
        HeaderSpan = headerSpan;
        Body = body;
    }

    public LoopKind LoopKind { get; }
    /// <summary>括号内的 C# 文本。</summary>
    public string Header { get; }
    public TextSpan HeaderSpan { get; }
    public CodeBodySyntax Body { get; }
    public string Keyword => LoopKind switch { LoopKind.Foreach => "foreach", LoopKind.For => "for", _ => "while" };
    public override SyntaxKind Kind => SyntaxKind.Loop;
    public override IEnumerable<SyntaxNode> ChildNodes() => new SyntaxNode[] { Body };
}

/// <summary><c>@switch (x) { case …: … }</c>。</summary>
public sealed class SwitchSyntax : SyntaxNode
{
    public SwitchSyntax(TextSpan span, string expression, TextSpan expressionSpan, IReadOnlyList<SwitchSectionSyntax> sections, bool hasOpenBrace, bool isClosed)
        : base(span)
    {
        Expression = expression;
        ExpressionSpan = expressionSpan;
        Sections = sections;
        HasOpenBrace = hasOpenBrace;
        IsClosed = isClosed;
    }

    public string Expression { get; }
    public TextSpan ExpressionSpan { get; }
    public IReadOnlyList<SwitchSectionSyntax> Sections { get; }
    public bool HasOpenBrace { get; }
    public bool IsClosed { get; }
    public override SyntaxKind Kind => SyntaxKind.Switch;
    public override IEnumerable<SyntaxNode> ChildNodes() => Sections;
}

/// <summary>switch 的一节：一个或多个标签，后接语句与标记。第一个标签之前的内容归入没有标签的一节。</summary>
public sealed class SwitchSectionSyntax : SyntaxNode
{
    public SwitchSectionSyntax(TextSpan span, IReadOnlyList<SwitchLabelSyntax> labels, IReadOnlyList<SyntaxNode> nodes) : base(span)
    {
        Labels = labels;
        Nodes = nodes;
    }

    public IReadOnlyList<SwitchLabelSyntax> Labels { get; }
    public IReadOnlyList<SyntaxNode> Nodes { get; }
    public override SyntaxKind Kind => SyntaxKind.SwitchSection;

    public override IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (var l in Labels) yield return l;
        foreach (var n in Nodes) yield return n;
    }
}

/// <summary><c>case 模式:</c> 或 <c>default:</c>，<see cref="Text"/> 为含冒号的原文。</summary>
public sealed class SwitchLabelSyntax : SyntaxNode
{
    public SwitchLabelSyntax(TextSpan span, string text, bool isDefault) : base(span)
    {
        Text = text;
        IsDefault = isDefault;
    }

    public string Text { get; }
    public bool IsDefault { get; }
    public override SyntaxKind Kind => SyntaxKind.SwitchLabel;
    public override IEnumerable<SyntaxNode> ChildNodes() => None;
}

/// <summary>代码块里的 <c>@:</c> 单行文本：其后到行尾按标记处理（不做空白规范化）。</summary>
public sealed class LineTextSyntax : SyntaxNode
{
    public LineTextSyntax(TextSpan span, IReadOnlyList<SyntaxNode> children) : base(span) => Children = children;

    public IReadOnlyList<SyntaxNode> Children { get; }
    public override SyntaxKind Kind => SyntaxKind.LineText;
    public override IEnumerable<SyntaxNode> ChildNodes() => Children;
}
