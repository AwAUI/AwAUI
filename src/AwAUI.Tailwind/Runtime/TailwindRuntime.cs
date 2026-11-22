using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace AwAUI.Tailwind.Runtime;

/// <summary>
/// 运行时 Tailwind 补全（design/23 第 3 层机制 3、6）：用构建期同一份入口样式表创建 <see cref="TailwindCompiler"/>，
/// 先 <c>Build([])</c> 得到主题、preflight 等与候选无关的基线，再用构建期候选建立逐条规则，之后遇到构建期未生成的类名时即时补全。
/// </summary>
/// <remarks>
/// <para>
/// 组合方式：<see cref="Css"/> = <c>Build([])</c> 的基线 + 一个按 <see cref="TailwindGeneratedRule.Index"/> 排序的
/// <c>@layer utilities</c> 块 + 追加的 <c>@layer theme</c> 块（新用到的主题变量）+ 顶层追加的 <c>@property</c> / <c>@keyframes</c>。
/// <see cref="TailwindCompiler.Build"/> 的合并输出不能用来做插入：它把相邻的同条件 at 规则（例如多条 <c>hover:</c> 规则共用一个
/// <c>@media (hover: hover)</c>）合并，<see cref="TailwindGeneratedRule.Index"/> 与合并后输出中的规则数不对应；而
/// <see cref="TailwindCompiler.AddCandidates"/> 的增量结果逐条给出规则与下标，把它们按 <see cref="TailwindGeneratedRule.Index"/> 插入
/// 逐条规则列表即可保持 v4 的工具顺序。追加的 <c>@layer theme</c> 块不会打乱层序：基线里的
/// <c>@layer theme, base, components, utilities</c> 已经确定了层顺序，晚到的主题变量仍然属于 theme 层（在 utilities 之前）。
/// 文本本身增量维护：新的规则只在已有的 <c>@layer utilities</c> 块中按序插入，主题块插在全局块之前，<c>@property</c> / <c>@keyframes</c>
/// 追加在末尾；<see cref="RemoveCandidates"/> 只删除规则文本、保留空槽位，重新加入时在原位恢复。每次变化仍生成一份新的完整字符串，
/// <see cref="Css"/> 通过引用交换原子更新（读取方看到旧文本或新文本，不会看到半份样式表）。
/// </para>
/// <para>
/// 线程安全：所有公开成员内部加锁，可从多个线程调用。未知或无效的类名不会抛出，而是收集到 <see cref="InvalidCandidates"/>
/// （每个只记一次）；<see cref="AddCandidates"/> 返回样式表是否变化并触发 <see cref="Changed"/>。<see cref="TryAddCandidates"/>
/// 用于整树扫描得到的混合类名：未知类名直接忽略，不进入无效列表。
/// </para>
/// <para>
/// <see cref="Enabled"/> 默认只在 Debug 构建开启（Release 关闭）；关闭时 <see cref="AddCandidates"/> 不做事并返回 false。
/// </para>
/// <para>
/// 入口样式表里的本地 <c>@import</c> 需要 <see cref="TailwindOptions.Resolver"/>（构建期由源生成器读取附加文件，运行时可读取嵌入资源）；
/// 只使用内置包（<c>tailwindcss</c>、<c>tw-animate-css</c>）时不需要。
/// </para>
/// </remarks>
public sealed class TailwindRuntime
{
    const string UtilitiesHead = "@layer utilities {\n";
    const string UtilitiesTail = "}\n";

    readonly object _gate = new();
    readonly TailwindCompiler _compiler;
    readonly string _baseCss;
    readonly List<TailwindGeneratedRule> _rules = new();
    readonly List<string> _themeBlocks = new();
    readonly List<string> _globalBlocks = new();
    readonly HashSet<string> _invalidSet = new(StringComparer.Ordinal);
    readonly List<string> _invalid = new();
    readonly Dictionary<string, List<TailwindGeneratedRule>> _removed = new(StringComparer.Ordinal);

    // 增量组合文本：规则偏移与各段起点只在锁内读写
    readonly StringBuilder _text = new();
    readonly List<int> _ruleOffsets = new();
    int _utilitiesStart = -1;   // 第一条规则在 _text 中的位置；-1 表示还没有 @layer utilities 块
    int _utilitiesEnd = -1;     // utilities 块结束（闭括号前）的位置
    int _themeStart;            // 第一个 @layer theme 块的位置（没有主题块时等于 _globalsStart）
    int _globalsStart;          // 第一个全局块（@property / @keyframes）的位置
    int _dirtyFrom = int.MaxValue; // 本次修改中第一个可能变化的顶层位置
    int _lastChangedFrom;       // 最近一次成功修改的 _dirtyFrom

#if DEBUG
    static volatile bool s_enabled = true;
#else
    static volatile bool s_enabled;
#endif

    string _css;

    /// <summary>
    /// 创建运行时补全。构造时编译一次入口样式表（错误抛出 <see cref="TailwindException"/>），再建立构建期基线。
    /// </summary>
    /// <param name="entryCss">与构建期相同的入口样式表文本。</param>
    /// <param name="baselineCandidates">构建期已经生成样式的类名（生成代码里的 <c>TailwindStyles.Candidates</c>）；可为 null。</param>
    /// <param name="options">编译选项（应与构建期一致，例如 <see cref="TailwindOptions.ThemeRootSelector"/>）。</param>
    public TailwindRuntime(string entryCss, IEnumerable<string>? baselineCandidates = null, TailwindOptions? options = null)
    {
        if (entryCss is null) throw new ArgumentNullException(nameof(entryCss));
        _compiler = TailwindCompiler.Create(entryCss, options);
        _baseCss = _compiler.Build(Array.Empty<string>());
        BuildText();
        if (baselineCandidates is not null)
        {
            var list = new List<string>();
            foreach (var c in baselineCandidates)
                if (!string.IsNullOrEmpty(c)) list.Add(c);
            if (list.Count > 0) Apply(_compiler.AddCandidates(list));
        }
        _css = _text.ToString();
    }

    /// <summary>
    /// 是否启用运行时补全。默认值：Debug 构建为 true、Release 构建为 false；可在应用启动时覆盖。
    /// </summary>
    public static bool Enabled
    {
        get => s_enabled;
        set => s_enabled = value;
    }

    /// <summary>当前完整的样式表文本（原子读取）。可以直接交给 <c>AwAUI.UI.AppStyleSheet.Update</c> 或重新解析。</summary>
    public string Css => Volatile.Read(ref _css);

    /// <summary>无法生成样式的类名（按首次出现的顺序，每个只记一次）；不会抛出异常。</summary>
    public IReadOnlyList<string> InvalidCandidates
    {
        get
        {
            lock (_gate) return _invalid.ToArray();
        }
    }

    /// <summary>
    /// 无法生成样式的类名被首次发现时触发（每个类名只一次）。在锁外触发，参数是运行时与类名。
    /// 应用可以把它们转发为 UI 诊断（代码 <c>AW2001</c>，见 design/12），而不是只轮询 <see cref="InvalidCandidates"/>。
    /// </summary>
    public event Action<TailwindRuntime, string>? InvalidCandidate;

    /// <summary><see cref="Css"/> 发生变化（新增规则、主题变量或 <c>@property</c> / <c>@keyframes</c>）。在锁外触发。</summary>
    public event Action<TailwindRuntime>? Changed;

    /// <summary>
    /// <see cref="Css"/> 发生变化时报告第一个可能变化的顶层规则起点（<see cref="Css"/> 中的绝对偏移）。
    /// 偏移之前的新旧文本逐字符相同，可以直接交给 <c>AppStyleSheet.Update(css, changedAt)</c> 做增量解析。
    /// 规则都在同一个 <c>@layer utilities</c> 块里，所以插入 / 移除规则时偏移就是该块的开头（整块重新解析，基线前缀仍复用）；
    /// 追加主题块时是原全局块起点；追加 <c>@property</c> / <c>@keyframes</c> 时是原文本末尾。在锁外、<see cref="Changed"/> 之后触发。
    /// </summary>
    public event Action<TailwindRuntime, int>? ChangedFrom;

    /// <summary>最近一次变化报告的偏移（<see cref="ChangedFrom"/> 的参数）；从未变化时为 0。</summary>
    public int LastChangeOffset => Volatile.Read(ref _lastChangedFrom);

    /// <summary>
    /// 把当前与之后的 <see cref="Css"/> 交给 <paramref name="apply"/>：立即调用一次，之后每次 <see cref="Changed"/> 再调用。
    /// 典型用法是 <c>runtime.Bind(sheet.Update)</c>，把运行时补全接到 <c>AppStyleSheet</c>；返回的令牌取消订阅。
    /// </summary>
    public IDisposable Bind(Action<string> apply)
    {
        if (apply is null) throw new ArgumentNullException(nameof(apply));
        void OnChanged(TailwindRuntime runtime) => apply(runtime.Css);
        Changed += OnChanged;
        apply(Css);
        return new Unbind(this, OnChanged);
    }

    /// <summary>
    /// 与 <see cref="Bind"/> 相同，但同时给出变化的顶层起点：典型用法是 <c>runtime.BindIncremental(sheet.Update)</c>，
    /// 把 <see cref="Css"/> 与 <see cref="ChangedFrom"/> 的偏移一起交给 <c>AppStyleSheet.Update(string, int)</c>，只重新解析变化后的尾部。
    /// 首次调用使用偏移 0（整段解析）。返回的令牌取消订阅。
    /// </summary>
    public IDisposable BindIncremental(Action<string, int> apply)
    {
        if (apply is null) throw new ArgumentNullException(nameof(apply));
        void OnChanged(TailwindRuntime runtime) => apply(runtime.Css, runtime.LastChangeOffset);
        Changed += OnChanged;
        apply(Css, 0);
        return new Unbind(this, OnChanged);
    }

    sealed class Unbind : IDisposable
    {
        TailwindRuntime? _runtime;
        readonly Action<TailwindRuntime> _handler;

        public Unbind(TailwindRuntime runtime, Action<TailwindRuntime> handler)
        {
            _runtime = runtime;
            _handler = handler;
        }

        public void Dispose()
        {
            var runtime = Interlocked.Exchange(ref _runtime, null);
            if (runtime is not null) runtime.Changed -= _handler;
        }
    }

    /// <summary>
    /// 加入候选类名：只为新出现的类名生成样式。返回样式表是否发生变化；变化时更新 <see cref="Css"/> 并触发 <see cref="Changed"/>。
    /// 未知或无效的类名收集到 <see cref="InvalidCandidates"/> 并触发 <see cref="InvalidCandidate"/>，不抛出。关闭 <see cref="Enabled"/> 时直接返回 false。
    /// </summary>
    public bool AddCandidates(IEnumerable<string> candidates) => AddCandidates(candidates, reportInvalid: true);

    /// <summary>
    /// 与 <see cref="AddCandidates"/> 相同，但未知类名直接忽略、不记入 <see cref="InvalidCandidates"/>。
    /// 适合把扫描整棵元素树得到的混合类名（其中包含应用自定义的非 Tailwind 类）喂进来，见 <c>UIDevToolsOptions.ClassCandidates</c>。
    /// </summary>
    public bool TryAddCandidates(IEnumerable<string> candidates) => AddCandidates(candidates, reportInvalid: false);

    /// <summary>
    /// 移除候选类名对应的工具规则（元素树里不再出现的类名）。被移除规则按候选缓存，之后再次
    /// <see cref="AddCandidates"/> 同一个类名会原位恢复，不需要重新编译。返回样式表是否发生变化。
    /// <para>
    /// 只撤回工具规则：已经追加的主题变量与 <c>@property</c> / <c>@keyframes</c> 保持不动（它们是共享的，
    /// 可能还被其他类使用，而且按 v4 的层序必须留在原处）。从未加入过或无效的类名不做事。
    /// </para>
    /// </summary>
    public bool RemoveCandidates(IEnumerable<string> candidates)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (!s_enabled) return false;

        bool changed = false;
        int from = 0;
        lock (_gate)
        {
            _dirtyFrom = int.MaxValue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !seen.Add(c) || _removed.ContainsKey(c)) continue;
                var slots = new List<int>();
                for (int i = 0; i < _rules.Count; i++)
                    if (_rules[i].Css.Length > 0 && string.Equals(_rules[i].Candidate, c, StringComparison.Ordinal))
                        slots.Add(i);
                if (slots.Count == 0) continue;
                var cached = new List<TailwindGeneratedRule>(slots.Count);
                foreach (int slot in slots) cached.Add(_rules[slot]);
                // 从后往前删除文本段，前面规则的下标不受影响；规则槽位保留，恢复时回到原位
                for (int j = slots.Count - 1; j >= 0; j--) DeactivateRuleAt(slots[j]);
                _removed[c] = cached;
                changed = true;
            }
            if (changed)
            {
                from = _dirtyFrom == int.MaxValue ? 0 : _dirtyFrom;
                Volatile.Write(ref _lastChangedFrom, from);
                Volatile.Write(ref _css, _text.ToString());
            }
        }
        if (changed)
        {
            Changed?.Invoke(this);
            ChangedFrom?.Invoke(this, from);
        }
        return changed;
    }

    bool AddCandidates(IEnumerable<string> candidates, bool reportInvalid)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (!s_enabled) return false;

        bool changed = false;
        int from = 0;
        List<string>? added = null;
        lock (_gate)
        {
            _dirtyFrom = int.MaxValue;
            var fresh = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !seen.Add(c)) continue;
                // --name：主题变量的引用，由编译器标记为已用；不是工具类，不按候选判断有效性
                if (c.Length > 1 && c[0] == '-' && c[1] == '-')
                {
                    fresh.Add(c);
                    continue;
                }
                if (_invalidSet.Contains(c)) continue;
                if (!_compiler.IsValidCandidate(c))
                {
                    if (!reportInvalid) continue;
                    _invalidSet.Add(c);
                    _invalid.Add(c);
                    (added ??= new List<string>()).Add(c);
                    continue;
                }
                fresh.Add(c);
            }
            if (fresh.Count > 0)
            {
                bool applied = false;
                // 曾被移除的类名用缓存的规则恢复；其余交给编译器生成增量
                var toCompile = new List<string>(fresh.Count);
                foreach (var c in fresh)
                {
                    if (!_removed.TryGetValue(c, out var cached))
                    {
                        toCompile.Add(c);
                        continue;
                    }
                    _removed.Remove(c);
                    RestoreCandidate(c, cached);
                    applied = true;
                }
                if (toCompile.Count > 0)
                {
                    var delta = _compiler.AddCandidates(toCompile);
                    if (!delta.IsEmpty)
                    {
                        Apply(delta);
                        applied = true;
                    }
                }
                if (applied)
                {
                    from = _dirtyFrom == int.MaxValue ? 0 : _dirtyFrom;
                    Volatile.Write(ref _lastChangedFrom, from);
                    Volatile.Write(ref _css, _text.ToString());
                    changed = true;
                }
            }
        }
        if (added is not null)
            foreach (var candidate in added) InvalidCandidate?.Invoke(this, candidate);
        if (changed)
        {
            Changed?.Invoke(this);
            ChangedFrom?.Invoke(this, from);
        }
        return changed;
    }

    void Apply(TailwindDelta delta)
    {
        foreach (var rule in delta.Rules)
        {
            int index = rule.Index;
            if (index < 0) index = 0;
            else if (index > _rules.Count) index = _rules.Count;
            InsertRule(index, rule);
        }
        if (delta.ThemeCss.Length > 0)
        {
            string block = ThemeBlock(delta.ThemeCss);
            MarkDirty(_globalsStart);
            _text.Insert(_globalsStart, block);
            _globalsStart += block.Length;
            _themeBlocks.Add(delta.ThemeCss);
        }
        if (delta.GlobalCss.Length > 0)
        {
            string block = GlobalBlock(delta.GlobalCss);
            MarkDirty(_text.Length);
            _text.Append(block);
            _globalBlocks.Add(delta.GlobalCss);
        }
    }

    /// <summary>记录本次修改中可能变化的最早顶层位置（只在小值上更新）。</summary>
    void MarkDirty(int offset)
    {
        if (offset >= 0 && offset < _dirtyFrom) _dirtyFrom = offset;
    }

    /// <summary><c>@layer utilities</c> 块的开头；还没有该块时等于主题 / 全局区起点。</summary>
    int UtilitiesBlockStart => _utilitiesStart < 0 ? _themeStart : _utilitiesStart - UtilitiesHead.Length;

    /// <summary>把一条规则插到逐条规则列表的 <paramref name="index"/> 处，同时更新组合文本与偏移。</summary>
    void InsertRule(int index, TailwindGeneratedRule rule)
    {
        string css = rule.Css;
        if (_utilitiesStart < 0)
        {
            // 还没有 @layer utilities 块（基线没有任何候选）：在主题 / 全局块之前建一个。
            // 这时列表必然为空，index 已被钳到 0。
            int at = _themeStart;
            MarkDirty(at);
            _text.Insert(at, UtilitiesHead + css + UtilitiesTail);
            _utilitiesStart = at + UtilitiesHead.Length;
            _utilitiesEnd = _utilitiesStart + css.Length;
            int shift = UtilitiesHead.Length + css.Length + UtilitiesTail.Length;
            _themeStart += shift;
            _globalsStart += shift;
            _ruleOffsets.Add(_utilitiesStart);
            _rules.Insert(index, rule);
            return;
        }

        MarkDirty(UtilitiesBlockStart);
        int offset = index >= _ruleOffsets.Count ? _utilitiesEnd : _ruleOffsets[index];
        _text.Insert(offset, css);
        _utilitiesEnd += css.Length;
        _themeStart += css.Length;
        _globalsStart += css.Length;
        for (int i = index; i < _ruleOffsets.Count; i++) _ruleOffsets[i] += css.Length;
        _ruleOffsets.Insert(index, offset);
        _rules.Insert(index, rule);
    }

    /// <summary>把候选的规则文本放回它原来占据的空槽位（顺序因此与移除前完全一致）。</summary>
    void RestoreCandidate(string candidate, List<TailwindGeneratedRule> cached)
    {
        int k = 0;
        for (int i = 0; i < _rules.Count && k < cached.Count; i++)
        {
            if (_rules[i].Css.Length != 0 || !string.Equals(_rules[i].Candidate, candidate, StringComparison.Ordinal))
                continue;
            RestoreRuleAt(i, cached[k++]);
        }
    }

    /// <summary>撤回一条规则的文本，但保留它在规则列表里的槽位（<see cref="TailwindGeneratedRule.Css"/> 变为空串）。</summary>
    void DeactivateRuleAt(int index)
    {
        MarkDirty(UtilitiesBlockStart);
        var rule = _rules[index];
        int offset = _ruleOffsets[index];
        int length = rule.Css.Length;
        _text.Remove(offset, length);
        _utilitiesEnd -= length;
        _themeStart -= length;
        _globalsStart -= length;
        for (int i = index + 1; i < _ruleOffsets.Count; i++) _ruleOffsets[i] -= length;
        _ruleOffsets[index] = offset;
        _rules[index] = new TailwindGeneratedRule(rule.Candidate, "", rule.Index);
    }

    /// <summary>把规则文本插回空槽位，并更新后续偏移。</summary>
    void RestoreRuleAt(int index, TailwindGeneratedRule rule)
    {
        MarkDirty(UtilitiesBlockStart);
        int offset = _ruleOffsets[index];
        int length = rule.Css.Length;
        _text.Insert(offset, rule.Css);
        _utilitiesEnd += length;
        _themeStart += length;
        _globalsStart += length;
        for (int i = index + 1; i < _ruleOffsets.Count; i++) _ruleOffsets[i] += length;
        _rules[index] = rule;
    }

    /// <summary>从当前规则与块列表重新拼出整份文本（偏移的参考实现，测试用它校验增量组合）。</summary>
    internal string Recompose()
    {
        lock (_gate)
        {
            var sb = new StringBuilder(_baseCss.Length + _rules.Count * 96 + 256);
            sb.Append(_baseCss);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            if (_rules.Count > 0)
            {
                sb.Append("@layer utilities {\n");
                foreach (var rule in _rules) sb.Append(rule.Css);
                sb.Append("}\n");
            }
            foreach (var theme in _themeBlocks) sb.Append(ThemeBlock(theme));
            foreach (var global in _globalBlocks) sb.Append(GlobalBlock(global));
            return sb.ToString();
        }
    }

    /// <summary>初始文本（基线 + 空的主题 / 全局区），并记录各段起点。</summary>
    void BuildText()
    {
        _text.Clear();
        _text.Append(_baseCss);
        if (_text.Length > 0 && _text[_text.Length - 1] != '\n') _text.Append('\n');
        _utilitiesStart = _utilitiesEnd = -1;
        _ruleOffsets.Clear();
        if (_rules.Count > 0)
        {
            _text.Append("@layer utilities {\n");
            _utilitiesStart = _text.Length;
            foreach (var rule in _rules)
            {
                _ruleOffsets.Add(_text.Length);
                _text.Append(rule.Css);
            }
            _utilitiesEnd = _text.Length;
            _text.Append("}\n");
        }
        _themeStart = _text.Length;
        foreach (var theme in _themeBlocks) _text.Append(ThemeBlock(theme));
        _globalsStart = _text.Length;
        foreach (var global in _globalBlocks) _text.Append(GlobalBlock(global));
    }

    static string ThemeBlock(string theme)
    {
        var sb = new StringBuilder(theme.Length + 32);
        sb.Append("@layer theme {\n").Append(theme);
        if (theme.Length > 0 && theme[theme.Length - 1] != '\n') sb.Append('\n');
        sb.Append("}\n");
        return sb.ToString();
    }

    static string GlobalBlock(string global)
    {
        var sb = new StringBuilder(global.Length + 2).Append(global);
        if (global.Length > 0 && global[global.Length - 1] != '\n') sb.Append('\n');
        return sb.ToString();
    }
}
