using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Compilation;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Theming;

namespace AwAUI.Tailwind;

/// <summary><c>@source "pattern"</c> 声明的扫描路径（由构建集成解释，引擎本身不访问文件系统）。</summary>
public sealed class TailwindSource
{
    internal TailwindSource(string baseDirectory, string pattern, bool negated)
    {
        BaseDirectory = baseDirectory;
        Pattern = pattern;
        Negated = negated;
    }

    /// <summary>声明所在样式表的目录。</summary>
    public string BaseDirectory { get; }

    /// <summary>glob 模式（相对 <see cref="BaseDirectory"/>）。</summary>
    public string Pattern { get; }

    /// <summary><c>@source not "..."</c>：排除。</summary>
    public bool Negated { get; }
}

/// <summary>增量生成的一条工具类规则。</summary>
public sealed class TailwindGeneratedRule
{
    internal TailwindGeneratedRule(string candidate, string css, int index)
    {
        Candidate = candidate;
        Css = css;
        Index = index;
    }

    /// <summary>产生该规则的类名。</summary>
    public string Candidate { get; }

    /// <summary>规则的 CSS 文本（已展开嵌套；不含 <c>@layer utilities</c> 包裹）。</summary>
    public string Css { get; }

    /// <summary>
    /// 在当前全部工具规则（每个候选的每条规则各占一项，按 v4 顺序排序）中的位置，运行时据此插入以保持层叠顺序。
    /// 注意：<see cref="TailwindCompiler.Build"/> 的输出会合并相邻的同条件 at 规则（如多个 <c>hover:</c> 规则共用一个
    /// <c>@media (hover: hover)</c>），其中的规则数与这里的下标不对应；运行时应维护由增量结果组成的规则列表
    /// （先 <c>Build([])</c> 输出主题与基础样式，再用 <see cref="TailwindCompiler.AddCandidates"/> 加入构建期候选得到基线规则）。
    /// </summary>
    public int Index { get; }
}

/// <summary><see cref="TailwindCompiler.AddCandidates"/> 的结果：只含此前未输出过的内容。</summary>
public sealed class TailwindDelta
{
    internal TailwindDelta(IReadOnlyList<TailwindGeneratedRule> rules, string themeCss, string globalCss)
    {
        Rules = rules;
        ThemeCss = themeCss;
        GlobalCss = globalCss;
    }

    /// <summary>新的工具规则，按 v4 顺序排列。</summary>
    public IReadOnlyList<TailwindGeneratedRule> Rules { get; }

    /// <summary>新用到的主题变量（<see cref="TailwindOptions.ThemeRootSelector"/> 规则，属于 <c>@layer theme</c>）；没有时为空串。</summary>
    public string ThemeCss { get; }

    /// <summary>新的 <c>@property</c> 与 <c>@keyframes</c>（不分层）；没有时为空串。</summary>
    public string GlobalCss { get; }

    /// <summary>是否没有任何新内容。</summary>
    public bool IsEmpty => Rules.Count == 0 && ThemeCss.Length == 0 && GlobalCss.Length == 0;
}

/// <summary>
/// Tailwind CSS v4 兼容的工具类编译器：解析输入样式表（<c>@import "tailwindcss"</c>、<c>@theme</c>、<c>@utility</c>、<c>@custom-variant</c> ...），
/// 再把候选类名编译成 CSS 文本。输出与 Tailwind CSS v4.3.5 一致（规则顺序、选择器转义、声明顺序）。
/// </summary>
/// <remarks>
/// <para>线程安全：所有公开成员内部加锁，可以从多个线程调用；同一实例上的调用串行执行。</para>
/// <para>构建期用 <see cref="Build"/> 生成完整样式表。运行时 JIT 先调用一次 <c>Build([])</c>（主题、preflight 与不依赖候选的样式），
/// 再用 <see cref="AddCandidates"/> 加入构建期的候选得到逐条的基线规则，之后对新类名调用 <see cref="AddCandidates"/> 只得到增量规则
/// （<see cref="TailwindGeneratedRule.Index"/> 指向逐条规则列表中的位置，与 <see cref="Build"/> 合并后的输出不对应）。</para>
/// </remarks>
public sealed class TailwindCompiler
{
    readonly object _gate = new();
    readonly CompiledStylesheet _sheet;
    readonly TailwindOptions _options;
    readonly List<string> _candidates = new();
    readonly HashSet<string> _candidateSet = new(StringComparer.Ordinal);
    readonly HashSet<string> _emittedProperties = new(StringComparer.Ordinal);
    readonly HashSet<string> _emittedThemeVariables = new(StringComparer.Ordinal);
    readonly HashSet<string> _emittedKeyframes = new(StringComparer.Ordinal);

    TailwindCompiler(CompiledStylesheet sheet, TailwindOptions options)
    {
        _sheet = sheet;
        _options = options;
        foreach (var c in sheet.InlineCandidates) AddCandidate(c);
    }

    /// <summary>编译输入样式表。输入错误抛出 <see cref="TailwindException"/>。</summary>
    public static TailwindCompiler Create(string css, TailwindOptions? options = null)
    {
        if (css == null) throw new ArgumentNullException(nameof(css));
        options ??= new TailwindOptions();
        return new TailwindCompiler(StylesheetCompiler.Compile(css, options), options);
    }

    /// <summary>等价于 <c>Create("@import \"tailwindcss\";")</c>：默认主题 + preflight + 工具类。</summary>
    public static TailwindCompiler CreateDefault(TailwindOptions? options = null) => Create("@import \"tailwindcss\";", options);

    /// <summary><c>@source</c> 声明的扫描路径。</summary>
    public IReadOnlyList<TailwindSource> Sources => _sheet.Sources;

    /// <summary>
    /// 主题与工具 / 变体注册表（只供以源码链接方式编入本引擎的构建集成使用，例如判断未知类名是否「像」工具类）。
    /// 不加锁：只应读取创建后不再变化的部分（工具与变体注册表）。
    /// </summary>
    internal DesignSystem Design => _sheet.DesignSystem;

    /// <summary><c>@tailwind utilities</c> 的参数（含 <c>@import "tailwindcss" source(...)</c> 带来的 <c>source(...)</c>）；没有该指令时为 null。</summary>
    internal string? UtilitiesParams => _sheet.UtilitiesNode?.Params;

    /// <summary>当前已加入的候选类名（含 <c>@source inline(...)</c>）。</summary>
    public IReadOnlyList<string> Candidates
    {
        get
        {
            lock (_gate) return _candidates.ToArray();
        }
    }

    /// <summary>把候选加入集合并生成完整样式表（与 Tailwind <c>compile().build()</c> 相同：候选在多次调用之间累积）。</summary>
    public string Build(IEnumerable<string> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        lock (_gate)
        {
            _sheet.DesignSystem.TrimCaches();
            foreach (var c in candidates) AddCandidate(c);
            var ds = _sheet.DesignSystem;
            var compiled = CandidateCompiler.CompileCandidates(_candidates, ds, c => ds.InvalidCandidates.Add(c));
            if (_sheet.UtilitiesNode != null) _sheet.UtilitiesNode.Nodes = compiled.AstNodes;
            var optimized = Optimizer.Optimize(_sheet.Ast, ds, Polyfills.None);
            RecordEmitted(optimized);
            return CssPrinter.Print(optimized, _options.Minify);
        }
    }

    /// <summary>
    /// 增量加入候选：只返回此前（上一次 <see cref="Build"/> 或 <see cref="AddCandidates"/> 之后）没有输出过的规则、主题变量、<c>@property</c> 与 <c>@keyframes</c>。
    /// 运行时 JIT 用它在遇到构建期未生成的类名时补全样式。
    /// </summary>
    public TailwindDelta AddCandidates(IEnumerable<string> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        lock (_gate)
        {
            var ds = _sheet.DesignSystem;
            ds.TrimCaches();
            var fresh = new List<string>();
            foreach (var c in candidates)
            {
                if (_candidateSet.Contains(c) || ds.InvalidCandidates.Contains(c)) continue;
                if (c.Length > 1 && c[0] == '-' && c[1] == '-')
                {
                    ds.Theme.MarkUsedVariable(c);
                    continue;
                }
                var parsed = ds.ParseCandidate(c);
                bool valid = false;
                foreach (var p in parsed)
                {
                    if (ds.CompileAstNodes(p, 1).Count > 0)
                    {
                        valid = true;
                        break;
                    }
                }
                if (!valid)
                {
                    ds.InvalidCandidates.Add(c);
                    continue;
                }
                AddCandidate(c);
                fresh.Add(c);
            }
            if (fresh.Count == 0)
            {
                string onlyTheme = CollectNewThemeVariables(ds);
                return new TailwindDelta(Array.Empty<TailwindGeneratedRule>(), onlyTheme, "");
            }

            var freshSet = new HashSet<string>(fresh, StringComparer.Ordinal);
            var all = CandidateCompiler.CompileCandidates(_candidates, ds);
            var rules = new List<TailwindGeneratedRule>();
            var globals = new List<AstNode>();
            // animation 声明里直接写出的 keyframes 名（@theme inline 的 --animate-* 被内联时不会标记主题变量为已用）
            var animationNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < all.AstNodes.Count; i++)
            {
                var node = all.AstNodes[i];
                string candidate = all.Sorting[node].Candidate;
                if (!freshSet.Contains(candidate)) continue;
                var optimized = Optimizer.Optimize(new List<AstNode> { node }, ds, Polyfills.None);
                var ruleNodes = new List<AstNode>();
                AstNode.Walk(optimized, (n, _) =>
                {
                    if (n.Kind == AstKind.Declaration && n.Property == "animation" && n.Value != null)
                    {
                        foreach (var name in n.Value.Split(s_keyframeSeparators, StringSplitOptions.RemoveEmptyEntries)) animationNames.Add(name);
                    }
                    return WalkAction<AstNode>.Continue;
                });
                foreach (var n in optimized)
                {
                    if (n.Kind == AstKind.AtRule && n.Name == "@property")
                    {
                        if (_emittedProperties.Add(n.Params)) globals.Add(n);
                    }
                    else ruleNodes.Add(n);
                }
                rules.Add(new TailwindGeneratedRule(candidate, CssPrinter.Print(ruleNodes, _options.Minify), i));
            }
            string themeCss = CollectNewThemeVariables(ds);
            foreach (var kf in ds.Theme.GetKeyframes())
            {
                if (_emittedKeyframes.Contains(kf.Params)) continue;
                if (!animationNames.Contains(kf.Params) && !IsKeyframesUsed(kf.Params, ds)) continue;
                _emittedKeyframes.Add(kf.Params);
                globals.Add(kf);
            }
            return new TailwindDelta(rules, themeCss, globals.Count == 0 ? "" : CssPrinter.Print(globals, _options.Minify));
        }
    }

    /// <summary>单个候选的 CSS（不含主题变量与 preflight；含它需要的 <c>@property</c>）；无效类名返回 null。对应 Tailwind 的 <c>candidatesToCss</c>。</summary>
    public string? CandidateToCss(string candidate)
    {
        lock (_gate)
        {
            _sheet.DesignSystem.TrimCaches();
            return _sheet.DesignSystem.CandidatesToCss(new[] { candidate }, _options.Minify)[0];
        }
    }

    /// <summary>批量版本的 <see cref="CandidateToCss"/>。</summary>
    public IReadOnlyList<string?> CandidatesToCss(IReadOnlyList<string> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        lock (_gate)
        {
            _sheet.DesignSystem.TrimCaches();
            return _sheet.DesignSystem.CandidatesToCss(candidates, _options.Minify);
        }
    }

    /// <summary>类名能否生成样式（用于诊断：构建集成对无法识别的类名报告 AW5xxx 警告）。</summary>
    public bool IsValidCandidate(string candidate)
    {
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        lock (_gate)
        {
            var ds = _sheet.DesignSystem;
            ds.TrimCaches();
            if (ds.InvalidCandidates.Contains(candidate)) return false;
            foreach (var p in ds.ParseCandidate(candidate))
            {
                if (ds.CompileAstNodes(p, 1).Count > 0) return true;
            }
            return false;
        }
    }

    void AddCandidate(string c)
    {
        var ds = _sheet.DesignSystem;
        if (ds.InvalidCandidates.Contains(c)) return;
        if (c.Length > 1 && c[0] == '-' && c[1] == '-')
        {
            ds.Theme.MarkUsedVariable(c);
            return;
        }
        if (c.Length == 0 || c.Length > Limits.MaxCandidateLength) return;
        if (_candidateSet.Add(c)) _candidates.Add(c);
    }

    void RecordEmitted(List<AstNode> optimized)
    {
        AstNode.Walk(optimized, (n, _) =>
        {
            if (n.Kind == AstKind.AtRule && n.Name == "@property") _emittedProperties.Add(n.Params);
            if (n.Kind == AstKind.AtRule && n.Name == "@keyframes") _emittedKeyframes.Add(n.Params);
            return WalkAction<AstNode>.Continue;
        });
        foreach (var e in _sheet.DesignSystem.Theme.Entries())
        {
            if ((e.Options & (ThemeOptions.Used | ThemeOptions.Static)) != 0 && (e.Options & ThemeOptions.Reference) == 0) _emittedThemeVariables.Add(e.Key);
        }
    }

    static readonly char[] s_keyframeSeparators = { ' ', '\t', '\n', '\r', '\f', ',' };

    bool IsKeyframesUsed(string name, DesignSystem ds)
    {
        foreach (var e in ds.Theme.Entries())
        {
            if ((e.Options & ThemeOptions.Used) == 0) continue;
            if (!e.Key.StartsWith("--animate-", StringComparison.Ordinal)) continue;
            foreach (var part in e.Value.Split(' ', ','))
            {
                if (part == name) return true;
            }
        }
        return false;
    }

    string CollectNewThemeVariables(DesignSystem ds)
    {
        var theme = ds.Theme;
        // 被新用到的变量引用的主题变量也要输出（例如 --default-font-family: var(--font-sans)）
        var queue = new Queue<ThemeEntry>();
        foreach (var e in theme.Entries())
        {
            if ((e.Options & ThemeOptions.Used) != 0 && (e.Options & ThemeOptions.Reference) == 0 && !_emittedThemeVariables.Contains(e.Key)) queue.Enqueue(e);
        }
        var decls = new List<AstNode>();
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            if (!_emittedThemeVariables.Add(e.Key)) continue;
            decls.Add(AstNode.Decl(Js.Escape(theme.PrefixKey(e.Key)), e.Value));
            foreach (var name in VariableNames.Extract(e.Value))
            {
                var dep = theme.GetEntry(Js.Unescape(name));
                if (dep != null && (dep.Options & ThemeOptions.Reference) == 0 && !_emittedThemeVariables.Contains(dep.Key)) queue.Enqueue(dep);
            }
        }
        if (decls.Count == 0) return "";
        return CssPrinter.Print(new List<AstNode> { AstNode.Rule(_options.ThemeRootSelector, decls) }, _options.Minify);
    }
}
