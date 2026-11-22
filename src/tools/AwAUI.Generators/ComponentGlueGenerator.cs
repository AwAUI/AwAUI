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
/// AW1001 组件内部给自己的 Props 赋值（构造函数除外）；
/// AW1004 在动态列表（循环或 Select/SelectMany）中创建的组件子项没有设置 Key。
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

    internal static readonly DiagnosticDescriptor MissingKeyInDynamicList = new(
        "AW1004",
        "动态列表中的组件子项缺少 Key",
        "组件 '{0}' 在循环或 Select 中作为列表项创建，但没有设置 Key：请在循环/Select 创建的组件上设置 Key，以便重建时保持状态与焦点",
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

        // AW1004：只检查组件实例的构造（普通 Node 不要求 Key）
        var keyResults = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ObjectCreationExpressionSyntax,
                static (ctx, ct) => AnalyzeDynamicKey((ObjectCreationExpressionSyntax)ctx.Node, ctx.SemanticModel, ct))
            .Where(static d => d is not null);

        context.RegisterSourceOutput(keyResults, static (spc, d) => spc.ReportDiagnostic(d!.ToDiagnostic()));
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

    // ------------------------------------------------------------ AW1004

    /// <summary>AW1004 的保守判定：只处理循环体或 Select/SelectMany lambda 内、确实作为列表项收集的组件构造。</summary>
    internal static DiagnosticInfo? AnalyzeDynamicKey(ObjectCreationExpressionSyntax creation, SemanticModel model, CancellationToken ct)
    {
        var kind = DynamicKind(creation);
        if (kind == Dynamism.None) return null;
        bool projected = kind == Dynamism.Projection;

        // 循环体中的单次构造不算列表：只有作为数组/集合元素、Add/AddRange 实参或迭代器产出时才报告。
        // Select/SelectMany 的 lambda 本身就在投影序列，元素会被收集。
        if (!projected && !IsCollectedAsItem(creation)) return null;

        if (HasKey(creation)) return null;

        if (model.GetTypeInfo(creation, ct).Type is not INamedTypeSymbol type) return null;
        if (type.TypeKind == TypeKind.Error) return null;
        if (!DerivesFromComponent(type)) return null;

        // 保守起见只检查组件自身成员中的构造（Build 及其局部/嵌套函数）。测试与框架里直接拼装
        // 描述树的静态辅助方法不在此列，避免对无需保持身份的固定夹具误报。
        if (!IsInsideComponentType(creation, model, ct)) return null;

        return DiagnosticInfo.Create(MissingKeyInDynamicList, creation.Type.GetLocation(), type.Name);
    }

    enum Dynamism { None, Loop, Projection }

    static Dynamism DynamicKind(SyntaxNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (p is ForEachStatementSyntax forEach && forEach.Statement.Span.Contains(node.Span)) return Dynamism.Loop;
            // 计数循环（for (int i = ...; ...)）生成的是位置稳定的列表，不要求 Key，避免对框架/示例里
            // 按索引生成的月份、年份等固定列表误报。
            if (p is ForStatementSyntax forStatement && forStatement.Statement.Span.Contains(node.Span) && !IsCountedLoop(forStatement))
                return Dynamism.Loop;
            if (p is WhileStatementSyntax whileStatement && whileStatement.Statement.Span.Contains(node.Span)) return Dynamism.Loop;
            if (p is DoStatementSyntax doStatement && doStatement.Statement.Span.Contains(node.Span)) return Dynamism.Loop;
            if (p is SimpleLambdaExpressionSyntax simple && IsProjectionLambda(simple)) return Dynamism.Projection;
            if (p is ParenthesizedLambdaExpressionSyntax parenthesized && IsProjectionLambda(parenthesized)) return Dynamism.Projection;
        }
        return Dynamism.None;
    }

    static bool IsInsideComponentType(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (p is TypeDeclarationSyntax declaration
                && model.GetDeclaredSymbol(declaration, ct) is INamedTypeSymbol symbol
                && DerivesFromComponent(symbol))
                return true;
        }
        return false;
    }

    static bool IsCountedLoop(ForStatementSyntax loop)
    {
        if (loop.Declaration is not VariableDeclarationSyntax declaration) return false;
        var type = declaration.Type is NullableTypeSyntax nullable ? nullable.ElementType : declaration.Type;
        return type is PredefinedTypeSyntax predefined
            && predefined.Keyword.Kind() is SyntaxKind.IntKeyword or SyntaxKind.LongKeyword or SyntaxKind.ShortKeyword
                or SyntaxKind.ByteKeyword or SyntaxKind.SByteKeyword or SyntaxKind.UIntKeyword or SyntaxKind.ULongKeyword
                or SyntaxKind.UShortKeyword;
    }

    static bool IsProjectionLambda(LambdaExpressionSyntax lambda) =>
        lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } }
        && invocation.Expression is MemberAccessExpressionSyntax access
        && (access.Name.Identifier.ValueText == "Select" || access.Name.Identifier.ValueText == "SelectMany");

    /// <summary>构造是否直接作为一个列表/子项元素被收集。</summary>
    static bool IsCollectedAsItem(ObjectCreationExpressionSyntax creation)
    {
        SyntaxNode node = creation;
        // 集合表达式 [..] 的元素被包在 ExpressionElementSyntax 中
        while (node.Parent is ParenthesizedExpressionSyntax || node.Parent is ExpressionElementSyntax) node = node.Parent;

        switch (node.Parent)
        {
            case CollectionExpressionSyntax:
                return true;

            case InitializerExpressionSyntax init:
                // 数组 / 集合初始化器的元素
                if (init.IsKind(SyntaxKind.ArrayInitializerExpression) || init.IsKind(SyntaxKind.CollectionInitializerExpression))
                    return true;
                // 成员初始化器 Children = { new Foo() }
                return init.IsKind(SyntaxKind.ObjectInitializerExpression) && init.Parent is AssignmentExpressionSyntax;

            case ArgumentSyntax argument:
                return IsAddInvocation(argument);

            // var item = new Foo(); ... list.Add(item);
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }:
                return IsAddedLater(creation, declarator.Identifier.ValueText);

            case YieldStatementSyntax:
                return true;

            default:
                return false;
        }
    }

    static bool IsAddedLater(ObjectCreationExpressionSyntax creation, string name)
    {
        var block = creation.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        if (block is null) return false;

        foreach (var invocation in block.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.SpanStart <= creation.SpanStart) continue;
            if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
            if (access.Name.Identifier.ValueText != "Add" && access.Name.Identifier.ValueText != "AddRange") continue;
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (argument.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == name) return true;
            }
        }
        return false;
    }

    static bool IsAddInvocation(ArgumentSyntax argument)
    {
        if (argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation }) return false;
        return invocation.Expression is MemberAccessExpressionSyntax access
            && (access.Name.Identifier.ValueText == "Add" || access.Name.Identifier.ValueText == "AddRange");
    }

    /// <summary>对象初始化器或构造后的 <c>x.Key = ...</c> 赋值均视为已设置 Key。</summary>
    static bool HasKey(ObjectCreationExpressionSyntax creation)
    {
        var init = creation.Initializer;
        if (init is not null && init.IsKind(SyntaxKind.ObjectInitializerExpression))
        {
            foreach (var expression in init.Expressions)
            {
                if (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax id } && id.Identifier.ValueText == "Key")
                    return true;
            }
        }
        return HasPostConstructionKey(creation);
    }

    static bool HasPostConstructionKey(ObjectCreationExpressionSyntax creation)
    {
        string? target = null;
        if (creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
            target = declarator.Identifier.ValueText;
        else if (creation.Parent is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment && assignment.Right == creation)
            target = left.Identifier.ValueText;
        if (target is null) return false;

        var block = creation.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        if (block is null) return false;

        foreach (var assignment in block.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.SpanStart <= creation.SpanStart) continue;
            // 可能是别处同名变量，但保守起见按已设置 Key 处理（宁可漏报，不误报）
            if (assignment.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner, Name: IdentifierNameSyntax name }
                && owner.Identifier.ValueText == target
                && name.Identifier.ValueText == "Key")
                return true;
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
