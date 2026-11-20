using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.CodeGen;
using AwAUI.Markup.Compiler.Analysis;
using AwAUI.Markup.Compiler.Styles;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.Compiler.CodeGen;

/// <summary>C# 生成的选项。</summary>
public sealed class CSharpEmitOptions
{
    /// <summary>没有 @namespace 时使用的命名空间（源生成器传入「项目根命名空间 + 目录」）。</summary>
    public string Namespace { get; set; } = "AwAUI.Generated";

    /// <summary>类名；为 null 时取自文件名。</summary>
    public string? ClassName { get; set; }

    /// <summary>是否输出 <c>#line</c> 映射。</summary>
    public bool EmitLineDirectives { get; set; } = true;

    /// <summary><c>#line</c> 中写入的文件路径（源生成器传入 <c>.awaui</c> 的完整路径）；为 null 时取源文本的路径。</summary>
    public string? LineDirectivePath { get; set; }

    /// <summary><c>[ComponentSource]</c> 中记录的路径（建议相对项目目录，统一用 <c>/</c>）；为 null 时取源文本的路径。</summary>
    public string? ComponentSourcePath { get; set; }

    /// <summary>组件目录：用于区分字符串属性 / 表达式属性 / 插槽；为 null 时按命名约定推断。</summary>
    public ComponentCatalog? Catalog { get; set; }

    /// <summary>语义层面的组件类信息（Props、手写的胶水、State 绑定目标）；为 null 时对 <c>@code</c> 做语法扫描。</summary>
    public ComponentClassInfo? ClassInfo { get; set; }

    /// <summary>是否在类中生成 Props 胶水（<c>PropsEqual</c> / <c>AdoptProps</c> / <c>CanAdoptProps</c>）。</summary>
    public bool EmitPropsGlue { get; set; } = true;

    /// <summary>
    /// 是否输出 <c>#nullable restore</c>（让 <c>@code</c> 沿用项目的可空设置；生成的文件默认处于可空禁用的上下文）。
    /// 该指令需要 C# 8，源生成器在项目语言版本低于 8 时关闭它，以保持生成代码在 C# 7.3 下可编译。
    /// </summary>
    public bool EmitNullableRestore { get; set; } = true;
}

/// <summary>
/// 把规范化后的语法树生成 design/17「代码生成」所示形状的 C#：<c>partial class X : global::AwAUI.UI.Component</c> 与命令式的 <c>Build()</c>，
/// 框架类型全部 <c>global::</c> 限定，局部变量 <c>__</c> 前缀，只用 C# 7.3 语法。
/// <list type="bullet">
/// <item>来自源文件的片段前有 <c>#line</c>，并用前导空格对齐到源中的列，C# 的编译错误与断点落在 <c>.awaui</c> 的行列上；生成的其余代码处于 <c>#line hidden</c>。</item>
/// <item>内建元素按 <see cref="RuntimeElements"/> 映射到运行时实际存在的类型与成员；运行时尚未实现的元素、事件报 AW4012 错误且不生成代码，
/// 行为尚未实现的属性透传为自定义属性并报 AW4012 警告。</item>
/// <item>Props 胶水由共享的 <c>ComponentGlueEmitter</c> 产出（与 C# 源生成器同一份逻辑）。</item>
/// <item><c>&lt;style&gt;</c> 的选择器由 <see cref="ScopedCss"/> 改写后以常量 <c>StyleSheetSource</c> 输出，本组件产出的元素带作用域标记。</item>
/// </list>
/// </summary>
public static class CSharpEmitter
{
    /// <summary>生成 C# 源码（诊断与样式见 <see cref="MarkupCompiler.Compile(MarkupAnalysis, CSharpEmitOptions?)"/>）。</summary>
    public static string Emit(MarkupAnalysis analysis, CSharpEmitOptions? options = null) =>
        new Emitter(analysis, options ?? new CSharpEmitOptions()).Run().Code;

    internal static EmitResult EmitFull(MarkupAnalysis analysis, CSharpEmitOptions? options) =>
        new Emitter(analysis, options ?? new CSharpEmitOptions()).Run();

    internal const string DefaultBase = "global::AwAUI.UI.Component";
    const string UI = RuntimeElements.UI;

    /// <summary>生成文件中默认导入的命名空间（与 Razor 的默认导入相当），使 <c>@code</c> 可以直接写 <c>State&lt;T&gt;</c>、<c>List&lt;T&gt;</c>。</summary>
    internal static readonly string[] DefaultUsings = { "System", "System.Collections.Generic", "System.Linq", "AwAUI.UI" };

    /// <summary>HTML 拼写的属性名 → 组件/元素描述类型上的 C# 属性名（不在表中的按连字符分段转 PascalCase）。</summary>
    static readonly Dictionary<string, string> PropertyNames = new(StringComparer.Ordinal)
    {
        ["tabindex"] = "TabIndex", ["readonly"] = "ReadOnly", ["maxlength"] = "MaxLength", ["minlength"] = "MinLength",
        ["autofocus"] = "AutoFocus", ["autocomplete"] = "AutoComplete", ["inputmode"] = "InputMode",
        ["spellcheck"] = "SpellCheck", ["src"] = "Source", ["type"] = "InputType", ["aria-label"] = "AriaLabel",
    };

    internal sealed class EmitResult
    {
        public EmitResult(string code, IReadOnlyList<MarkupDiagnostic> diagnostics, string className, string ns,
            string? styleScope, string? scopedCss, string? globalCss)
        {
            Code = code;
            Diagnostics = diagnostics;
            ClassName = className;
            Namespace = ns;
            StyleScope = styleScope;
            ScopedCss = scopedCss;
            GlobalCss = globalCss;
        }

        public string Code { get; }
        public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }
        public string ClassName { get; }
        public string Namespace { get; }
        public string? StyleScope { get; }
        public string? ScopedCss { get; }
        public string? GlobalCss { get; }
    }

    /// <summary>值的 C# 文本，以及（可精确映射时）它在源中的偏移。</summary>
    readonly struct ValueCode
    {
        public ValueCode(string text, int offset)
        {
            Text = text;
            Offset = offset;
        }

        public string Text { get; }

        /// <summary>源中的偏移；值由多段拼成、无法逐列对齐时为 -1。</summary>
        public int Offset { get; }
    }

    sealed class Emitter
    {
        const int MaxPadding = 240;

        readonly MarkupAnalysis _analysis;
        readonly CSharpEmitOptions _options;
        readonly SourceText _source;
        readonly StringBuilder _sb = new();
        readonly DiagnosticBag _diagnostics;
        readonly string _linePath;
        readonly bool _lines;
        readonly string _file;
        readonly ImportScope _scope;
        HashSet<string> _stateTargets = new(StringComparer.Ordinal);
        readonly List<string> _svgFields = new();
        int _indent;
        int _counter;
        bool _scoped;

        public Emitter(MarkupAnalysis analysis, CSharpEmitOptions options)
        {
            _analysis = analysis;
            _options = options;
            _source = analysis.Tree.Source;
            _diagnostics = new DiagnosticBag(_source);
            _file = _source.FilePath ?? ((options.ClassName ?? "Component") + ".awaui");
            _linePath = options.LineDirectivePath ?? _file;
            _lines = options.EmitLineDirectives && CanWriteLinePath(_linePath);
            var usings = new List<string>();
            string? ns = null;
            foreach (var item in analysis.NormalizedRoot.Items)
            {
                if (item is DirectiveSyntax d && d.DirectiveKind == DirectiveKind.Using && d.Value.IndexOf('=') < 0 && !d.Value.StartsWith("static ", StringComparison.Ordinal))
                    usings.Add(d.Value);
                if (item is DirectiveSyntax n && n.DirectiveKind == DirectiveKind.Namespace) ns = n.Value;
            }
            usings.AddRange(DefaultUsings);
            _scope = new ImportScope(ns ?? options.Namespace, usings);
        }

        public EmitResult Run()
        {
            var root = _analysis.NormalizedRoot;
            var directives = new List<DirectiveSyntax>();
            var codeBlocks = new List<CodeBlockSyntax>();
            var markup = new List<SyntaxNode>();
            var scopedStyles = new List<StyleBlockSyntax>();
            var globalStyles = new List<StyleBlockSyntax>();
            foreach (var item in root.Items)
            {
                switch (item)
                {
                    case DirectiveSyntax d: directives.Add(d); break;
                    case CodeBlockSyntax c: codeBlocks.Add(c); break;
                    case StyleBlockSyntax s:
                        (s.IsGlobal ? globalStyles : scopedStyles).Add(s);
                        break;
                    case CommentSyntax: break;
                    default: markup.Add(item); break;
                }
            }

            string className = _options.ClassName ?? MarkupValidator.ClassNameFromPath(_file);
            string ns = _scope.CurrentNamespace ?? _options.Namespace;
            string baseType = DefaultBase;
            var interfaces = new List<string>();
            var typeParams = new List<string>();
            var constraints = new List<string>();
            var attributes = new List<DirectiveSyntax>();
            bool hasInherits = false;
            foreach (var d in directives)
            {
                switch (d.DirectiveKind)
                {
                    case DirectiveKind.Inherits:
                        baseType = d.Value;
                        hasInherits = true;
                        break;
                    case DirectiveKind.Implements: interfaces.Add(d.Value); break;
                    case DirectiveKind.Attribute: attributes.Add(d); break;
                    case DirectiveKind.TypeParam:
                    {
                        int i = 0;
                        while (i < d.Value.Length && Parsing.MarkupParser.IsIdentPart(d.Value[i])) i++;
                        typeParams.Add(d.Value.Substring(0, i));
                        string rest = d.Value.Substring(i).Trim();
                        if (rest.Length > 0) constraints.Add(rest);
                        break;
                    }
                }
            }

            // 样式
            string? styleScope = null, scopedCss = null, globalCss = null;
            if (scopedStyles.Count > 0)
            {
                _scoped = true;
                styleScope = ScopedCss.ScopeAttributeFor(ns + "." + className);
                var css = new StringBuilder();
                foreach (var s in scopedStyles)
                {
                    if (css.Length > 0) css.Append('\n');
                    css.Append(ScopedCss.Rewrite(s.Content, styleScope));
                }
                scopedCss = css.ToString();
            }
            if (globalStyles.Count > 0)
            {
                var css = new StringBuilder();
                foreach (var s in globalStyles)
                {
                    if (css.Length > 0) css.Append('\n');
                    css.Append(s.Content);
                }
                globalCss = css.ToString();
            }

            // Props 胶水与 @bind 目标
            var codeTexts = new List<string>();
            foreach (var c in codeBlocks) codeTexts.Add(c.Code);
            var classInfo = _options.ClassInfo ?? SyntacticClassInfo(codeTexts, hasInherits && !IsComponentBase(baseType));
            foreach (var t in classInfo.StateBindTargets) _stateTargets.Add(t);

            Line("// <auto-generated/>");
            Line("#pragma warning disable 1591");
            if (_options.EmitNullableRestore) Line("#nullable restore");
            Line("namespace " + ns);
            Line("{");
            _indent++;
            foreach (var u in DefaultUsings) Line("using global::" + u + ";");
            foreach (var d in directives)
            {
                if (d.DirectiveKind != DirectiveKind.Using) continue;
                Mapped("using", d.Value, d.ValueSpan.Start, ";");
            }
            string sourcePath = (_options.ComponentSourcePath ?? _file).Replace('\\', '/');
            Line($"[{UI}ComponentSource({Literal(sourcePath)})]");
            foreach (var a in attributes) Mapped("", a.Value, a.ValueSpan.Start, "");
            var header = new StringBuilder("public partial class ").Append(className);
            string typeParamList = typeParams.Count > 0 ? "<" + string.Join(", ", typeParams) + ">" : "";
            header.Append(typeParamList);
            header.Append(" : ").Append(baseType);
            foreach (var i in interfaces) header.Append(", ").Append(i);
            Line(header.ToString());
            foreach (var c in constraints) Line("    " + c);
            Line("{");
            _indent++;
            Hidden();
            Line("protected override " + UI + "Node Build()");
            Line("{");
            _indent++;
            if (markup.Count == 1 && markup[0] is ElementSyntax single)
            {
                string? v = EmitElement(single, null);
                Line(v != null ? $"return {v};" : $"return new {UI}Fragment();");
            }
            else
            {
                string list = Next("__r");
                Line($"var {list} = new {UI}NodeList();");
                EmitNodes(markup, list);
                Line($"return new {UI}Fragment({list});");
            }
            _indent--;
            Line("}");
            foreach (var c in codeBlocks)
            {
                Line("");
                MappedRaw(c.Code.TrimEnd(), c.CodeSpan.Start);
            }

            if (_options.EmitPropsGlue)
            {
                var glue = GlueModel(className, typeParamList, classInfo);
                if (glue != null)
                {
                    Line("");
                    // 共享的胶水代码按 C# 7.3 书写（如 (object)__o == null），不参与可空分析，以免在可空项目中产生警告
                    if (_options.EmitNullableRestore) Raw("#nullable disable");
                    ComponentGlueEmitter.EmitMembers(glue, _sb, new string(' ', _indent * 4));
                    if (_options.EmitNullableRestore) Raw("#nullable restore");
                }
            }

            if (styleScope != null || globalCss != null)
            {
                // 派生自另一个带样式的组件（@inherits）时，这些静态常量有意隐藏基类的同名常量：不报 CS0108
                Line("");
                Raw("#pragma warning disable 0108");
            }
            if (styleScope != null)
            {
                Line("/// <summary>作用域样式标记：本组件 <c>Build()</c> 产出的元素带有这个空值属性，<see cref=\"StyleSheetSource\"/> 的选择器以它限定。</summary>");
                Line("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                Line($"public const string __StyleScope = {Literal(styleScope)};");
                Line("");
                Line("/// <summary>本组件 <c>&lt;style&gt;</c> 的样式表文本（选择器已附加作用域属性选择器），由运行时注册。</summary>");
                Line($"public const string StyleSheetSource = {Literal(scopedCss!)};");
            }
            if (globalCss != null)
            {
                if (styleScope != null) Line("");
                Line("/// <summary>本组件 <c>&lt;style global&gt;</c> 的样式表文本（不限定作用域），由运行时注册。</summary>");
                Line($"public const string GlobalStyleSheetSource = {Literal(globalCss)};");
            }
            if (styleScope != null || globalCss != null)
            {
                // 运行时在该组件类型首次挂载时读取这两个钩子并注册样式表（Component.ScopedStyleSheet / GlobalStyleSheet）。
                // 返回类型写成 string（基类为 string?）：C# 7.3 与未启用可空的项目也能编译，可空项目中协变的返回可空性不报警告
                Line("");
                if (styleScope != null) Line("protected override string ScopedStyleSheet => StyleSheetSource;");
                if (globalCss != null) Line("protected override string GlobalStyleSheet => GlobalStyleSheetSource;");
                Raw("#pragma warning restore 0108");
            }
            if (_svgFields.Count > 0)
            {
                // 内联 svg 的文档：首次构建时解析，之后共享（文档不可变）。字段在设置前为 null，不参与可空分析
                Line("");
                if (_options.EmitNullableRestore) Raw("#nullable disable");
                foreach (var f in _svgFields) Line($"private static global::AwAUI.Svg.SvgDocument {f};");
                if (_options.EmitNullableRestore) Raw("#nullable restore");
            }
            _indent--;
            Line("}");
            _indent--;
            Line("}");
            if (_lines) Raw("#line default");
            return new EmitResult(_sb.ToString(), _diagnostics.Items, className, ns, styleScope, scopedCss, globalCss);
        }

        /// <summary>
        /// 路径能否写进 <c>#line</c>：其文件名部分不支持转义，遇到 <c>"</c> 或换行类字符即结束，
        /// 含这些字符的路径（只可能出现在类 Unix 系统上）无法表示，此时不输出 <c>#line</c>（诊断落在生成的文件上，而不是一个不存在的路径）。
        /// </summary>
        internal static bool CanWriteLinePath(string path)
        {
            foreach (char c in path)
                if (c == '"' || c == '\n' || c == '\r' || c == '\u0085' || c == '\u2028' || c == '\u2029') return false;
            return true;
        }

        static bool IsComponentBase(string type)
        {
            string t = type.Trim();
            return t == DefaultBase || t == "AwAUI.UI.Component" || t == "Component";
        }

        ComponentClassInfo SyntacticClassInfo(List<string> codeTexts, bool assumeBaseGlue)
        {
            var members = CodeMembers.Scan(codeTexts);
            var targets = new List<string>();
            foreach (var b in CollectBindTargets(_analysis.NormalizedRoot))
            {
                string name = b;
                if (name.StartsWith("this.", StringComparison.Ordinal)) name = name.Substring(5).Trim();
                if (members.StateMembers.Contains(name)) targets.Add(b);
            }
            return new ComponentClassInfo(members.Props, assumeBaseGlue, members.DeclaresPropsEqual, members.DeclaresAdoptProps,
                members.DeclaresCanAdoptProps, targets);
        }

        static ComponentGlueModel? GlueModel(string className, string typeParams, ComponentClassInfo info)
        {
            var needed = GlueMembers.PropsEqual | GlueMembers.AdoptProps;
            var props = new List<GlueProp>(info.Props.Count);
            foreach (var p in info.Props)
            {
                props.Add(new GlueProp(p.Name, p.TypeName, p.IsInitOnly));
                if (p.IsInitOnly) needed |= GlueMembers.CanAdoptProps;
            }
            var declared = GlueMembers.None;
            if (info.DeclaresPropsEqual) declared |= GlueMembers.PropsEqual;
            if (info.DeclaresAdoptProps) declared |= GlueMembers.AdoptProps;
            if (info.DeclaresCanAdoptProps) declared |= GlueMembers.CanAdoptProps;
            var members = needed & ~declared;
            if (members == GlueMembers.None) return null;
            return new ComponentGlueModel(null, Array.Empty<GlueContainingType>(), className, typeParams, props, info.BaseHasGlue, members);
        }

        // ------------------------------------------------------------ 节点

        void EmitNodes(IReadOnlyList<SyntaxNode> nodes, string target)
        {
            foreach (var n in nodes) EmitNode(n, target);
        }

        void EmitNode(SyntaxNode n, string target)
        {
            switch (n)
            {
                case TextSyntax t:
                    Line($"{target}.Add({Literal(t.Value)});");
                    break;
                case ExpressionSyntax e:
                    Mapped($"{target}.Add({UI}Node.From(", e.Code, e.CodeSpan.Start, "));");
                    break;
                case ElementSyntax el:
                    EmitElement(el, target);
                    break;
                case FragmentSyntax f:
                {
                    string list = Next("__f");
                    Line($"var {list} = new {UI}NodeList();");
                    EmitNodes(f.Children, list);
                    Line($"{target}.Add(new {UI}Fragment({list}));");
                    break;
                }
                case IfSyntax i:
                    foreach (var b in i.Branches)
                    {
                        if (b.BranchKind == IfBranchKind.Else)
                            Line("else");
                        else
                            Mapped(b.BranchKind == IfBranchKind.If ? "if (" : "else if (", b.Condition ?? "", b.ConditionSpan.Start, ")");
                        EmitBlock(b.Body.Nodes, target);
                    }
                    break;
                case LoopSyntax l:
                    Mapped(l.Keyword + " (", l.Header, l.HeaderSpan.Start, ")");
                    EmitBlock(l.Body.Nodes, target);
                    break;
                case SwitchSyntax s:
                    Mapped("switch (", s.Expression, s.ExpressionSpan.Start, ")");
                    Line("{");
                    _indent++;
                    foreach (var sec in s.Sections)
                    {
                        foreach (var label in sec.Labels)
                        {
                            string text = label.Text;
                            int lead = 0;
                            while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;
                            Mapped("", text.Trim(), label.Span.Start + lead, "");
                        }
                        _indent++;
                        EmitNodes(sec.Nodes, target);
                        _indent--;
                    }
                    _indent--;
                    Line("}");
                    break;
                case StatementBlockSyntax sb:
                    // 与 Razor 一致：@{ } 中声明的局部变量在其后的标记中可见，因此不包额外的花括号
                    EmitNodes(sb.Body.Nodes, target);
                    break;
                case LineTextSyntax lt:
                    EmitNodes(lt.Children, target);
                    break;
                case CSharpCodeSyntax cs:
                    // 原样输出（含开头的换行），使 #line 的行号与源对齐
                    MappedRaw(cs.Code.EndsWith("\n", StringComparison.Ordinal) ? cs.Code.Substring(0, cs.Code.Length - 1) : cs.Code, cs.Span.Start);
                    break;
                case CommentSyntax:
                case DirectiveSyntax:
                case CodeBlockSyntax:
                case StyleBlockSyntax:
                    break;
            }
        }

        void EmitBlock(IReadOnlyList<SyntaxNode> nodes, string target)
        {
            Line("{");
            _indent++;
            EmitNodes(nodes, target);
            _indent--;
            Line("}");
        }

        /// <summary>
        /// 生成一个元素或组件的构建代码；<paramref name="target"/> 为 null 时不加入父列表（根元素）。
        /// 返回变量名；元素不受支持而被跳过时返回 null。
        /// </summary>
        string? EmitElement(ElementSyntax e, string? target)
        {
            string? v = e.ElementKind == ElementKind.Intrinsic ? EmitIntrinsic(e) : EmitComponent(e);
            if (v != null && target != null) Line($"{target}.Add({v});");
            return v;
        }

        // ------------------------------------------------------------ 内建元素

        string? EmitIntrinsic(ElementSyntax e)
        {
            string? type = RuntimeElements.TypeOf(e.Name);
            if (type == null)
            {
                if (IntrinsicElements.IsIntrinsic(e.Name))
                    _diagnostics.Error(DiagnosticCodes.NotSupportedByRuntime, e.NameSpan,
                        $"内建元素 <{e.Name}> 尚未被运行时（AwAUI.UI）实现，该元素及其内容不会生成");
                // 未知的内建元素已由 AW4002 报告：跳过整个子树，避免生成无法编译的 C#
                Line($"// <{e.Name}>：不受支持，已跳过");
                return null;
            }
            string v = Next("__n");
            Line($"var {v} = new {type}();");
            if (e.Name == "svg") return EmitInlineSvg(e, v);

            AttributeSyntax? bindEvent = e.FindAttribute("@bind:event");
            foreach (var a in e.Attributes)
            {
                if (IsSplat(a)) continue;
                if (a.IsDirective) EmitDirectiveAttribute(a, v, e.Name, null, bindEvent);
                else EmitIntrinsicAttribute(e.Name, a, v);
            }
            EmitElementSplats(e, v);
            if (_scoped) Line($"{v}.__Scope = __StyleScope;");
            EmitChildren(e, v, e.Children);
            return v;
        }

        // ------------------------------------------------------------ 内联 svg

        /// <summary>
        /// 内联 <c>&lt;svg&gt;…&lt;/svg&gt;</c> → <c>AwAUI.UI.Svg</c>：宿主属性（<see cref="RuntimeElements.IsSvgHostAttribute"/>）与指令属性
        /// 照常落在节点上；其余根属性与整个子树在编译期序列化为 SVG 文本，每个内联 svg 生成一个静态字段，第一次构建时
        /// <c>SvgDocument.Parse</c> 一次，之后各实例共享同一个不可变文档。
        /// 运行时没有动态 SVG：子树中的表达式、控制流、组件、指令属性与含表达式的属性值报 AW4012 错误，此时不设置文档。
        /// 子树中的 <c>&lt;style&gt;</c> 属于文档（由 AwAUI.Svg 应用于文档内部的元素），注释不输出。元素名与属性名按 HTML 解析器对 SVG
        /// 外来内容的规则修正大小写（<c>viewbox</c> → <c>viewBox</c>、<c>lineargradient</c> → <c>linearGradient</c>，见 <see cref="SvgNames"/>）。
        /// 文档用 <c>ImagingSvgImageResolver.DefaultOptions</c> 解析（<c>&lt;image&gt;</c> 的 data: 位图可以显示）。
        /// 需要动态文档时用 <c>&lt;Svg Document="@doc" /&gt;</c>。
        /// </summary>
        string EmitInlineSvg(ElementSyntax e, string v)
        {
            AttributeSyntax? bindEvent = e.FindAttribute("@bind:event");
            var xml = new StringBuilder("<svg");
            bool ok = true;
            foreach (var a in e.Attributes)
            {
                if (IsSplat(a)) continue;
                if (a.IsDirective) EmitDirectiveAttribute(a, v, e.Name, null, bindEvent);
                else if (RuntimeElements.IsSvgHostAttribute(a.Name)) EmitIntrinsicAttribute(e.Name, a, v);
                else ok &= AppendSvgAttribute(xml, a);
            }
            EmitElementSplats(e, v);
            if (_scoped) Line($"{v}.__Scope = __StyleScope;");
            ok &= AppendSvgContent(xml, "svg", e.Children);
            if (!ok) return v;
            string field = "__svg" + _svgFields.Count.ToString(CultureInfo.InvariantCulture);
            _svgFields.Add(field);
            Map(e.NameSpan.Start);
            // 与 <img src=svg> 相同的解析选项：<image> 的 data: 位图经 AwAUI.Imaging 解码（UI 引用 Imaging，生成的代码只经由 UI 访问它）
            Line($"{v}.Document = {field} ?? ({field} = global::AwAUI.Svg.SvgDocument.Parse({Literal(xml.ToString())}, global::AwAUI.UI.ImagingSvgImageResolver.DefaultOptions));");
            Hidden();
            return v;
        }

        /// <summary>追加 <c> name="value"</c>（值按 XML 转义）；值含表达式时报 AW4012 并返回 false。</summary>
        bool AppendSvgAttribute(StringBuilder xml, AttributeSyntax a)
        {
            string? value = a.Value == null ? "" : a.Value.LiteralValue;
            if (value == null)
            {
                _diagnostics.Error(DiagnosticCodes.NotSupportedByRuntime, a.Value!.Span,
                    $"内联 <svg> 中的属性 {a.Name} 含表达式：SVG 在编译期序列化为静态文档，运行时不支持动态 SVG（动态内容请改用 <Svg Document=\"@doc\" />）");
                return false;
            }
            xml.Append(' ').Append(SvgNames.Attribute(a.Name)).Append("=\"");
            AppendXmlEscaped(xml, value, attribute: true);
            xml.Append('"');
            return true;
        }

        /// <summary>序列化子节点并闭合 <paramref name="name"/>；遇到动态内容报 AW4012 并返回 false（继续检查以报告全部问题）。</summary>
        bool AppendSvgContent(StringBuilder xml, string name, IReadOnlyList<SyntaxNode> children)
        {
            bool ok = true;
            bool any = false;
            foreach (var c in children)
            {
                switch (c)
                {
                    case CommentSyntax:
                        break;
                    case TextSyntax t:
                        if (t.Value.Length == 0) break;
                        if (!any) xml.Append('>');
                        any = true;
                        AppendXmlEscaped(xml, t.Value, attribute: false);
                        break;
                    case ElementSyntax el when el.ElementKind == ElementKind.Intrinsic:
                    {
                        if (!any) xml.Append('>');
                        any = true;
                        string svgName = SvgNames.Element(el.Name);
                        xml.Append('<').Append(svgName);
                        foreach (var a in el.Attributes)
                        {
                            if (a.IsDirective)
                            {
                                _diagnostics.Error(DiagnosticCodes.NotSupportedByRuntime, a.NameSpan,
                                    $"内联 <svg> 的子元素 <{el.Name}> 上不支持指令属性 {a.FullName}：SVG 内部元素不是运行时元素，没有事件、@key 或 @ref");
                                ok = false;
                                continue;
                            }
                            ok &= AppendSvgAttribute(xml, a);
                        }
                        ok &= AppendSvgContent(xml, svgName, el.Children);
                        break;
                    }
                    case StyleBlockSyntax st when st.IsSvg:
                    {
                        // 文档内部的 <style>（AwAUI.Svg 应用于文档中的元素）；内容按 XML 文本转义
                        if (!any) xml.Append('>');
                        any = true;
                        xml.Append("<style");
                        foreach (var a in st.Attributes)
                            if (!a.IsDirective && a.Name != "global") ok &= AppendSvgAttribute(xml, a);
                        xml.Append('>');
                        AppendXmlEscaped(xml, st.Content, attribute: false);
                        xml.Append("</style>");
                        break;
                    }
                    default:
                    {
                        string what = c is ElementSyntax comp ? $"组件 <{comp.Name}>" : c is ExpressionSyntax ? "表达式" : "控制流或代码块";
                        _diagnostics.Error(DiagnosticCodes.NotSupportedByRuntime, c is ElementSyntax ce ? ce.NameSpan : c.Span,
                            $"内联 <svg> 中不能使用{what}：SVG 在编译期序列化为静态文档（动态内容请改用 <Svg Document=\"@doc\" />）");
                        ok = false;
                        break;
                    }
                }
            }
            if (any) xml.Append("</").Append(name).Append('>');
            else xml.Append("/>");
            return ok;
        }

        static void AppendXmlEscaped(StringBuilder xml, string text, bool attribute)
        {
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '&': xml.Append("&amp;"); break;
                    case '<': xml.Append("&lt;"); break;
                    case '>': xml.Append("&gt;"); break;
                    case '"' when attribute: xml.Append("&quot;"); break;
                    case '\n' when attribute: xml.Append("&#10;"); break;
                    case '\r' when attribute: xml.Append("&#13;"); break;
                    case '\t' when attribute: xml.Append("&#9;"); break;
                    default: xml.Append(ch); break;
                }
            }
        }

        void EmitIntrinsicAttribute(string element, AttributeSyntax a, string v)
        {
            var attr = RuntimeElements.Attribute(element, a.Name);
            if (attr == null) return; // 未知属性：AW4003 已报告
            EmitRuntimeAttribute(element, a, attr, v);
        }

        void EmitRuntimeAttribute(string element, AttributeSyntax a, RuntimeAttr attr, string v)
        {
            var value = a.Value;
            switch (attr.Kind)
            {
                case RuntimeAttrKind.PassThrough:
                {
                    if (attr.Unsupported)
                        _diagnostics.Warning(DiagnosticCodes.NotSupportedByRuntime, a.NameSpan,
                            $"属性 {a.Name} 的行为尚未被运行时（AwAUI.UI）实现，只作为自定义属性写入（CSS 属性选择器可见）");
                    IntrinsicElements.TryGetAttribute(element, a.Name, out var grammar);
                    string key = $"{v}.Attributes[{Literal(a.Name)}] = ";
                    if (grammar?.Type == AttributeValueType.Boolean)
                    {
                        // HTML 式布尔属性：存在即为真（空串），false 时不写
                        string? literal = value?.LiteralValue?.Trim();
                        if (value == null || literal == "true" || literal == "" || literal == a.Name)
                            AttrLine(a, key + "\"\";");
                        else if (literal == "false")
                            break;
                        else
                        {
                            var code = CodeValue(value);
                            Mapped(key + "(", code.Text, code.Offset, ") ? \"\" : null;", a.Span.Start);
                        }
                    }
                    else if (value == null)
                        AttrLine(a, key + "\"\";");
                    else if (value.LiteralValue is { } lit)
                        AttrLine(a, key + Literal(lit) + ";");
                    else if (grammar is { Type: AttributeValueType.Enum } && IsTrueFalseEnum(grammar) && SingleExpression(value) is { } boolish)
                    {
                        // 取值为 true / false 的枚举属性（draggable）：表达式可以是 bool，按 HTML 关键字写成小写的 "true" / "false"
                        Mapped(key + "global::System.Convert.ToString((object)(", boolish.Code, boolish.CodeSpan.Start,
                            "), global::System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();", a.Span.Start);
                    }
                    else
                        AttrLine(a, key + StringValue(value) + ";");
                    break;
                }
                case RuntimeAttrKind.String:
                case RuntimeAttrKind.Class:
                    if (value == null) AttrLine(a, $"{v}.{attr.Member} = \"\";");
                    else EmitStringProp(a, value, v + "." + attr.Member);
                    break;
                case RuntimeAttrKind.Int:
                {
                    if (value?.LiteralValue is { } lit)
                    {
                        AttrLine(a, int.TryParse(lit.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)
                            ? $"{v}.{attr.Member} = {n.ToString(CultureInfo.InvariantCulture)};"
                            : $"{v}.{attr.Member} = 0;"); // AW4004 已报告
                    }
                    else if (value != null)
                    {
                        var code = CodeValue(value);
                        Mapped($"{v}.{attr.Member} = ", code.Text, code.Offset, ";", a.Span.Start);
                    }
                    break;
                }
                case RuntimeAttrKind.Number:
                {
                    if (value?.LiteralValue is { } lit)
                    {
                        // 无法转换的字面量已由 AW4004 报告
                        if (double.TryParse(lit.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
                            AttrLine(a, $"{v}.{attr.Member} = {FloatLiteral(d)};");
                    }
                    else if (value != null)
                    {
                        var code = CodeValue(value);
                        Mapped($"{v}.{attr.Member} = ", code.Text, code.Offset, ";", a.Span.Start);
                    }
                    break;
                }
                case RuntimeAttrKind.Bool:
                {
                    string? lit = value?.LiteralValue?.Trim();
                    if (value == null || lit == "" || lit == "true" || lit == a.Name)
                        AttrLine(a, $"{v}.{attr.Member} = true;");
                    else if (lit == "false")
                        AttrLine(a, $"{v}.{attr.Member} = false;");
                    else if (lit == null)
                    {
                        var code = CodeValue(value!);
                        Mapped($"{v}.{attr.Member} = ", code.Text, code.Offset, ";", a.Span.Start);
                    }
                    break; // 其他字面量：AW4004 已报告
                }
                case RuntimeAttrKind.Role:
                {
                    if (value?.LiteralValue is { } lit)
                    {
                        string? member = RuntimeElements.RoleMember(lit);
                        if (member == null)
                        {
                            string? near = MarkupValidator.Nearest(lit.Trim(), RuntimeElements.RoleNames);
                            _diagnostics.Error(DiagnosticCodes.InvalidAttributeValue, value.Span,
                                $"未知的无障碍角色 \"{lit}\"" + (near != null ? $"，是否是 \"{near}\"？" : ""));
                            break;
                        }
                        AttrLine(a, $"{v}.Role = {UI}Role.{member};");
                    }
                    else if (value != null)
                    {
                        var code = CodeValue(value);
                        Mapped($"{v}.Role = ", code.Text, code.Offset, ";", a.Span.Start);
                    }
                    break;
                }
            }
        }

        /// <summary><c>float</c> 常量的 C# 写法；超出 <c>float</c> 范围时钳制到最大有限值（字面量本身会是编译错误）。</summary>
        static string FloatLiteral(double d)
        {
            float f = (float)Math.Max(-float.MaxValue, Math.Min(float.MaxValue, d));
            return f.ToString("R", CultureInfo.InvariantCulture) + "f";
        }

        /// <summary>字符串类属性：单一表达式直接赋值（类型交给 C# 检查），字面量为字符串字面量，混合时拼接。</summary>
        void EmitStringProp(AttributeSyntax a, AttributeValueSyntax value, string target)
        {
            if (value.LiteralValue is { } lit)
            {
                AttrLine(a, $"{target} = {Literal(lit)};");
            }
            else if (SingleExpression(value) is { } e)
            {
                Mapped(target + " = ", e.Code, e.CodeSpan.Start, ";");
            }
            else
            {
                AttrLine(a, $"{target} = {StringValue(value)};");
            }
        }

        // ------------------------------------------------------------ 组件

        string? EmitComponent(ElementSyntax e)
        {
            ComponentInfo? info = _options.Catalog?.Resolve(CatalogKey(e), _scope);
            string type = (info != null ? "global::" + info.FullName : e.Name) + (e.TypeArguments != null ? "<" + e.TypeArguments + ">" : "");
            string v = Next("__n");
            Mapped($"var {v} = new", " " + type + "()", e.NameSpan.Start - 1, ";");

            AttributeSyntax? bindEvent = e.FindAttribute("@bind:event");
            bool element = info != null && info.IsElement;
            string? capture = info?.CaptureUnmatchedProp;
            string? captured = null; // 收集未匹配属性的 AttributeDictionary 变量
            if (capture != null)
            {
                // 先写入全部展开值，再写显式属性：与元素上的展开相同，显式属性优先（与位置无关），class / style 拼接
                foreach (var a in e.Attributes)
                {
                    if (IsSplat(a)) TryCapture(a, v, info!, ref captured);
                    else if (IsCaptureProp(a, capture) && a.Value != null && a.Value.Parts.Count > 0)
                    {
                        // 显式传入捕获 Prop 本身（包装组件转发 Rest="@Rest"）：与展开值相同，先写入，否则最后整体赋值时会被覆盖
                        var forwarded = CodeValue(a.Value);
                        EnsureCaptured(v, ref captured);
                        Mapped($"{captured}.AddRange(((", forwarded.Text, forwarded.Offset, "))" + SplatSuppression + ");", a.Span.Start);
                    }
                }
            }
            foreach (var a in e.Attributes)
            {
                if (element && IsSplat(a)) continue; // 元素描述类型：写完显式属性之后再展开
                if (capture != null && (IsSplat(a) || (IsCaptureProp(a, capture) && a.Value != null && a.Value.Parts.Count > 0))) continue; // 已在上面写入
                if (capture != null && TryCapture(a, v, info!, ref captured)) continue;
                if (a.IsDirective)
                {
                    EmitDirectiveAttribute(a, v, null, info, bindEvent);
                    continue;
                }
                EmitComponentAttribute(a, v, info);
            }
            if (captured != null) Line($"{v}.{capture} = {captured};");
            if (element) EmitElementSplats(e, v);
            if (_scoped && element) Line($"{v}.__Scope = __StyleScope;");

            // 具名插槽：子标签名与组件的 Slot / Template 属性同名
            var rest = new List<SyntaxNode>();
            foreach (var c in e.Children)
            {
                PropInfo? slot = info != null && c is ElementSyntax ce && ce.ElementKind == ElementKind.Component ? info.FindProp(ce.Name) : null;
                if (slot != null && (slot.Kind == PropKind.Slot || slot.Kind == PropKind.Template))
                    EmitSlot((ElementSyntax)c, slot, v);
                else
                    rest.Add(c);
            }
            EmitChildren(e, v, rest);
            return v;
        }

        static bool IsSplat(AttributeSyntax a) => a.IsDirective && a.Name == "attributes" && a.Parameter == null;

        /// <summary>
        /// 展开值的实参后缀：<c>Dictionary&lt;string, object&gt;</c> 与参数类型 <c>IEnumerable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> 只差嵌套的可空注解，
        /// 开启可空时会报 CS8620；用 <c>!</c> 抑制（C# 8 以下没有该运算符，也没有这条警告）。表达式包两层括号：<c>(x)!</c> 会被解析为把 <c>!...</c> 转换为类型 <c>x</c>。
        /// </summary>
        string SplatSuppression => _options.EmitNullableRestore ? "!" : "";

        /// <summary>标签上按名字写出的捕获 Prop 本身（如 <c>Rest="@Rest"</c>）。</summary>
        static bool IsCaptureProp(AttributeSyntax a, string capture) =>
            !a.IsDirective && a.Name.Length > 0 && (char.IsLower(a.Name[0]) ? PropertyName(a.Name) : a.Name) == capture;

        /// <summary>
        /// 元素（内建元素与解析为元素描述类型的大写标签）上的 <c>@attributes="..."</c>：在全部显式属性写完之后调用
        /// <c>ElementNode.MergeAttributes</c>，因此显式属性优先、<c>class</c> / <c>style</c> 拼接（规则见运行时的文档）。
        /// </summary>
        void EmitElementSplats(ElementSyntax e, string v)
        {
            foreach (var a in e.Attributes)
            {
                if (!IsSplat(a) || a.Value == null || a.Value.Parts.Count == 0) continue; // 缺值：AW4004 已报告
                var code = CodeValue(a.Value);
                Mapped($"{v}.MergeAttributes(((", code.Text, code.Offset, "))" + SplatSuppression + ");", a.Span.Start);
            }
        }

        /// <summary>
        /// 组件有捕获未匹配属性的 Prop（<c>[CaptureUnmatchedAttributes]</c>）时：<c>@attributes</c> 的展开值先按源中的顺序写入一个 <c>AttributeDictionary</c>
        /// （<c>AddRange</c>，同名后写覆盖），再写入小写开头且没有对应 Prop 的普通属性与没有对应 Prop 的 <c>@onxxx</c>（<c>Merge</c>：覆盖展开值，
        /// <c>class</c> / <c>style</c> 接在展开值之后），最后整体赋给该 Prop。返回 true 表示已处理。
        /// </summary>
        bool TryCapture(AttributeSyntax a, string v, ComponentInfo info, ref string? captured)
        {
            string key;
            if (a.IsDirective)
            {
                if (IsSplat(a))
                {
                    if (a.Value == null || a.Value.Parts.Count == 0) return true; // AW4004 已报告
                    var splat = CodeValue(a.Value);
                    EnsureCaptured(v, ref captured);
                    Mapped($"{captured}.AddRange(((", splat.Text, splat.Offset, "))" + SplatSuppression + ");", a.Span.Start);
                    return true;
                }
                if (a.Parameter != null || !a.Name.StartsWith("on", StringComparison.Ordinal) || a.Name.Length <= 2) return false;
                string evt = a.Name.Substring(2);
                var known = RuntimeElements.AnyEvent(evt);
                if (info.FindProp(known?.Member ?? "On" + Pascal(evt)) != null) return false;
                if (a.Value == null || a.Value.Parts.Count == 0) return true; // AW4004 已报告
                var handler = CodeValue(a.Value);
                EnsureCaptured(v, ref captured);
                key = Literal(a.Name);
                if (known != null)
                    Mapped($"{captured}.Merge({key}, {UI}EventHandlers.Wrap<{UI}{known.ArgsType}>(", handler.Text, handler.Offset, "));", a.Span.Start);
                else
                    Mapped($"{captured}.Merge({key}, ", handler.Text, handler.Offset, ");", a.Span.Start);
                return true;
            }
            // 大写开头的属性名按 Prop 处理：拼错的 Prop 名由 C# 编译报告，而不是被悄悄收集
            if (a.Name.Length == 0 || !char.IsLower(a.Name[0])) return false;
            if (info.FindProp(PropertyName(a.Name)) != null) return false;
            EnsureCaptured(v, ref captured);
            key = Literal(a.Name);
            var value = a.Value;
            if (value == null)
                AttrLine(a, $"{captured}.Merge({key}, true);"); // HTML 布尔写法
            else if (value.LiteralValue is { } lit)
                AttrLine(a, $"{captured}.Merge({key}, {Literal(lit)});");
            else if (SingleExpression(value) is { } x)
                Mapped($"{captured}.Merge({key}, (", x.Code, x.CodeSpan.Start, "));");
            else
                AttrLine(a, $"{captured}.Merge({key}, {StringValue(value)});");
            return true;
        }

        void EnsureCaptured(string v, ref string? captured)
        {
            if (captured != null) return;
            captured = v + "_attrs";
            Line($"var {captured} = new {UI}AttributeDictionary();");
        }

        void EmitComponentAttribute(AttributeSyntax a, string v, ComponentInfo? info)
        {
            bool lower = a.Name.Length > 0 && char.IsLower(a.Name[0]);
            if (info != null && info.IsElement && lower && RuntimeElements.Attribute("", a.Name) is { } runtime)
            {
                // 大写标签解析到的元素描述类型（<Button> → AwAUI.UI.Button）：HTML 式的通用属性按内建元素处理
                EmitRuntimeAttribute("div", a, runtime, v);
                return;
            }
            string member = lower ? PropertyName(a.Name) : a.Name;
            var prop = info?.FindProp(member);
            string target = v + "." + member;
            if (a.Value == null)
            {
                AttrLine(a, $"{target} = true;");
                return;
            }
            // 没有目录信息时按命名猜测：小写名称、或字面量与表达式混合的值（"Hi @name"）按字符串
            bool isString = prop != null ? prop.Kind == PropKind.String : lower || IsMixedText(a.Value);
            if (isString)
            {
                EmitStringProp(a, a.Value, target);
                return;
            }
            var code = CodeValue(a.Value);
            Mapped(target + " = ", code.Text, code.Offset, ";", a.Span.Start);
        }

        void EmitSlot(ElementSyntax slotElement, PropInfo slot, string owner)
        {
            if (slot.Kind == PropKind.Slot)
            {
                string list = Next("__s");
                Line($"var {list} = new {UI}NodeList();");
                EmitNodes(slotElement.Children, list);
                Line($"{owner}.{slot.Name} = {list};");
                return;
            }
            string context = "context";
            if (slotElement.FindAttribute("Context") is { } ctx)
            {
                // 参数名原样写进 lambda：必须是合法的 C# 标识符，否则生成的代码无法解析
                string? name = ctx.Value?.LiteralText?.Trim();
                if (name != null && MarkupValidator.IsValidClassName(name))
                    context = name;
                else
                    _diagnostics.Error(DiagnosticCodes.InvalidAttributeValue, ctx.Value?.Span ?? ctx.NameSpan,
                        "模板的 Context 必须是一个 C# 标识符（lambda 参数名），如 Context=\"item\"");
            }
            string frag = Next("__t");
            Line($"{owner}.{slot.Name} = {context} =>");
            Line("{");
            _indent++;
            Line($"var {frag} = new {UI}NodeList();");
            EmitNodes(slotElement.Children, frag);
            Line($"return new {UI}Fragment({frag});");
            _indent--;
            Line("};");
        }

        /// <summary>子节点：先构建到新的 NodeList，再整体赋给 <c>Children</c>（组件的 <c>Children</c> 可能为 null）。</summary>
        void EmitChildren(ElementSyntax e, string v, IReadOnlyList<SyntaxNode> children)
        {
            if (children.Count == 0) return;
            string list = Next("__c");
            Line($"var {list} = new {UI}NodeList();");
            EmitNodes(children, list);
            Map(e.NameSpan.Start);
            Line($"{v}.Children = {list};");
            Hidden();
        }

        // ------------------------------------------------------------ 指令属性

        void EmitDirectiveAttribute(AttributeSyntax a, string v, string? intrinsic, ComponentInfo? info, AttributeSyntax? bindEvent)
        {
            if (a.Value == null || a.Value.Parts.Count == 0) return; // AW4004 已报告
            var code = CodeValue(a.Value);
            switch (a.Name)
            {
                case "key":
                    Mapped($"{v}.Key = ", code.Text, code.Offset, ";", a.Span.Start);
                    return;
                case "ref":
                    if (ComponentLacks(a, info, "Ref", "组件引用")) return;
                    Mapped($"{v}.Ref = ", code.Text, code.Offset, ";", a.Span.Start);
                    return;
                case "attributes":
                    // 元素上的展开由 EmitElementSplats 在显式属性之后输出；这里只剩没有捕获 Prop 的组件：写入它自己的 Attributes 成员
                    if (ComponentLacks(a, info, "Attributes", "组件的属性透传（组件可以声明 [CaptureUnmatchedAttributes] AttributeDictionary 类型的 Prop）")) return;
                    Mapped($"{v}.Attributes.AddRange(", code.Text, code.Offset, ");", a.Span.Start);
                    return;
                case "bind" when a.Parameter == null:
                {
                    if (intrinsic is not ("input" or "textarea")) return; // AW4009 已报告
                    string evt = bindEvent?.Value?.LiteralValue?.Trim() == "change" ? "OnChange" : "OnInput";
                    EmitBind(a, v, "Value", evt, "__e", "__e.Value", code);
                    return;
                }
                case "bind":
                    return; // @bind:event 已在 @bind 中处理
            }
            if (a.Name.StartsWith("bind-", StringComparison.Ordinal))
            {
                if (a.Parameter != null) return;
                string bound = a.Name.Substring(5);
                if (bound.Length == 0 || intrinsic != null) return; // AW4009 已报告
                EmitBind(a, v, bound, bound + "Changed", "__v", "__v", code);
                return;
            }
            if (!a.Name.StartsWith("on", StringComparison.Ordinal) || a.Name.Length <= 2) return;
            string name = a.Name.Substring(2);
            if (intrinsic != null)
            {
                var evt = RuntimeElements.Event(intrinsic, name);
                if (evt == null)
                {
                    if (IntrinsicElements.IsKnownEvent(name))
                        _diagnostics.Error(DiagnosticCodes.NotSupportedByRuntime, a.NameSpan,
                            $"事件 @{a.Name} 尚未被运行时（AwAUI.UI）实现：<{intrinsic}> 上没有对应的处理器属性");
                    return; // 未知事件：AW4003 已报告
                }
                Mapped($"{v}.{evt.Member} = {UI}EventHandlers.Wrap<{UI}{evt.ArgsType}>(", code.Text, code.Offset, ");", a.Span.Start);
                return;
            }

            // 组件或大写的元素描述类型
            var known = RuntimeElements.AnyEvent(name);
            string member = known?.Member ?? "On" + Pascal(name);
            var prop = info?.FindProp(member);
            string? args = prop != null ? RuntimeElements.EventArgsOfHandlerType(prop.TypeName) : known?.ArgsType;
            if (prop != null && prop.TypeName == null) args = known?.ArgsType;
            if (args != null)
                Mapped($"{v}.{member} = {UI}EventHandlers.Wrap<{UI}{args}>(", code.Text, code.Offset, ");", a.Span.Start);
            else
                Mapped($"{v}.{member} = ", code.Text, code.Offset, ";", a.Span.Start);
        }

        /// <summary>
        /// <c>@ref</c> / <c>@attributes</c> 用在已解析的组件（不是元素描述类型）上、而组件没有同名成员时：
        /// 运行时（AwAUI.UI）还没有组件引用与组件级的属性透传，报 AW4012 并跳过，而不是生成一句无法编译的赋值。
        /// </summary>
        bool ComponentLacks(AttributeSyntax a, ComponentInfo? info, string member, string feature)
        {
            if (info == null || info.IsElement || info.FindProp(member) != null) return false;
            _diagnostics.Error(DiagnosticCodes.NotSupportedByRuntime, a.NameSpan,
                $"@{a.Name}：{feature}尚未被运行时（AwAUI.UI）实现，组件 {info.FullName} 也没有可写的 {member} 属性");
            return true;
        }

        /// <summary>
        /// <c>@bind</c> 展开：<c>State&lt;T&gt;</c> 目标用 <c>Binding.Get / Binding.Set</c>，其余按可赋值表达式（<c>x = value</c>）。
        /// </summary>
        void EmitBind(AttributeSyntax a, string v, string valueMember, string eventMember, string param, string newValue, ValueCode code)
        {
            string target = code.Text.Trim();
            if (_stateTargets.Contains(target))
            {
                Mapped($"{v}.{valueMember} = {UI}Binding.Get(", code.Text, code.Offset, ");", a.Span.Start);
                Map(a.Span.Start);
                Line($"{v}.{eventMember} = {param} => {UI}Binding.Set({target}, {newValue});");
            }
            else
            {
                Mapped($"{v}.{valueMember} = ", code.Text, code.Offset, ";", a.Span.Start);
                Map(a.Span.Start);
                Line($"{v}.{eventMember} = {param} => {target} = {newValue};");
            }
            Hidden();
        }

        // ------------------------------------------------------------ 值

        /// <summary>
        /// 值同时含有表达式与带文字的字面量（如 <c>"Hi @name"</c>）：只能理解为字符串插值。
        /// 字面量只含运算符、数字与空白时（<c>"@a + 1"</c>）仍按 C# 表达式处理。
        /// </summary>
        static bool IsMixedText(AttributeValueSyntax value)
        {
            bool words = false, expression = false;
            foreach (var p in value.Parts)
            {
                if (p is ExpressionSyntax) expression = true;
                else if (p is AttributeLiteralSyntax l)
                {
                    foreach (char c in l.Value)
                    {
                        if (char.IsLetter(c) || c == '_' || c > 0x7F)
                        {
                            words = true;
                            break;
                        }
                    }
                }
            }
            return words && expression;
        }

        /// <summary>值是两侧只有空白的单一表达式时返回它。</summary>
        static bool IsTrueFalseEnum(IntrinsicAttributeInfo info)
        {
            foreach (var v in info.EnumValues)
                if (v == "true") return true;
            return false;
        }

        static ExpressionSyntax? SingleExpression(AttributeValueSyntax value)
        {
            ExpressionSyntax? found = null;
            foreach (var p in value.Parts)
            {
                if (p is ExpressionSyntax e)
                {
                    if (found != null) return null;
                    found = e;
                }
                else if (p is AttributeLiteralSyntax l && l.Value.Trim().Length > 0) return null;
            }
            return found;
        }

        /// <summary>字符串类别：字面量与表达式拼接。</summary>
        static string StringValue(AttributeValueSyntax value)
        {
            if (value.Parts.Count == 0) return "\"\"";
            var parts = new List<string>();
            if (value.Parts[0] is ExpressionSyntax) parts.Add("\"\""); // 保证按字符串拼接
            foreach (var p in value.Parts)
            {
                if (p is AttributeLiteralSyntax l) parts.Add(Literal(l.Value));
                else if (p is ExpressionSyntax e) parts.Add("(" + e.Code + ")");
            }
            return string.Join(" + ", parts);
        }

        /// <summary>表达式类别：单一表达式，或引号内的原文整体作为 C# 表达式（字面量不解码实体）。</summary>
        static ValueCode CodeValue(AttributeValueSyntax value)
        {
            if (SingleExpression(value) is { } e) return new ValueCode(e.Code, e.CodeSpan.Start);
            if (value.Parts.Count == 1 && value.Parts[0] is AttributeLiteralSyntax only)
            {
                string text = only.Text;
                int lead = 0;
                while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;
                string trimmed = text.Trim();
                return trimmed.Length == 0 ? new ValueCode("default", -1) : new ValueCode(trimmed, only.Span.Start + lead);
            }
            var sb = new StringBuilder();
            foreach (var p in value.Parts)
            {
                if (p is AttributeLiteralSyntax l) sb.Append(l.Text);
                else if (p is ExpressionSyntax x) sb.Append('(').Append(x.Code).Append(')');
            }
            string code = sb.ToString().Trim();
            return new ValueCode(code.Length == 0 ? "default" : code, -1);
        }

        // ------------------------------------------------------------ 输出工具

        string Next(string prefix) => prefix + (_counter++).ToString(CultureInfo.InvariantCulture);

        void Line(string text)
        {
            if (text.Length > 0)
                for (int i = 0; i < _indent; i++) _sb.Append("    ");
            _sb.Append(text).Append('\n');
        }

        void Raw(string text) => _sb.Append(text).Append('\n');

        void Map(int offset)
        {
            if (!_lines) return;
            int line = _source.GetLinePosition(offset).Line;
            // #line 的文件名不是普通字符串字面量：C# 不处理其中的转义（\ 原样保留），所以按原文写入
            Raw($"#line {line.ToString(CultureInfo.InvariantCulture)} \"{_linePath}\"");
        }

        void Hidden()
        {
            if (_lines) Raw("#line hidden");
        }

        /// <summary>整行映射到属性所在的行（值由多段拼成、无法逐列对齐时）。</summary>
        void AttrLine(AttributeSyntax a, string text)
        {
            Map(a.Span.Start);
            Line(text);
            Hidden();
        }

        /// <summary>
        /// 输出 <c>prefix code suffix</c>，并把 <paramref name="code"/> 映射到源中的 <paramref name="codeOffset"/>：
        /// 有 <c>#line</c> 时前缀单独成行（处于 hidden 区），代码另起一行并用空格补齐到源中的列，
        /// 因此 C# 的诊断与断点精确落在 <c>.awaui</c> 的行列上。<paramref name="codeOffset"/> 为 -1 时整行映射到 <paramref name="fallbackOffset"/>。
        /// </summary>
        void Mapped(string prefix, string code, int codeOffset, string suffix, int fallbackOffset = -1)
        {
            if (!_lines)
            {
                Line(prefix.Length > 0 && !prefix.EndsWith("(", StringComparison.Ordinal) && !prefix.EndsWith(" ", StringComparison.Ordinal) && code.Length > 0 && !code.StartsWith(" ", StringComparison.Ordinal)
                    ? prefix + " " + code + suffix
                    : prefix + code + suffix);
                return;
            }
            if (codeOffset < 0)
            {
                Map(fallbackOffset < 0 ? 0 : fallbackOffset);
                Line(prefix + code + suffix);
                Hidden();
                return;
            }
            if (prefix.Length > 0) Line(prefix);
            Map(codeOffset);
            Pad(codeOffset, code);
            _sb.Append(code).Append(suffix).Append('\n');
            Hidden();
        }

        /// <summary>
        /// 用空格补齐到源中的列。超长的行不补齐（只保证行号正确）：否则整个文件写在一行时，每个片段都要补齐到 O(n) 列，输出变成二次方。
        /// </summary>
        void Pad(int offset, string code)
        {
            if (code.Length > 0 && (code[0] == '\n' || code[0] == '\r')) return; // 首行为空，无需对齐
            int column = _source.GetLinePosition(offset).Column;
            if (column - 1 <= MaxPadding) _sb.Append(' ', column - 1);
        }

        /// <summary>原样输出一段源代码（<c>@code</c>、代码体中的语句），首行补齐到源中的列。</summary>
        void MappedRaw(string code, int offset)
        {
            if (!_lines)
            {
                _sb.Append(code).Append('\n');
                return;
            }
            Map(offset);
            Pad(offset, code);
            _sb.Append(code).Append('\n');
            Hidden();
        }

        static string PropertyName(string attr)
        {
            if (PropertyNames.TryGetValue(attr, out var name)) return name;
            return Pascal(attr);
        }
    }

    /// <summary>收集 <c>@bind</c> / <c>@bind-X</c> 的目标表达式（去掉首尾空白的原文），供源生成器判断哪些是 <c>State&lt;T&gt;</c>。</summary>
    public static IReadOnlyList<string> CollectBindTargets(SyntaxNode root)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<SyntaxNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is ElementSyntax e)
            {
                foreach (var a in e.Attributes)
                {
                    if (!a.IsDirective || a.Parameter != null || a.Value == null) continue;
                    if (a.Name != "bind" && !a.Name.StartsWith("bind-", StringComparison.Ordinal)) continue;
                    var e1 = a.Value.SingleExpression;
                    string? code = e1?.Code ?? a.Value.LiteralText;
                    if (code == null) continue;
                    code = code.Trim();
                    if (code.Length > 0 && seen.Add(code)) result.Add(code);
                }
            }
            foreach (var c in n.ChildNodes()) stack.Push(c);
        }
        return result;
    }

    /// <summary>
    /// 组件目录的查询键：标签名；泛型标签（<c>&lt;Grid&lt;int&gt;&gt;</c>）附加元数据式的元数 <c>`1</c>。
    /// </summary>
    public static string CatalogKey(ElementSyntax e) =>
        e.TypeArguments != null ? e.Name + "`" + GenericArity(e.TypeArguments).ToString(CultureInfo.InvariantCulture) : e.Name;

    /// <summary>收集文件中全部组件标签的目录查询键（<see cref="CatalogKey"/>），供源生成器预先解析组件目录。</summary>
    public static IReadOnlyList<string> CollectComponentTags(SyntaxNode root)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<SyntaxNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is ElementSyntax e && e.ElementKind == ElementKind.Component && seen.Add(CatalogKey(e)))
                result.Add(CatalogKey(e));
            foreach (var c in n.ChildNodes()) stack.Push(c);
        }
        return result;
    }

    /// <summary>类型实参文本中顶层逗号数 + 1。</summary>
    internal static int GenericArity(string typeArguments)
    {
        int depth = 0, arity = 1;
        foreach (char c in typeArguments)
        {
            if (c == '<' || c == '(' || c == '[') depth++;
            else if ((c == '>' || c == ')' || c == ']') && depth > 0) depth--;
            else if (c == ',' && depth == 0) arity++;
        }
        return arity;
    }

    static string Pascal(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool upper = true;
        foreach (char c in s)
        {
            if (c == '-' || c == ':' || c == '.' || c == '_') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    /// <summary>C# 普通字符串字面量（C# 7.3 可用）。代理项一律转义，孤立的代理项因此也能写进 UTF-8 的生成文件。</summary>
    internal static string Literal(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (char.IsControl(c) || c == '\u2028' || c == '\u2029' || c == '\u0085' || char.IsSurrogate(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
