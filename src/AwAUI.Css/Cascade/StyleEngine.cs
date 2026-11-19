using System;
using System.Collections.Generic;
using AwAUI.Css.Media;
using AwAUI.Css.Selectors;
using AwAUI.Css.Syntax;
using AwAUI.Css.Values;

namespace AwAUI.Css.Cascade;

/// <summary>
/// 级联引擎（design/08「级联」）：配置一组有序的样式表（各带来源）与环境，为元素解析计算样式。
/// <para>
/// 排序（CSS Cascade 5，从低到高）：普通 UA → 普通作者（层按声明顺序，未分层最后）→ 普通内联 → 动画（空槽）→
/// <c>!important</c> 作者（层逆序，未分层最先）→ <c>!important</c> 内联 → <c>!important</c> UA → 过渡（空槽）→
/// DevTools 覆盖（普通、<c>!important</c>）。同一层级内依次比较特异性与出现顺序。
/// </para>
/// <para>
/// 规则按最右侧复合选择器分桶（<see cref="SelectorIndex{T}"/>），声明在加入样式表时解析一次并缓存。
/// 解析过程复用内部缓冲，实例不是线程安全的。
/// </para>
/// </summary>
public sealed class StyleEngine
{
    // 级联层级（数值越大优先级越高）。3 = 动画、7 = 过渡：第 2 期由动画引擎以独立的声明块填入这两个槽位
    const int LevelUserAgent = 0, LevelAuthor = 1, LevelInline = 2, LevelAuthorImportant = 4, LevelInlineImportant = 5,
        LevelUserAgentImportant = 6, LevelDevTools = 8, LevelDevToolsImportant = 9;
#pragma warning disable IDE0051 // 预留的槽位
    const int LevelAnimation = 3, LevelTransition = 7;
#pragma warning restore IDE0051

    readonly List<CascadeStyleSheet> _sheets = new();
    readonly List<MediaGate> _gates = new();
    readonly LayerNode[] _layerRoots = new LayerNode[3];
    SelectorIndex<CompiledRule> _index = new();
    bool _dirty = true;
    int _mediaGeneration;

    // 每次解析复用的缓冲
    readonly List<SelectorIndex<CompiledRule>.Entry> _entries = new();
    readonly List<Block> _blocks = new();
    readonly Slot[] _slots = new Slot[CssProperties.LonghandCount];
    readonly List<int> _touched = new();
    readonly Dictionary<string, Slot> _customSlots = new(StringComparer.Ordinal);
    readonly StyleComputer _computer;
    readonly VariableResolver _vars = new();
    readonly StyleBuilder _builder = new();
    readonly List<PropertyValue> _pendingValues = new();
    readonly HashSet<MatchedRule> _recordedRules = new(); // MatchedRule 按引用比较
    PendingSubstitutionValue? _pendingKey;
    int _explicitBorderWidths;
    bool _inheritsNonInherited;
    bool _pendingValid;

    public StyleEngine(CssEnvironment? environment = null)
    {
        Environment = environment ?? new CssEnvironment();
        _computer = new StyleComputer(Environment);
    }

    /// <summary>媒体查询、视口单位与 env() 所用的环境。修改后调用 <see cref="EnvironmentChanged"/>。</summary>
    public CssEnvironment Environment { get; }

    /// <summary>已注册的样式表，按级联中的出现顺序。</summary>
    public IReadOnlyList<CascadeStyleSheet> StyleSheets => _sheets;

    /// <summary>
    /// 追加一个样式表。同一来源内，后加入的样式表中的规则出现顺序更晚。值层诊断见返回对象的 <see cref="CascadeStyleSheet.Diagnostics"/>。
    /// </summary>
    public CascadeStyleSheet AddStyleSheet(StyleSheet sheet, CascadeOrigin origin = CascadeOrigin.Author)
    {
        if (sheet is null) throw new ArgumentNullException(nameof(sheet));
        var cs = new CascadeStyleSheet(sheet, origin);
        _sheets.Add(cs);
        ParseDeclarations(cs);
        _dirty = true;
        return cs;
    }

    /// <summary>移除样式表（DevTools 编辑、热重载时先移除旧表再加入新表）。</summary>
    public bool RemoveStyleSheet(CascadeStyleSheet sheet)
    {
        if (!_sheets.Remove(sheet)) return false;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 原位替换一个样式表（DevTools 编辑、热重载）：新样式表沿用旧样式表的来源与在级联中的位置（出现顺序不变）。
    /// <paramref name="sheet"/> 未注册时返回 null。
    /// </summary>
    public CascadeStyleSheet? ReplaceStyleSheet(CascadeStyleSheet sheet, StyleSheet replacement)
    {
        if (sheet is null) throw new ArgumentNullException(nameof(sheet));
        if (replacement is null) throw new ArgumentNullException(nameof(replacement));
        int i = _sheets.IndexOf(sheet);
        if (i < 0) return null;
        var cs = new CascadeStyleSheet(replacement, sheet.Origin);
        _sheets[i] = cs;
        ParseDeclarations(cs);
        _dirty = true;
        return cs;
    }

    /// <summary>
    /// 通知环境（视口、媒体特性、安全区）已变化：重新求值全部 @media 条件。
    /// 返回是否有 @media 条件的结果改变（改变时调用方应重算全部样式）；
    /// 视口单位与 env() 的依赖见 <see cref="ComputedStyle.Flags"/>。
    /// </summary>
    public bool EnvironmentChanged()
    {
        // 尚未编译（新加入/移除了样式表，或上次环境变化改变了层的排序）：编译本身就按当前环境求值，
        // 无从与旧结果比较，保守地报告「已变化」
        if (_dirty)
        {
            EnsureCompiled();
            return true;
        }
        _mediaGeneration++;
        bool changed = false;
        foreach (var g in _gates)
        {
            bool old = g.Value;
            if (g.Evaluate(_mediaGeneration, Environment.Media) != old)
            {
                changed = true;
                // 条件中含 @layer：层的排序随条件变化，下次解析前重新编译
                if (g.AffectsLayerOrder) _dirty = true;
            }
        }
        return changed;
    }

    // ================= 编译 =================

    void ParseDeclarations(CascadeStyleSheet cs)
    {
        if (cs.Parsed) return;
        cs.Parsed = true;
        foreach (var rule in cs.Sheet.EnumerateRules())
        {
            IReadOnlyList<CssDeclaration>? decls = rule switch
            {
                CssStyleRule s => s.Declarations,
                CssNestedDeclarationsRule n => n.Declarations,
                _ => null,
            };
            if (decls is null || decls.Count == 0) continue;
            cs.Blocks[rule] = PropertyDeclarationBlock.Parse(decls, cs._diagnostics);
        }
    }

    void EnsureCompiled()
    {
        if (!_dirty) return;
        _dirty = false;
        _index = new SelectorIndex<CompiledRule>();
        _gates.Clear();
        for (int i = 0; i < _layerRoots.Length; i++) _layerRoots[i] = new LayerNode(null, null);
        // 编译时就要知道 @media 是否成立：不成立的条件中的 @layer 不参与层排序（CSS Cascade 5 §6.4.3）
        _mediaGeneration++;
        int order = 0;
        foreach (var sheet in _sheets)
        {
            var root = _layerRoots[(int)sheet.Origin];
            CompileRules(sheet.Sheet.Rules, sheet, root, null, ref order);
        }
        foreach (var root in _layerRoots) root.AssignRanks();
    }

    void CompileRules(IReadOnlyList<CssRule> rules, CascadeStyleSheet sheet, LayerNode layer, MediaGate? media, ref int order)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case CssStyleRule s:
                    AddRule(s, s.Selectors, sheet, layer, media, ref order);
                    if (s.ChildRules.Count > 0) CompileRules(s.ChildRules, sheet, layer, media, ref order);
                    break;
                case CssNestedDeclarationsRule n:
                    AddRule(n, n.Selectors, sheet, layer, media, ref order);
                    break;
                case CssMediaRule m:
                {
                    var gate = new MediaGate(m.Media, media);
                    _gates.Add(gate);
                    CompileRules(m.Rules, sheet, layer, gate, ref order);
                    break;
                }
                case CssLayerBlockRule l:
                {
                    // 条件不成立时层不被声明：其中的规则挂在不参与排序的游离节点上（反正不会命中），
                    // 条件变化时 EnvironmentChanged 会触发重新编译
                    bool active = IsActive(media);
                    var node = !active ? layer.CreateDetached(l.Name)
                        : l.Name is null ? layer.CreateAnonymous() : layer.GetOrCreate(l.Name);
                    CompileRules(l.Rules, sheet, node, media, ref order);
                    break;
                }
                case CssLayerStatementRule ls:
                    if (IsActive(media)) foreach (var name in ls.Names) layer.GetOrCreate(name);
                    break;
                case CssImportRule imp when imp.HasLayer:
                    // 导入的样式表由上层加载；这里只按规范在此处登记层的顺序
                    if (!IsActive(media)) break;
                    if (imp.LayerName is null) layer.CreateAnonymous();
                    else layer.GetOrCreate(imp.LayerName);
                    break;
            }
        }
    }

    /// <summary>当前 @media 条件是否成立；同时标记该条件链影响层排序。</summary>
    bool IsActive(MediaGate? media)
    {
        if (media is null) return true;
        for (var g = media; g is not null; g = g.Parent) g.AffectsLayerOrder = true;
        return media.Evaluate(_mediaGeneration, Environment.Media);
    }

    void AddRule(CssRule rule, SelectorList selectors, CascadeStyleSheet sheet, LayerNode layer, MediaGate? media, ref int order)
    {
        if (!sheet.Blocks.TryGetValue(rule, out var block) || block.IsEmpty)
        {
            // 只有无效声明的规则不参与级联，但 DevTools 仍需要看到它们：保留空块规则
            if (block is null || block.RejectedDeclarations.Count == 0) return;
        }
        var compiled = new CompiledRule(rule, selectors, block, sheet, layer, media) { Order = order++ };
        _index.Add(selectors, compiled);
    }

    // ================= 解析 =================

    struct Block
    {
        public ParsedDeclaration[] Declarations;
        public RejectedDeclaration[] Rejected;
        public int Level;
        public int LayerKey;
        public Specificity Specificity;
        public int Order;
        public CompiledRule? Rule;
        public LayerNode? Layer;
        public byte OriginRank;
        public ComplexSelector? Selector;
        public MatchedRule? Record;
    }

    sealed class BlockComparer : IComparer<Block>
    {
        public static readonly BlockComparer Descending = new();

        public int Compare(Block a, Block b)
        {
            if (a.Level != b.Level) return b.Level.CompareTo(a.Level);
            if (a.LayerKey != b.LayerKey) return b.LayerKey.CompareTo(a.LayerKey);
            int s = b.Specificity.CompareTo(a.Specificity);
            if (s != 0) return s;
            return b.Order.CompareTo(a.Order);
        }
    }

    enum SlotState : byte { Free, Decided, RevertingOrigin, RevertingLayer }

    struct Slot
    {
        public CssValue? Value;
        public SlotState State;
        public byte RevertOrigin;
        public int RevertLevel;
        public LayerNode? RevertLayer;
        public DeclarationMatch? Record;
    }

    /// <summary>解析元素的计算样式。<paramref name="parentStyle"/> 为 null 表示根元素。</summary>
    public ComputedStyle Resolve(StyleElement element, ComputedStyle? parentStyle)
        => Resolve(element, parentStyle, PseudoElementKind.None, null);

    /// <summary>解析元素并收集 DevTools 匹配记录。</summary>
    public ComputedStyle Resolve(StyleElement element, ComputedStyle? parentStyle, StyleMatchRecord record)
        => Resolve(element, parentStyle, PseudoElementKind.None, record);

    /// <summary>
    /// 解析元素或其伪元素（<c>::before</c> / <c>::after</c> 等）的计算样式。解析伪元素时 <paramref name="parentStyle"/>
    /// 应为原始元素的计算样式，内联样式不参与。<paramref name="record"/> 非空时收集匹配记录（较慢）。
    /// </summary>
    public ComputedStyle Resolve(StyleElement element, ComputedStyle? parentStyle, PseudoElementKind pseudoElement, StyleMatchRecord? record)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        EnsureCompiled();
        record?.Clear();
        bool isRoot = parentStyle is null;
        var parent = parentStyle ?? ComputedStyle.Initial;

        try
        {
            CollectBlocks(element, pseudoElement, record);
            RunCascade(record);
            return Compute(parent, isRoot, record);
        }
        finally
        {
            // 即使元素适配器抛出异常，也不能把半途的状态留给下一次解析
            ResetSlots();
        }
    }

    void CollectBlocks(StyleElement element, PseudoElementKind pseudo, StyleMatchRecord? record)
    {
        _entries.Clear();
        _blocks.Clear();
        _index.CollectMatches(element, _entries, pseudo);
        CompiledRule? last = null;
        int lastFirstBlock = -1;
        foreach (var e in _entries)
        {
            var rule = e.Value;
            if (rule.Media is { } gate && !gate.Evaluate(_mediaGeneration, Environment.Media)) continue;
            if (ReferenceEquals(rule, last))
            {
                // 同一规则的多个选择器都命中：取特异性最高的那个（同一规则的项在索引中相邻）
                MatchedRule? better = null;
                for (int i = lastFirstBlock; i < _blocks.Count; i++)
                {
                    var b = _blocks[i];
                    if (e.Specificity > b.Specificity)
                    {
                        b.Specificity = e.Specificity;
                        b.Selector = e.Selector;
                        // 匹配记录也要反映实际参与级联的选择器与特异性
                        if (b.Record is not null) b.Record = better ??= CreateRecord(rule, e.Specificity, e.Selector);
                        _blocks[i] = b;
                    }
                }
                continue;
            }
            last = rule;
            lastFirstBlock = _blocks.Count;
            AddRuleBlocks(rule, e.Specificity, e.Selector, record is not null);
        }

        if (pseudo == PseudoElementKind.None && element.InlineStyle is { } inline)
        {
            var block = inline.Block;
            if (!block.IsEmpty || (record is not null && block.Rejected.Length > 0))
            {
                MatchedRule? rec = record is null ? null
                    : new MatchedRule(null, string.Empty, null, default, CascadeOrigin.Author, isInline: true, null, Array.Empty<MediaQueryList>(), null, int.MaxValue);
                AddBlock(block.Normal, block.Rejected, LevelInline, 0, default, int.MaxValue, null, null, 1, null, rec);
                if (block.Important.Length > 0)
                    AddBlock(block.Important, Array.Empty<RejectedDeclaration>(), LevelInlineImportant, 0, default, int.MaxValue, null, null, 1, null, rec);
            }
        }

        if (_blocks.Count > 1) _blocks.Sort(BlockComparer.Descending);

        if (record is not null)
        {
            // 以普通声明的优先级给规则排序（每条规则只出现一次）
            _recordedRules.Clear();
            foreach (var b in _blocks)
            {
                if (b.Record is not null && _recordedRules.Add(b.Record)) record.Rules.Add(b.Record);
            }
        }
    }

    void AddRuleBlocks(CompiledRule rule, Specificity specificity, ComplexSelector selector, bool recording)
    {
        var block = rule.Block;
        var layer = rule.Layer;
        byte originRank = (byte)rule.Origin;
        int normalLevel, importantLevel;
        switch (rule.Origin)
        {
            case CascadeOrigin.UserAgent: normalLevel = LevelUserAgent; importantLevel = LevelUserAgentImportant; break;
            case CascadeOrigin.DevTools: normalLevel = LevelDevTools; importantLevel = LevelDevToolsImportant; break;
            default: normalLevel = LevelAuthor; importantLevel = LevelAuthorImportant; break;
        }
        MatchedRule? rec = recording ? CreateRecord(rule, specificity, selector) : null;
        // 普通层：层按后序编号（未分层的根最大）；!important 层逆序（未分层最小）
        if (block.Normal.Length > 0 || (recording && block.Rejected.Length > 0))
            AddBlock(block.Normal, block.Rejected, normalLevel, layer.Rank, specificity, rule.Order, rule, layer, originRank, selector, rec);
        if (block.Important.Length > 0)
            AddBlock(block.Important, Array.Empty<RejectedDeclaration>(), importantLevel, -layer.Rank, specificity, rule.Order, rule, layer, originRank, selector, rec);
    }

    /// <summary>为一次解析中命中的规则创建 DevTools 记录（只在记录模式下调用）。</summary>
    static MatchedRule CreateRecord(CompiledRule rule, Specificity specificity, ComplexSelector selector)
    {
        var media = new List<MediaQueryList>();
        rule.Media?.CollectChain(media);
        string selectorText = rule.Source switch
        {
            CssStyleRule s => s.SelectorText,
            CssNestedDeclarationsRule n => n.ParentStyleRule.SelectorText,
            _ => string.Empty,
        };
        return new MatchedRule(rule.Source, selectorText, selector, specificity, rule.Origin, isInline: false,
            rule.Layer.IsRoot ? null : rule.Layer.DisplayName, media, rule.Sheet, rule.Order);
    }

    void AddBlock(ParsedDeclaration[] decls, RejectedDeclaration[] rejected, int level, int layerKey, Specificity specificity, int order,
        CompiledRule? rule, LayerNode? layer, byte originRank, ComplexSelector? selector, MatchedRule? rec)
    {
        _blocks.Add(new Block
        {
            Declarations = decls,
            Rejected = rejected,
            Level = level,
            LayerKey = layerKey,
            Specificity = specificity,
            Order = order,
            Rule = rule,
            Layer = layer,
            OriginRank = originRank,
            Selector = selector,
            Record = rec,
        });
    }

    /// <summary>按优先级从高到低遍历声明，为每个属性选出胜出者；处理 revert / revert-layer。</summary>
    void RunCascade(StyleMatchRecord? record)
    {
        foreach (var b in _blocks)
        {
            var decls = b.Declarations;
            // 同一块内后出现的声明优先
            for (int i = decls.Length - 1; i >= 0; i--)
            {
                ref readonly var d = ref decls[i];
                DeclarationStatus status;
                if (d.IsCustom)
                {
                    _customSlots.TryGetValue(d.CustomName!, out var cs);
                    status = Consider(ref cs, b, d.Value);
                    if (record is not null)
                    {
                        var m = Record(record, b, d, status);
                        if (status == DeclarationStatus.Active) cs.Record = m;
                    }
                    _customSlots[d.CustomName!] = cs;
                }
                else
                {
                    ref var slot = ref _slots[(int)d.Id];
                    if (slot.State == SlotState.Free) _touched.Add((int)d.Id);
                    status = Consider(ref slot, b, d.Value);
                    if (record is not null)
                    {
                        var m = Record(record, b, d, status);
                        if (status == DeclarationStatus.Active) slot.Record = m;
                    }
                }
            }
            if (record is not null && b.Rejected.Length > 0)
            {
                foreach (var r in b.Rejected)
                {
                    CssPropertyId? id = CssProperties.TryGet(r.Source.Name, out var p) ? p.Id : null;
                    record.Decls.Add(new DeclarationMatch(b.Record!, r.Source, r.Source.Name, id, r.Status, r.Reason));
                }
            }
        }
    }

    static DeclarationMatch Record(StyleMatchRecord record, in Block b, in ParsedDeclaration d, DeclarationStatus status)
    {
        var m = new DeclarationMatch(b.Record!, d.Source, d.Name, d.IsCustom ? null : d.Id, status, null);
        record.Decls.Add(m);
        return m;
    }

    static DeclarationStatus Consider(ref Slot slot, in Block b, CssValue value)
    {
        switch (slot.State)
        {
            case SlotState.Decided:
                return DeclarationStatus.Overridden;
            case SlotState.RevertingOrigin:
                if (b.OriginRank >= slot.RevertOrigin) return DeclarationStatus.Overridden;
                break;
            case SlotState.RevertingLayer:
                if (b.Level == slot.RevertLevel && ReferenceEquals(b.Layer, slot.RevertLayer)) return DeclarationStatus.Overridden;
                break;
        }
        if (value is CssWideKeywordValue wide)
        {
            if (wide.Keyword == CssWideKeyword.Revert)
            {
                // 回退到上一个来源：跳过同一来源及更高来源的全部声明
                slot.State = SlotState.RevertingOrigin;
                slot.RevertOrigin = b.OriginRank;
                slot.Value = null;
                return DeclarationStatus.Active;
            }
            if (wide.Keyword == CssWideKeyword.RevertLayer)
            {
                // 回退到上一个层：跳过同一层级（来源与重要性）同一层中的声明
                slot.State = SlotState.RevertingLayer;
                slot.RevertLevel = b.Level;
                slot.RevertLayer = b.Layer;
                slot.Value = null;
                return DeclarationStatus.Active;
            }
        }
        slot.State = SlotState.Decided;
        slot.Value = value;
        return DeclarationStatus.Active;
    }

    void ResetSlots()
    {
        foreach (int id in _touched) _slots[id] = default;
        _touched.Clear();
        _customSlots.Clear();
        _pendingKey = null;
        _pendingValues.Clear();
    }

    // ================= 计算值 =================

    ComputedStyle Compute(ComputedStyle parent, bool isRoot, StyleMatchRecord? record)
    {
        // 自定义属性
        _vars.Begin(parent.CustomProperties, Environment);
        foreach (var kv in _customSlots)
        {
            var s = kv.Value;
            if (s.State == SlotState.Decided && s.Value is not null) _vars.Declare(kv.Key, s.Value);
            // revert 到底：等同 unset（自定义属性可继承 → 取父值，无需记录）
        }
        var customMap = _vars.HasDeclarations
            ? _vars.BuildMap(record is null ? null : name =>
            {
                var w = record.GetWinner(name);
                if (w is not null)
                {
                    w.Status = DeclarationStatus.Invalid;
                    w.Reason = "var() 引用无效或存在循环，计算值时无效";
                }
            })
            : parent.CustomProperties;

        var c = _computer;
        c.Begin(isRoot ? null : parent, isRoot);
        _explicitBorderWidths = 0;
        _inheritsNonInherited = false;
        var b = _builder;
        b.ResetGroups(parent, ComputedStyle.Initial);

        // font-size 先于其他属性（em 的基准）
        ApplySlot(CssPropertyId.FontSize, ref _slots[(int)CssPropertyId.FontSize], parent);
        c.FontSize = b.Font.FontSize;
        if (isRoot) c.RootFontSize = b.Font.FontSize;

        foreach (int i in _touched)
        {
            if (i == (int)CssPropertyId.FontSize) continue;
            ApplySlot((CssPropertyId)i, ref _slots[i], parent);
        }

        FixUp(b, parent, isRoot, _explicitBorderWidths);

        b.CustomProperties = customMap;
        b.RootFontSize = isRoot ? b.Font.FontSize : parent.RootFontSize;
        b.Flags = c.Flags | (_vars.UsedEnvironment ? ComputedStyleFlags.DependsOnEnvironment : ComputedStyleFlags.None)
                  | (_inheritsNonInherited ? ComputedStyleFlags.InheritsNonInherited : ComputedStyleFlags.None);
        return new ComputedStyle(b);
    }

    void ApplySlot(CssPropertyId id, ref Slot slot, ComputedStyle parent)
    {
        // 未决定（含 revert 到底）：保持继承值或初始值，即 unset
        if (slot.State != SlotState.Decided || slot.Value is null) return;
        var value = slot.Value;
        var b = _builder;
        if (value is UnparsedValue u) value = Substitute(id, u, ref slot);
        else if (value is PendingSubstitutionValue p) value = SubstitutePending(id, p, ref slot);

        if (value is CssWideKeywordValue wide)
        {
            switch (wide.Keyword)
            {
                case CssWideKeyword.Initial:
                    b.CopyFrom(id, ComputedStyle.Initial);
                    return;
                case CssWideKeyword.Inherit:
                    b.CopyFrom(id, parent);
                    _explicitBorderWidths |= BorderWidthBit(id);
                    if (!CssProperties.Get(id).Inherited) _inheritsNonInherited = true;
                    return;
                default:
                    b.CopyFrom(id, CssProperties.Get(id).Inherited ? parent : ComputedStyle.Initial);
                    return;
            }
        }
        _explicitBorderWidths |= BorderWidthBit(id);
        // color: currentColor 等同 inherit
        if (id == CssPropertyId.Color && value is ColorValue { Color.IsCurrentColor: true })
        {
            b.CopyFrom(id, parent);
            return;
        }
        b.Apply(id, value, _computer);
    }

    CssValue Substitute(CssPropertyId id, UnparsedValue u, ref Slot slot)
    {
        int budget = VariableResolver.MaxTokens;
        var tokens = _vars.Substitute(u.Tokens, ref budget);
        CssValue? parsed = null;
        if (tokens is not null)
        {
            var single = SingleWideKeyword(tokens);
            parsed = single ?? PropertyParsers.ParseLonghand(id, tokens);
        }
        if (parsed is null)
        {
            MarkInvalid(ref slot);
            return CssWideKeywordValue.Unset;
        }
        return parsed;
    }

    CssValue SubstitutePending(CssPropertyId id, PendingSubstitutionValue p, ref Slot slot)
    {
        // 同一简写的各长属性共享同一个占位值：只替换与解析一次
        if (!ReferenceEquals(_pendingKey, p))
        {
            _pendingKey = p;
            _pendingValues.Clear();
            int budget = VariableResolver.MaxTokens;
            var tokens = _vars.Substitute(p.Value.Tokens, ref budget);
            _pendingValid = tokens is not null
                && PropertyParsers.TryParseValue(CssProperties.Get(p.Shorthand), tokens, _pendingValues);
        }
        if (_pendingValid)
        {
            foreach (var pv in _pendingValues)
                if (pv.Id == id && pv.Value is not (UnparsedValue or PendingSubstitutionValue)) return pv.Value;
        }
        MarkInvalid(ref slot);
        return CssWideKeywordValue.Unset;
    }

    static void MarkInvalid(ref Slot slot)
    {
        if (slot.Record is { } r)
        {
            r.Status = DeclarationStatus.Invalid;
            r.Reason = "var() 替换后的值无效，计算值时无效（按 unset 处理）";
        }
    }

    /// <summary>替换结果是单个全局关键字时按该关键字处理（revert 类在此处按 unset 处理）。</summary>
    static CssValue? SingleWideKeyword(IReadOnlyList<CssComponentValue> tokens)
    {
        var r = new ValueReader(tokens);
        if (r.Next() is CssToken { Type: CssTokenType.Ident } t && r.AtEnd && CssWideKeywordValue.TryParse(t.Value, out var k))
            return k is CssWideKeyword.Revert or CssWideKeyword.RevertLayer ? CssWideKeywordValue.Unset : CssWideKeywordValue.Of(k);
        return null;
    }

    /// <summary>计算值阶段的跨属性修正（CSS 规范规定的计算值规则）。</summary>
    /// <summary>
    /// 边框宽度的计算值：样式为 none/hidden 时为 0；否则未显式指定（初始值 medium）时为 3px。
    /// 初始样式的边框样式是 none，因此其计算宽度为 0，绝大多数元素直接共享初始的边框分组。
    /// </summary>
    internal static void FixUpBorderWidths(StyleBuilder b, int explicitMask)
    {
        var border = b.Border;
        Side(border.BorderTopStyle, border.BorderTopWidth, 1, (g, v) => g.BorderTopWidth = v);
        Side(border.BorderRightStyle, border.BorderRightWidth, 2, (g, v) => g.BorderRightWidth = v);
        Side(border.BorderBottomStyle, border.BorderBottomWidth, 4, (g, v) => g.BorderBottomWidth = v);
        Side(border.BorderLeftStyle, border.BorderLeftWidth, 8, (g, v) => g.BorderLeftWidth = v);

        void Side(CssLineStyle style, float width, int bit, Action<BorderStyleGroup, float> set)
        {
            float target = NoBorder(style) ? 0 : (explicitMask & bit) != 0 ? width : MediumBorderWidth;
            if (target != width) set(b.MutableBorder(), target);
        }
    }

    const float MediumBorderWidth = 3;

    static int BorderWidthBit(CssPropertyId id) => id switch
    {
        CssPropertyId.BorderTopWidth => 1,
        CssPropertyId.BorderRightWidth => 2,
        CssPropertyId.BorderBottomWidth => 4,
        CssPropertyId.BorderLeftWidth => 8,
        _ => 0,
    };

    static void FixUp(StyleBuilder b, ComputedStyle parent, bool isRoot, int explicitBorderWidths)
    {
        FixUpBorderWidths(b, explicitBorderWidths);

        // overflow：一轴为 visible/clip 而另一轴不是时，visible → auto，clip → hidden（CSS Overflow 3）
        var box = b.Box;
        var ox = box.OverflowX;
        var oy = box.OverflowY;
        bool xVis = ox is CssOverflow.Visible or CssOverflow.Clip, yVis = oy is CssOverflow.Visible or CssOverflow.Clip;
        if (xVis != yVis)
        {
            if (xVis) b.MutableBox().OverflowX = ox == CssOverflow.Visible ? CssOverflow.Auto : CssOverflow.Hidden;
            else b.MutableBox().OverflowY = oy == CssOverflow.Visible ? CssOverflow.Auto : CssOverflow.Hidden;
        }

        // 块级化（CSS Display 3 §2.7）：根元素、绝对/固定定位元素与 flex/grid 容器的子元素
        var display = b.Box.Display;
        if (display is not (CssDisplay.None or CssDisplay.Block or CssDisplay.Flex or CssDisplay.Grid or CssDisplay.FlowRoot))
        {
            bool blockify = isRoot
                || b.Position.Position is CssPosition.Absolute or CssPosition.Fixed
                || parent.Display is CssDisplay.Flex or CssDisplay.InlineFlex or CssDisplay.Grid or CssDisplay.InlineGrid;
            if (blockify && !(display == CssDisplay.Contents && !isRoot))
            {
                b.MutableBox().Display = display switch
                {
                    CssDisplay.InlineFlex => CssDisplay.Flex,
                    CssDisplay.InlineGrid => CssDisplay.Grid,
                    _ => CssDisplay.Block,
                };
            }
        }
    }

    static bool NoBorder(CssLineStyle s) => s is CssLineStyle.None or CssLineStyle.Hidden;
}
