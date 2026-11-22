using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using AwAUI.Markup.Compiler.Analysis;
using AwAUI.Markup.Compiler.CodeGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AwAUI.Markup.Generator;

/// <summary>
/// 解析阶段（design/17「组件信息从哪里来」）：把全部 <c>.awaui</c> 的类骨架加入一份临时编译，
/// 在其中解析组件标签、本组件的 Props 与胶水信息、<c>@bind</c> 目标的类型。只读取符号，不报告诊断（类型错误交给最终的 C# 编译）。
/// </summary>
internal static class ComponentResolver
{
    /// <summary>骨架类中的探针方法：在它的方法体内做 <c>@bind</c> 目标的推测绑定。</summary>
    public const string ProbeMethod = "__AwAUIMarkupBindProbe";

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    static readonly SymbolDisplayFormat NameFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGenericsOptions(SymbolDisplayGenericsOptions.None)
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static ImmutableArray<ResolvedFile> Resolve(ImmutableArray<FileDeclaration> files, Compilation compilation,
        CSharpParseOptions? parseOptions, CancellationToken ct)
    {
        if (files.IsDefaultOrEmpty) return ImmutableArray<ResolvedFile>.Empty;
        var options = parseOptions ?? CSharpParseOptions.Default;
        bool nullable = options.LanguageVersion >= LanguageVersion.CSharp8;

        var trees = new SyntaxTree[files.Length];
        for (int i = 0; i < files.Length; i++)
            trees[i] = CSharpSyntaxTree.ParseText(files[i].Skeleton, options, path: files[i].Input.Path + ".skeleton.cs", cancellationToken: ct);
        var withSkeletons = compilation.AddSyntaxTrees(trees);
        var known = new Known(withSkeletons, GlobalUsingsOf(compilation));
        var awauiTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files) awauiTypes.Add(f.MetadataName);

        var result = ImmutableArray.CreateBuilder<ResolvedFile>(files.Length);
        // 提示名必须在生成器内唯一（重复时 AddSource 抛异常，整个生成器的输出都会丢失）：
        // 项目目录之外的同名链接文件、重复加入的 AdditionalFiles 都会得到相同的相对路径，这里按出现顺序加序号
        var hintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            var tags = new List<ResolvedTag>(f.ComponentTags.Count);
            foreach (var tag in f.ComponentTags)
                tags.Add(new ResolvedTag(tag, ResolveTag(tag, f, withSkeletons, known)));
            var classInfo = ClassInfo(f, trees[i], withSkeletons, known, awauiTypes, ct);
            string hint = MarkupSourceGenerator.HintName(f.Input.RelativePath);
            for (int n = 2; !hintNames.Add(hint); n++)
                hint = MarkupSourceGenerator.HintName(f.Input.RelativePath, n);
            result.Add(new ResolvedFile(f, new EquatableArray<ResolvedTag>(tags), classInfo, nullable, hint));
        }
        return result.MoveToImmutable();
    }

    /// <summary>运行时中用到的类型。</summary>
    sealed class Known
    {
        public Known(Compilation c, ImmutableArray<string> globalUsings)
        {
            GlobalUsings = globalUsings;
            Node = c.GetTypeByMetadataName("AwAUI.UI.Node");
            Component = c.GetTypeByMetadataName("AwAUI.UI.Component");
            ElementNode = c.GetTypeByMetadataName("AwAUI.UI.ElementNode");
            NodeList = c.GetTypeByMetadataName("AwAUI.UI.NodeList");
            ClassValue = c.GetTypeByMetadataName("AwAUI.UI.ClassValue");
            StyleValue = c.GetTypeByMetadataName("AwAUI.UI.StyleValue");
            State = c.GetTypeByMetadataName("AwAUI.UI.State`1");
            AttributeDictionary = c.GetTypeByMetadataName("AwAUI.UI.AttributeDictionary");
            CaptureUnmatched = c.GetTypeByMetadataName("AwAUI.UI.CaptureUnmatchedAttributesAttribute");
        }

        public INamedTypeSymbol? Node { get; }
        public INamedTypeSymbol? Component { get; }
        public INamedTypeSymbol? ElementNode { get; }
        public INamedTypeSymbol? NodeList { get; }
        public INamedTypeSymbol? ClassValue { get; }
        public INamedTypeSymbol? StyleValue { get; }
        public INamedTypeSymbol? State { get; }
        public INamedTypeSymbol? AttributeDictionary { get; }
        public INamedTypeSymbol? CaptureUnmatched { get; }

        /// <summary>项目里的 <c>global using</c>（SDK 隐式 using 与手写的全局 using），按出现顺序。</summary>
        public ImmutableArray<string> GlobalUsings { get; }
    }

    static readonly ConditionalWeakTable<Compilation, GlobalUsingsInfo> s_globalUsings = new();

    sealed class GlobalUsingsInfo
    {
        public GlobalUsingsInfo(ImmutableArray<string> names) => Names = names;

        public ImmutableArray<string> Names { get; }
    }

    /// <summary>
    /// 收集项目级全局导入（<c>&lt;Using Include="..." /&gt;</c> 生成的 <c>global using</c> 或手写的全局 using 语句）。
    /// 生成的 C# 与项目在同一编译中，本来就能看到这些 using，这里只是把它们加入标签解析的候选。
    /// </summary>
    static ImmutableArray<string> GlobalUsingsOf(Compilation compilation) =>
        s_globalUsings.GetValue(compilation, static c => new GlobalUsingsInfo(CollectGlobalUsings(c))).Names;

    static ImmutableArray<string> CollectGlobalUsings(Compilation compilation)
    {
        const string GlobalPrefix = "global::";
        var usings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            // 只解析根节点：global using 不能出现在命名空间内
            if (tree.GetRoot() is not CompilationUnitSyntax root) continue;
            foreach (var directive in root.Usings)
            {
                if (!directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) continue;
                // 别名与 static using 不引入命名空间
                if (directive.Alias is not null || directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) continue;
                string? name = directive.Name?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (name!.StartsWith(GlobalPrefix, StringComparison.Ordinal)) name = name.Substring(GlobalPrefix.Length);
                if (seen.Add(name)) usings.Add(name);
            }
        }
        return usings.Count == 0 ? ImmutableArray<string>.Empty : usings.ToImmutableArray();
    }

    // ------------------------------------------------------------ 组件标签

    /// <summary>
    /// 按 C# 的名称查找顺序解析标签：当前命名空间 → 文件与 <c>_Imports.awaui</c> 的 using → 项目级 <c>global using</c>
    /// → 项目配置的额外命名空间（<c>AwAUIMarkupNamespaces</c>）→ 框架内置命名空间（<c>AwAUIMarkupBuiltInNamespaces</c>，
    /// 默认为 <see cref="CSharpEmitter.SystemNamespaces"/>）
    /// → 默认导入 → 外层命名空间 → 全局；带点的标签另外按全名查找。
    /// 只接受派生自 <c>AwAUI.UI.Node</c> 的类型。
    /// 内置命名空间插在用户 using 之后：显式 <c>@using</c> 与项目级全局 using 优先，其次是 <c>AwAUI.Controls</c>，最后才默认导入的
    /// <c>AwAUI.UI</c>。编译中没有引用相应程序集时（如只引用 AwAUI.UI 的测试）这些候选自然落空。
    /// </summary>
    static ComponentInfo? ResolveTag(string tag, FileDeclaration f, Compilation c, Known known)
    {
        if (known.Node is null) return null;
        var candidates = new List<string>();
        if (tag.IndexOf('.') >= 0) candidates.Add(tag);
        string ns = f.Namespace;
        if (ns.Length > 0) candidates.Add(ns + "." + tag);
        int explicitCount = Math.Max(0, Math.Min(f.ExplicitUsingCount, f.Usings.Count));
        for (int i = 0; i <= f.Usings.Count; i++)
        {
            if (i == explicitCount && tag.IndexOf('.') < 0)
            {
                foreach (var global in known.GlobalUsings) candidates.Add(global + "." + tag);
                // 项目配置的额外命名空间（AwAUIMarkupNamespaces）优先于框架内置命名空间
                foreach (var configured in f.Input.TagNamespaces) candidates.Add(configured + "." + tag);
                foreach (var system in f.Input.BuiltInNamespaces) candidates.Add(system + "." + tag);
            }
            if (i < f.Usings.Count) candidates.Add(f.Usings[i] + "." + tag);
        }
        for (int dot = ns.LastIndexOf('.'); dot > 0; dot = ns.LastIndexOf('.', dot - 1))
            candidates.Add(ns.Substring(0, dot) + "." + tag);
        candidates.Add(tag);
        foreach (var name in candidates)
        {
            var type = c.GetTypeByMetadataName(name);
            if (type is null || !DerivesFrom(type, known.Node)) continue;
            return Describe(type, known);
        }
        return null;
    }

    static ComponentInfo Describe(INamedTypeSymbol type, Known known)
    {
        var props = new List<PropInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && !SymbolEqualityComparer.Default.Equals(t, known.Node); t = t.BaseType)
        {
            foreach (var m in t.GetMembers())
            {
                if (m is not IPropertySymbol p || p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public) continue;
                if (p.SetMethod is null || p.SetMethod.DeclaredAccessibility != Accessibility.Public) continue;
                if (!seen.Add(p.Name)) continue;
                props.Add(new PropInfo(p.Name, KindOf(p.Type, known), p.Type.ToDisplayString(TypeFormat)));
            }
        }
        bool isElement = known.ElementNode is not null && DerivesFrom(type, known.ElementNode);
        return new ComponentInfo(type.ToDisplayString(NameFormat), props, isElement, isElement ? null : CaptureProp(type, known));
    }

    /// <summary>
    /// 标了 <c>[CaptureUnmatchedAttributes]</c>、类型为 <c>AttributeDictionary</c> 的可写 Prop（相当于 Blazor 的 CaptureUnmatchedValues）：
    /// 最派生的类型优先，同一类型中按名称序数排序取第一个；特性按继承查找（重写的属性也算）。
    /// </summary>
    static string? CaptureProp(INamedTypeSymbol type, Known known)
    {
        if (known.CaptureUnmatched is null || known.AttributeDictionary is null) return null;
        for (var t = type; t is not null && !SymbolEqualityComparer.Default.Equals(t, known.Node); t = t.BaseType)
        {
            string? found = null;
            foreach (var m in t.GetMembers())
            {
                if (m is not IPropertySymbol p || p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public) continue;
                if (p.SetMethod is null || p.SetMethod.DeclaredAccessibility != Accessibility.Public) continue;
                if (!Is(p.Type, known.AttributeDictionary) || !HasCaptureAttribute(p, known.CaptureUnmatched)) continue;
                if (found is null || string.CompareOrdinal(p.Name, found) < 0) found = p.Name;
            }
            if (found is not null) return found;
        }
        return null;
    }

    static bool HasCaptureAttribute(IPropertySymbol p, INamedTypeSymbol attribute)
    {
        for (var q = p; q is not null; q = q.OverriddenProperty)
            foreach (var a in q.GetAttributes())
                if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)) return true;
        return false;
    }

    static PropKind KindOf(ITypeSymbol type, Known known)
    {
        if (type.SpecialType == SpecialType.System_String) return PropKind.String;
        if (type.SpecialType == SpecialType.System_Boolean) return PropKind.Bool;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            && n.TypeArguments[0].SpecialType == SpecialType.System_Boolean) return PropKind.Bool;
        if (Is(type, known.ClassValue) || Is(type, known.StyleValue)) return PropKind.String;
        if (Is(type, known.NodeList)) return PropKind.Slot;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke })
        {
            if (invoke.Parameters.Length == 1 && known.Node is not null && invoke.ReturnType is INamedTypeSymbol r && DerivesFromOrIs(r, known.Node))
                return PropKind.Template;
            return PropKind.Delegate;
        }
        return PropKind.Other;
    }

    // ------------------------------------------------------------ 本组件

    static ComponentClassInfo? ClassInfo(FileDeclaration f, SyntaxTree skeleton, Compilation c, Known known, HashSet<string> awauiTypes, CancellationToken ct)
    {
        var symbol = c.GetTypeByMetadataName(f.MetadataName);
        if (symbol is null || known.Component is null) return null;

        var props = new List<ComponentPropDeclaration>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddProps(symbol, props, names);
        bool baseHasGlue = false;
        for (var b = symbol.BaseType; b is not null && !SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, known.Component); b = b.BaseType)
        {
            if (HasGlue(b, awauiTypes, ct))
            {
                baseHasGlue = true;
                break;
            }
            AddProps(b, props, names);
        }
        var declared = DeclaredGlue(symbol);

        // @bind 目标：在骨架的探针方法体内推测绑定，类型为 State<T> 的按 State 展开
        var stateTargets = new List<string>();
        if (f.BindTargets.Count > 0 && known.State is not null)
        {
            var model = c.GetSemanticModel(skeleton);
            var probe = skeleton.GetRoot(ct).DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == ProbeMethod);
            if (probe?.Body is { } body)
            {
                int position = body.OpenBraceToken.Span.End;
                foreach (var target in f.BindTargets)
                {
                    var expr = SyntaxFactory.ParseExpression(target);
                    var type = model.GetSpeculativeTypeInfo(position, expr, SpeculativeBindingOption.BindAsExpression).Type;
                    if (type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, known.State))
                        stateTargets.Add(target);
                }
            }
        }

        return new ComponentClassInfo(props, baseHasGlue,
            (declared & 1) != 0, (declared & 2) != 0, (declared & 4) != 0, stateTargets);
    }

    /// <summary>与 C# 胶水生成器相同的 Props 规则（design/12）：public、非 static、非索引器、public get 加 public set/init、不是 ref 类结构、名字不是 Key。</summary>
    static void AddProps(INamedTypeSymbol type, List<ComponentPropDeclaration> props, HashSet<string> names)
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
            if (!names.Add(p.Name)) continue;
            props.Add(new ComponentPropDeclaration(p.Name, p.Type.ToDisplayString(TypeFormat), set.IsInitOnly));
        }
    }

    /// <summary>位标志：1 = PropsEqual、2 = AdoptProps、4 = CanAdoptProps。</summary>
    static int DeclaredGlue(INamedTypeSymbol t)
    {
        int result = 0;
        foreach (var m in t.GetMembers())
        {
            if (m is not IMethodSymbol { IsOverride: true } method) continue;
            switch (method.Name)
            {
                case "PropsEqual": result |= 1; break;
                case "AdoptProps": result |= 2; break;
                case "CanAdoptProps": result |= 4; break;
            }
        }
        return result;
    }

    /// <summary>基类有胶水：手写了胶水方法、是本项目的 .awaui 组件，或是源码中的 partial 类（C# 胶水生成器会处理它）。</summary>
    static bool HasGlue(INamedTypeSymbol b, HashSet<string> awauiTypes, CancellationToken ct)
    {
        var def = b.OriginalDefinition;
        if (DeclaredGlue(def) != 0) return true;
        if (awauiTypes.Contains(MetadataName(def))) return true;
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is ClassDeclarationSyntax cls && cls.Modifiers.Any(SyntaxKind.PartialKeyword))
                return true;
        }
        return false;
    }

    static string MetadataName(INamedTypeSymbol t)
    {
        var ns = t.ContainingNamespace;
        string prefix = ns is null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString() + ".";
        return prefix + t.MetadataName;
    }

    static bool Is(ITypeSymbol type, INamedTypeSymbol? target) =>
        target is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target);

    static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, baseType)) return true;
        return false;
    }

    static bool DerivesFromOrIs(INamedTypeSymbol type, INamedTypeSymbol baseType) =>
        SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, baseType) || DerivesFrom(type, baseType);
}
