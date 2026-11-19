using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using AwAUI.CodeGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AwAUI.Generators;

/// <summary>
/// 组件胶水源生成器：为每个（直接或间接）派生自 <c>AwAUI.UI.Component</c> 的 partial 类生成
/// <c>PropsEqual</c> / <c>AdoptProps</c>（以及存在 init Props 时的 <c>CanAdoptProps</c>）。
/// <para>
/// Props 的判定是纯语法层面的约定（公共、非静态、带公共 set/init 访问器的非索引器属性），语义模型只用于确认基类链与取得类型全名。
/// 基类若是本编译中的 partial 组件或已有胶水（引用程序集中生成过、或手写了 <c>AdoptProps</c>），生成的方法先调用基类实现；
/// 否则基类上声明的 Props 由派生类一并处理。
/// </para>
/// 诊断：AW1006 组件类不是 partial；AW1007 嵌套组件的外层类型不是 partial；AW1009 组件是 file 局部类型；
/// AW1001 组件内部给自己的 Props 赋值（构造函数除外）。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentGlueGenerator : IIncrementalGenerator
{
    const string ComponentMetadataName = "AwAUI.UI.Component";
    const string Category = "AwAUI.Components";

    internal static readonly DiagnosticDescriptor AssignsOwnProp = new(
        "AW1001",
        "组件内部不应给自己的 Props 赋值",
        "组件 '{0}' 在内部给自己的 Prop '{1}' 赋值：Props 由父组件传入，父组件重建时会被覆盖。请改用 State 保存内部状态",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor NotPartial = new(
        "AW1006",
        "组件类应声明为 partial",
        "组件类 '{0}' 不是 partial，无法生成 Props 胶水（PropsEqual/AdoptProps）：父组件重建时它的 Props 不会更新。请声明为 'partial class {0}'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ContainerNotPartial = new(
        "AW1007",
        "嵌套组件的外层类型应声明为 partial",
        "组件 '{0}' 嵌套在非 partial 的类型 '{1}' 中，无法生成 Props 胶水。请把 '{1}' 声明为 partial",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor FileLocal = new(
        "AW1009",
        "file 局部组件无法生成 Props 胶水",
        "组件 '{0}' 是 file 局部类型：它的 partial 声明必须都在同一文件中，无法生成 Props 胶水（PropsEqual/AdoptProps），父组件重建时它的 Props 不会更新。请去掉 'file' 修饰符，或手写这两个方法",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider
            .CreateSyntaxProvider(
                // 组件类必然有基类列表或是 partial（partial 的其他部分可能才写了基类）
                static (node, _) => node is ClassDeclarationSyntax c && (c.BaseList is not null || c.Modifiers.Any(SyntaxKind.PartialKeyword)),
                static (ctx, ct) => Analyze((ClassDeclarationSyntax)ctx.Node, ctx.SemanticModel, ct))
            .Where(static r => r is not null);

        context.RegisterSourceOutput(results, static (spc, r) => Produce(spc, r!));
    }

    static void Produce(SourceProductionContext spc, GlueResult r)
    {
        foreach (var d in r.Diagnostics) spc.ReportDiagnostic(d.ToDiagnostic());
        if (r.Model is not null) spc.AddSource(r.HintName, SourceText.From(ComponentGlueEmitter.EmitFile(r.Model), Encoding.UTF8));
    }

    // ------------------------------------------------------------ 分析

    internal static GlueResult? Analyze(ClassDeclarationSyntax decl, SemanticModel model, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol symbol) return null;
        if (!DerivesFromComponent(symbol)) return null;

        // .awaui 组件的代码后置文件（X.awaui.cs）：胶水只由 .awaui 生成器产出（它看得到 @code 中的 Props，本生成器看不到它的输出）。
        // 代码后置文件重复写出 `: Component` 时两边都会生成 PropsEqual / AdoptProps（CS0111），因此这里跳过。
        foreach (var r in symbol.DeclaringSyntaxReferences)
            if (r.SyntaxTree.FilePath.EndsWith(".awaui.cs", StringComparison.OrdinalIgnoreCase)) return null;

        // partial 类的多个声明只处理第一个
        var refs = symbol.DeclaringSyntaxReferences;
        if (refs.Length > 1 && (refs[0].SyntaxTree != decl.SyntaxTree || refs[0].Span != decl.Span)) return null;

        var diagnostics = new List<DiagnosticInfo>();
        string display = symbol.Name;

        if (!decl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            // 手写了完整胶水（至少 PropsEqual 与 AdoptProps）的非 partial 组件不需要生成器
            var own = DeclaredGlue(symbol);
            if ((own & (GlueMembers.PropsEqual | GlueMembers.AdoptProps)) != (GlueMembers.PropsEqual | GlueMembers.AdoptProps))
                diagnostics.Add(DiagnosticInfo.Create(NotPartial, decl.Identifier.GetLocation(), display));
            return new GlueResult(null, "", diagnostics);
        }

        // file 局部类型：生成的另一份 partial 声明不在同一文件，无法编译（其元数据名也不能用作提示名）
        if (IsInFileLocalType(symbol))
        {
            if (!DeclaresGlue(symbol)) diagnostics.Add(DiagnosticInfo.Create(FileLocal, decl.Identifier.GetLocation(), display));
            return new GlueResult(null, "", diagnostics);
        }

        var containers = new List<GlueContainingType>();
        for (var outer = symbol.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            var outerDecl = outer.DeclaringSyntaxReferences.Length > 0 ? outer.DeclaringSyntaxReferences[0].GetSyntax(ct) as TypeDeclarationSyntax : null;
            if (outerDecl is null || !outerDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                diagnostics.Add(DiagnosticInfo.Create(ContainerNotPartial, decl.Identifier.GetLocation(), display, outer.Name));
                return new GlueResult(null, "", diagnostics);
            }
            containers.Insert(0, new GlueContainingType(KeywordOf(outerDecl), outer.Name + TypeParameterList(outer)));
        }

        // 收集 Props：本类，以及没有胶水的基类组件
        var props = new List<GlueProp>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddProps(symbol, props, names);
        bool baseHasGlue = false;
        for (var b = symbol.BaseType; b is not null && !IsComponent(b); b = b.BaseType)
        {
            if (HasGlue(b, ct))
            {
                baseHasGlue = true;
                break;
            }
            AddProps(b, props, names);
        }

        // AW1001：组件内部给自己的 Props 赋值（纯语法判断）
        if (names.Count > 0)
        {
            foreach (var r in refs)
            {
                if (r.GetSyntax(ct) is ClassDeclarationSyntax part) FindPropAssignments(part, names, display, diagnostics);
            }
        }

        // 手写的胶水方法不再生成，只补齐其余的（只手写 PropsEqual 时仍需要 AdoptProps，否则 Props 永远不会更新）
        var needed = GlueMembers.PropsEqual | GlueMembers.AdoptProps;
        foreach (var p in props)
        {
            if (p.IsInitOnly)
            {
                needed |= GlueMembers.CanAdoptProps;
                break;
            }
        }
        var members = needed & ~DeclaredGlue(symbol);
        if (members == GlueMembers.None) return new GlueResult(null, "", diagnostics);

        string? ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        var glue = new ComponentGlueModel(ns, containers, symbol.Name, TypeParameterList(symbol), props, baseHasGlue, members);
        return new GlueResult(glue, HintName(symbol), diagnostics);
    }

    static bool IsInFileLocalType(INamedTypeSymbol symbol)
    {
        for (var t = symbol; t is not null; t = t.ContainingType)
            if (t.IsFileLocal) return true;
        return false;
    }

    static bool IsComponent(INamedTypeSymbol t) =>
        t.Name == "Component" && t.ContainingNamespace is { } ns && ns.ToDisplayString() == "AwAUI.UI" && t.TypeArguments.Length == 0;

    static bool DerivesFromComponent(INamedTypeSymbol symbol)
    {
        for (var b = symbol.BaseType; b is not null; b = b.BaseType)
            if (IsComponent(b)) return true;
        return false;
    }

    /// <summary>类型自己声明了胶水方法（手写）。</summary>
    static bool DeclaresGlue(INamedTypeSymbol t) => DeclaredGlue(t) != GlueMembers.None;

    /// <summary>类型自己声明（重写）了哪些胶水方法。</summary>
    static GlueMembers DeclaredGlue(INamedTypeSymbol t)
    {
        var result = GlueMembers.None;
        foreach (var m in t.GetMembers())
        {
            if (m is not IMethodSymbol { IsOverride: true } method) continue;
            switch (method.Name)
            {
                case "PropsEqual": result |= GlueMembers.PropsEqual; break;
                case "AdoptProps": result |= GlueMembers.AdoptProps; break;
                case "CanAdoptProps": result |= GlueMembers.CanAdoptProps; break;
            }
        }
        return result;
    }

    /// <summary>基类有胶水：引用程序集中已声明 AdoptProps 重写，或本编译中的 partial 组件（将由本生成器生成）。</summary>
    static bool HasGlue(INamedTypeSymbol b, CancellationToken ct)
    {
        var def = b.OriginalDefinition;
        if (DeclaresGlue(def)) return true;
        if (def.DeclaringSyntaxReferences.Length == 0) return false;
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword))
                return true;
        }
        return false;
    }

    static void AddProps(INamedTypeSymbol type, List<GlueProp> props, HashSet<string> names)
    {
        foreach (var m in type.GetMembers())
        {
            if (m is not IPropertySymbol p) continue;
            if (p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public) continue;
            var set = p.SetMethod;
            if (set is null || set.DeclaredAccessibility != Accessibility.Public) continue;
            if (p.GetMethod is null || p.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
            if (p.Type.IsRefLikeType || p.Type.TypeKind == TypeKind.Pointer || p.Type.TypeKind == TypeKind.FunctionPointer) continue;
            if (p.Name == "Key") continue;
            if (!names.Add(p.Name)) continue; // 重写的属性只处理一次
            props.Add(new GlueProp(p.Name, p.Type.ToDisplayString(TypeFormat), set.IsInitOnly));
        }
    }

    static void FindPropAssignments(ClassDeclarationSyntax part, HashSet<string> names, string display, List<DiagnosticInfo> diagnostics)
    {
        foreach (var node in part.DescendantNodes())
        {
            ExpressionSyntax? target = node switch
            {
                AssignmentExpressionSyntax a => a.Left,
                PrefixUnaryExpressionSyntax pre when pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression) => pre.Operand,
                PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression) => post.Operand,
                _ => null,
            };
            if (target is null) continue;
            string? name = target switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.ValueText,
                _ => null,
            };
            if (name is null || !names.Contains(name)) continue;
            // 对象初始化器 / with 表达式中的成员赋值属于另一个对象
            if (node is AssignmentExpressionSyntax && node.Parent is InitializerExpressionSyntax init
                && (init.IsKind(SyntaxKind.ObjectInitializerExpression) || init.IsKind(SyntaxKind.WithInitializerExpression))
                && target is IdentifierNameSyntax)
                continue;
            if (!IsInsideOwnMember(node, part)) continue;
            diagnostics.Add(DiagnosticInfo.Create(AssignsOwnProp, node.GetLocation(), display, name));
        }
    }

    /// <summary>赋值位于本类（而非嵌套类型）的非构造函数成员中；手写的 <c>AdoptProps</c> 本来就是给 Props 赋值的地方。</summary>
    static bool IsInsideOwnMember(SyntaxNode node, ClassDeclarationSyntax part)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (p is ConstructorDeclarationSyntax) return false;
            if (p is MethodDeclarationSyntax m && m.Identifier.ValueText == "AdoptProps" && m.Modifiers.Any(SyntaxKind.OverrideKeyword)) return false;
            if (p is BaseTypeDeclarationSyntax) return ReferenceEquals(p, part);
        }
        return false;
    }

    static string KeywordOf(TypeDeclarationSyntax decl) => decl switch
    {
        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        _ => "class",
    };

    static string? TypeParameterList(INamedTypeSymbol t)
    {
        if (t.TypeParameters.Length == 0) return null;
        return "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";
    }

    static string HintName(INamedTypeSymbol symbol)
    {
        var sb = new StringBuilder();
        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns) sb.Append(ns.ToDisplayString()).Append('.');
        var chain = new List<string>();
        for (var t = symbol; t is not null; t = t.ContainingType) chain.Insert(0, t.MetadataName);
        sb.Append(string.Join(".", chain));
        sb.Replace('`', '_');
        return sb.Append(".Props.g.cs").ToString();
    }
}

/// <summary>增量管线中的分析结果（值相等，便于缓存）。</summary>
internal sealed class GlueResult : IEquatable<GlueResult>
{
    public GlueResult(ComponentGlueModel? model, string hintName, List<DiagnosticInfo> diagnostics)
    {
        Model = model;
        HintName = hintName;
        Diagnostics = diagnostics.ToImmutableArray();
    }

    public ComponentGlueModel? Model { get; }

    public string HintName { get; }

    public ImmutableArray<DiagnosticInfo> Diagnostics { get; }

    public bool Equals(GlueResult? other)
    {
        if (other is null) return false;
        if (HintName != other.HintName || !Equals(Model, other.Model)) return false;
        return Diagnostics.SequenceEqual(other.Diagnostics);
    }

    public override bool Equals(object? obj) => Equals(obj as GlueResult);

    public override int GetHashCode() => HintName.GetHashCode() * 31 + Diagnostics.Length;
}

/// <summary>诊断的可比较表示（不持有语法树）。</summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    DiagnosticInfo(DiagnosticDescriptor descriptor, string path, TextSpan span, LinePositionSpan lineSpan, string[] args)
    {
        Descriptor = descriptor;
        Path = path;
        Span = span;
        LineSpan = lineSpan;
        Args = args;
    }

    public DiagnosticDescriptor Descriptor { get; }
    public string Path { get; }
    public TextSpan Span { get; }
    public LinePositionSpan LineSpan { get; }
    public string[] Args { get; }

    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location location, params string[] args) =>
        new(descriptor, location.SourceTree?.FilePath ?? "", location.SourceSpan, location.GetLineSpan().Span, args);

    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Descriptor, Location.Create(Path, Span, LineSpan), Args.Cast<object>().ToArray());

    public bool Equals(DiagnosticInfo? other) =>
        other is not null && Descriptor.Id == other.Descriptor.Id && Path == other.Path && Span == other.Span && Args.SequenceEqual(other.Args);

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode() => Descriptor.Id.GetHashCode() * 31 + Span.GetHashCode();
}
