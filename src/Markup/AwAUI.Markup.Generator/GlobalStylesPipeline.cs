using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynSourceText = Microsoft.CodeAnalysis.Text.SourceText;

namespace AwAUI.Markup.Generator;

/// <summary>
/// 项目级共享样式（<c>&lt;AwAUIGlobalStylesheet Include="Styles/app.css" /&gt;</c>）：把若干张全局 CSS 合并成一个
/// <c>AwAUIGlobalStyles</c> 类，模块初始化时登记到 <see cref="AwAUI.UI.AppStyleSheets"/>，所有组件共享。
/// 这是 Blazor <c>wwwroot/app.css</c> 的对应物；组件自己的 <c>X.awaui.css</c> 走作用域路径（<see cref="MarkupSourceGenerator"/>）。
/// </summary>
internal static class GlobalStylesPipeline
{
    internal const string MetadataName = "AwAUIGlobalStylesheet";

    internal static void Register(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(static pair =>
            {
                var options = pair.Right.GetOptions(pair.Left);
                return options.TryGetValue("build_metadata.AdditionalFiles." + MetadataName, out var value)
                    && string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            })
            .Select(static (pair, ct) => new SharedStyle(pair.Left.Path.Replace('\\', '/'), pair.Left.GetText(ct)?.ToString() ?? ""))
            .Collect();

        var settings = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? ""))
            .Select(static (pair, _) => ReadSettings(pair.Left.GlobalOptions, pair.Right));

        var compilationInfo = context.CompilationProvider
            .Combine(context.ParseOptionsProvider)
            .Select(static (pair, _) => ReadCompilationInfo(pair.Left, pair.Right));

        var input = files.Combine(settings).Combine(compilationInfo)
            .Select(static (pair, _) => new Input(pair.Left.Left, pair.Left.Right, pair.Right));

        context.RegisterSourceOutput(input, static (spc, input) => Produce(spc, input));
    }

    sealed class SharedStyle
    {
        public SharedStyle(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }
        public string Text { get; }
    }

    sealed class Settings
    {
        public Settings(string rootNamespace, bool register)
        {
            RootNamespace = rootNamespace;
            Register = register;
        }

        public string RootNamespace { get; }
        public bool Register { get; }
    }

    sealed class CompilationInfo
    {
        public CompilationInfo(bool hasAppStyleSheets, bool moduleInitializerAvailable, bool languageSupportsModuleInitializer)
        {
            HasAppStyleSheets = hasAppStyleSheets;
            ModuleInitializerAvailable = moduleInitializerAvailable;
            LanguageSupportsModuleInitializer = languageSupportsModuleInitializer;
        }

        public bool HasAppStyleSheets { get; }
        public bool ModuleInitializerAvailable { get; }
        public bool LanguageSupportsModuleInitializer { get; }
    }

    sealed class Input
    {
        public Input(ImmutableArray<SharedStyle> files, Settings settings, CompilationInfo info)
        {
            Files = files;
            Settings = settings;
            Info = info;
        }

        public ImmutableArray<SharedStyle> Files { get; }
        public Settings Settings { get; }
        public CompilationInfo Info { get; }
    }

    static Settings ReadSettings(AnalyzerConfigOptions options, string assemblyName)
    {
        options.TryGetValue("build_property.RootNamespace", out var root);
        if (string.IsNullOrWhiteSpace(root)) root = assemblyName;
        string ns = MarkupSourceGenerator.SanitizeNamespace(root!.Trim());
        bool register = !string.Equals(Get(options, "AwAUIGlobalStylesRegister")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        return new Settings(ns.Length > 0 ? ns : "AwAUI.Generated", register);
    }

    static string? Get(AnalyzerConfigOptions options, string property) =>
        options.TryGetValue("build_property." + property, out var value) ? value : null;

    static CompilationInfo ReadCompilationInfo(Compilation compilation, ParseOptions parseOptions)
    {
        bool hasSheets = compilation.GetTypeByMetadataName("AwAUI.UI.AppStyleSheets") is { } type
            && compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);
        var attr = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute");
        bool attrAvailable = attr is not null && compilation.IsSymbolAccessibleWithin(attr, compilation.Assembly);
        bool language = parseOptions is CSharpParseOptions p && p.LanguageVersion >= LanguageVersion.CSharp9;
        return new CompilationInfo(hasSheets, attrAvailable, language);
    }

    static void Produce(SourceProductionContext spc, Input input)
    {
        if (input.Files.IsDefaultOrEmpty) return;
        var files = new List<SharedStyle>(input.Files);
        // 附加文件的顺序不保证与 MSBuild 项一致：按路径排序，构建结果稳定
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var css = new StringBuilder();
        foreach (var file in files)
        {
            if (css.Length > 0) css.Append('\n');
            css.Append(file.Text);
        }

        var settings = input.Settings;
        var info = input.Info;
        var sb = new StringBuilder(css.Length + 1024);
        sb.Append("// <auto-generated/>\n");
        sb.Append("// AwAUI.Markup：项目级共享样式（<AwAUIGlobalStylesheet>）。\n");
        sb.Append("#nullable enable\n");
        sb.Append("namespace ").Append(settings.RootNamespace).Append("\n{\n");
        sb.Append("    /// <summary>项目级共享样式表：所有组件共用，模块初始化时登记到 <c>AppStyleSheets</c>。</summary>\n");
        sb.Append("    [global::System.CodeDom.Compiler.GeneratedCode(\"AwAUI.Markup\", \"1.0\")]\n");
        sb.Append("    internal static partial class AwAUIGlobalStyles\n    {\n");
        sb.Append("        /// <summary>来源名（诊断与 DevTools 显示，也是 <c>AppStyleSheets</c> 的登记键）。</summary>\n");
        sb.Append("        public const string SourceName = \"global\";\n\n");
        sb.Append("        /// <summary>合并后的共享样式表。</summary>\n");
        sb.Append("        public const string Css = ").Append(TailwindIntegration.CSharpLiterals.Verbatim(css.ToString())).Append(";\n");
        if (info.HasAppStyleSheets)
        {
            bool init = settings.Register && info.LanguageSupportsModuleInitializer;
            sb.Append("\n        /// <summary>\n");
            sb.Append("        /// 登记共享样式（重复调用无害）。");
            sb.Append(init ? "模块初始化时已自动调用。\n" : "未启用自动登记（AwAUIGlobalStylesRegister=false 或 C# 9 以下），由应用调用。\n");
            sb.Append("        /// </summary>\n");
            if (init) sb.Append("        [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
            sb.Append("        public static void Register() => global::AwAUI.UI.AppStyleSheets.Register(Css, SourceName);\n");
        }
        sb.Append("    }\n}\n");
        spc.AddSource("AwAUI.GlobalStyles.g.cs", RoslynSourceText.From(sb.ToString(), Encoding.UTF8));

        if (info.HasAppStyleSheets && settings.Register && info.LanguageSupportsModuleInitializer && !info.ModuleInitializerAvailable)
            spc.AddSource("AwAUI.ModuleInitializerAttribute.g.cs",
                RoslynSourceText.From(TailwindIntegration.TailwindStylesEmitter.ModuleInitializerPolyfill, Encoding.UTF8));
    }
}
