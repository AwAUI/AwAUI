using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Css;
using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;
using AwAUI.Diagnostics.Inspection;

namespace AwAUI.UI;

/// <summary>
/// DevTools <c>Css</c> 域的数据源（design/20）：基于 CSS 引擎的 <see cref="StyleMatchRecord"/> 报告命中的规则与计算值，
/// 并真实地编辑样式：
/// <list type="bullet">
/// <item><b>规则</b>：改写所在样式表的源文本（只替换该规则的声明块），重新解析后用 <see cref="RenderPipeline.ReplaceStyleSheet"/>
/// 原位替换（来源与级联位置不变），下一帧重算样式并重绘。首次修改前记下原样式表，<see cref="ClearOverrides"/> 还原。</item>
/// <item><b>内联样式</b>：写入 <see cref="RenderBox.InlineOverride"/>（替代元素自身的 <c>Style</c> 文本），组件重建不会冲掉它。</item>
/// <item><b>新规则</b>：加入独立的 DevTools 来源样式表（级联中最高的来源）。</item>
/// <item><b>强制伪状态</b>：<see cref="Element.ForcedStates"/>。</item>
/// </list>
/// 规则 id 为「样式表 id/规则序号」（序号按 <see cref="StyleSheet.EnumerateRules"/> 中的样式规则与嵌套声明规则计数）；
/// 样式表文本整体替换后旧的规则 id 可能失效。只在 UI 线程上使用。
/// </summary>
internal sealed class ElementStyleInspector : StyleInspector
{
    /// <summary>继承规则最多回溯的祖先层数（每层要做一次带记录的级联）。</summary>
    const int MaxInheritedAncestors = 64;

    readonly UIRoot _root;
    readonly RenderPipeline _pipeline;
    readonly ElementInspectableTree _tree;
    readonly Dictionary<CascadeStyleSheet, string> _sheetIds = new();
    readonly Dictionary<string, CascadeStyleSheet> _sheets = new(StringComparer.Ordinal);
    readonly Dictionary<string, StyleSheet> _originals = new(StringComparer.Ordinal);
    readonly Dictionary<StyleSheet, RuleTable> _ruleTables = new();
    readonly HashSet<Element> _overridden = new();
    CascadeStyleSheet? _devToolsSheet;
    int _nextSheet;
    bool _disposed;

    public ElementStyleInspector(UIRoot root, RenderPipeline pipeline, ElementInspectableTree tree)
    {
        _root = root;
        _pipeline = pipeline;
        _tree = tree;
        pipeline.StyleSheetAdded += OnSheetAdded;
        pipeline.StyleSheetRemoved += OnSheetRemoved;
        pipeline.StyleSheetReplaced += OnSheetReplaced;
        pipeline.ComputedStyleChanged = OnComputedStyleChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipeline.StyleSheetAdded -= OnSheetAdded;
        _pipeline.StyleSheetRemoved -= OnSheetRemoved;
        _pipeline.StyleSheetReplaced -= OnSheetReplaced;
        if (_pipeline.ComputedStyleChanged == (Action<Element>)OnComputedStyleChanged) _pipeline.ComputedStyleChanged = null;
    }

    // ------------------------------------------------------------ 样式表 id

    string SheetId(CascadeStyleSheet cs)
    {
        if (_sheetIds.TryGetValue(cs, out var id)) return id;
        id = cs.Origin == CascadeOrigin.DevTools && !_sheets.ContainsKey("devtools") ? "devtools" : "s" + (++_nextSheet).ToString(CultureInfo.InvariantCulture);
        _sheetIds[cs] = id;
        _sheets[id] = cs;
        return id;
    }

    CascadeStyleSheet? FindSheet(string id)
    {
        if (!_sheets.TryGetValue(id, out var cs)) return null;
        foreach (var s in _pipeline.StyleEngine.StyleSheets)
            if (ReferenceEquals(s, cs)) return cs;
        return null;
    }

    void OnSheetAdded(CascadeStyleSheet cs) => NotifyStyleSheetAdded(SheetInfo(cs));

    void OnSheetRemoved(CascadeStyleSheet cs)
    {
        if (!_sheetIds.TryGetValue(cs, out var id)) return;
        _sheetIds.Remove(cs);
        _sheets.Remove(id);
        _originals.Remove(id);
        _ruleTables.Remove(cs.Sheet);
        NotifyStyleSheetRemoved(id);
    }

    void OnSheetReplaced(CascadeStyleSheet old, CascadeStyleSheet now)
    {
        var id = SheetId(old);
        _sheetIds.Remove(old);
        _sheetIds[now] = id;
        _sheets[id] = now;
        _ruleTables.Remove(old.Sheet);
        if (ReferenceEquals(old, _devToolsSheet)) _devToolsSheet = now;
        NotifyStyleSheetChanged(id);
    }

    void OnComputedStyleChanged(Element e) => NotifyComputedStyleInvalidated(e.UniqueId);

    StyleSheetInfo SheetInfo(CascadeStyleSheet cs) => new(SheetId(cs), OriginOf(cs.Origin))
    {
        SourceUrl = cs.Sheet.SourceName,
        Title = cs.Origin == CascadeOrigin.DevTools ? "DevTools" : cs.Sheet.SourceName,
        Editable = true,
    };

    static StyleOrigin OriginOf(CascadeOrigin o) => o switch
    {
        CascadeOrigin.UserAgent => StyleOrigin.Default,
        CascadeOrigin.DevTools => StyleOrigin.DevTools,
        _ => StyleOrigin.Author,
    };

    // ------------------------------------------------------------ 规则 id

    /// <summary>一个样式表中可编辑的规则（样式规则与嵌套声明规则），按 <see cref="StyleSheet.EnumerateRules"/> 的顺序。</summary>
    sealed class RuleTable
    {
        public readonly List<CssRule> Rules = new();
        public readonly Dictionary<CssRule, int> Index = new();
    }

    RuleTable Rules(StyleSheet sheet)
    {
        if (_ruleTables.TryGetValue(sheet, out var t)) return t;
        t = new RuleTable();
        foreach (var r in sheet.EnumerateRules())
        {
            if (r.Kind is not (CssRuleKind.Style or CssRuleKind.NestedDeclarations)) continue;
            t.Index[r] = t.Rules.Count;
            t.Rules.Add(r);
        }
        _ruleTables[sheet] = t;
        return t;
    }

    string RuleId(CascadeStyleSheet cs, CssRule rule) =>
        SheetId(cs) + "/" + (Rules(cs.Sheet).Index.TryGetValue(rule, out var i) ? i : -1).ToString(CultureInfo.InvariantCulture);

    bool TryFindRule(string ruleId, out CascadeStyleSheet sheet, out int index)
    {
        sheet = null!;
        index = -1;
        int slash = ruleId.LastIndexOf('/');
        if (slash <= 0 || !int.TryParse(ruleId.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
        var cs = FindSheet(ruleId.Substring(0, slash));
        if (cs is null || index >= Rules(cs.Sheet).Rules.Count) return false;
        sheet = cs;
        return true;
    }

    /// <summary>规则在样式表源文本中可编辑的声明块区间 [start, end)：花括号之内、第一条嵌套规则之前。</summary>
    static (int Start, int End) BlockRange(string text, CssRule rule)
    {
        if (rule is CssNestedDeclarationsRule)
            return (Clamp(rule.Range.Start.Offset, text), Clamp(rule.Range.End.Offset, text));
        var style = (CssStyleRule)rule;
        int open = text.IndexOf('{', Clamp(style.PreludeRange.End.Offset, text));
        int start = open < 0 ? text.Length : open + 1;
        int end = Clamp(style.Range.End.Offset, text);
        if (end > start && text[end - 1] == '}') end--;
        if (style.ChildRules.Count > 0) end = Math.Min(end, Math.Max(start, style.ChildRules[0].Range.Start.Offset));
        return (start, Math.Max(start, end));
    }

    static int Clamp(int offset, string text) => offset < 0 ? 0 : Math.Min(offset, text.Length);

    static IReadOnlyList<CssDeclaration> DeclarationsOf(CssRule rule) => rule switch
    {
        CssStyleRule s => s.Declarations,
        CssNestedDeclarationsRule n => n.Declarations,
        _ => Array.Empty<CssDeclaration>(),
    };

    static string SelectorTextOf(CssRule rule) => rule switch
    {
        CssStyleRule s => s.SelectorText,
        CssNestedDeclarationsRule n => n.ParentStyleRule.SelectorText,
        _ => "",
    };

    // ------------------------------------------------------------ 声明

    readonly struct Status
    {
        public Status(StyleDeclarationStatus value, string? reason)
        {
            Value = value;
            Reason = reason;
        }

        public readonly StyleDeclarationStatus Value;
        public readonly string? Reason;
    }

    /// <summary>
    /// 一条源声明在级联中的结果：简写展开出的长属性只要有一个胜出即为 Active，全部被覆盖为 Overridden。
    /// 记录中没有它（例如编辑后尚未在元素上级联）时按解析结果给出 Active / Invalid / Unsupported。
    /// </summary>
    static Dictionary<CssDeclaration, Status> StatusMap(StyleMatchRecord? record)
    {
        var map = new Dictionary<CssDeclaration, Status>();
        if (record is null) return map;
        foreach (var d in record.Declarations)
        {
            var s = d.Status switch
            {
                DeclarationStatus.Active => StyleDeclarationStatus.Active,
                DeclarationStatus.Overridden => StyleDeclarationStatus.Overridden,
                DeclarationStatus.Invalid => StyleDeclarationStatus.Invalid,
                _ => StyleDeclarationStatus.Unsupported,
            };
            if (map.TryGetValue(d.Declaration, out var prev))
            {
                if (prev.Value == StyleDeclarationStatus.Active || s == StyleDeclarationStatus.Overridden) continue;
            }
            map[d.Declaration] = new Status(s, d.Reason);
        }
        return map;
    }

    static Status ParseStatus(CssDeclaration d)
    {
        var block = PropertyDeclarationBlock.Parse(new[] { d });
        foreach (var r in block.RejectedDeclarations)
        {
            return r.Status == DeclarationStatus.Invalid
                ? new Status(StyleDeclarationStatus.Invalid, r.Reason)
                : new Status(StyleDeclarationStatus.Unsupported, r.Reason);
        }
        return new Status(StyleDeclarationStatus.Active, null);
    }

    /// <summary>
    /// 把一个声明块的声明（源区间为绝对偏移，<paramref name="blockStart"/> 为块在源文本中的起点）与块中被注释掉的声明
    /// （<c>/* name: value */</c>，DevTools 的「切换」）按出现顺序合并成列表。区间相对块文本。
    /// </summary>
    static List<StyleDeclarationInfo> BuildDeclarations(IReadOnlyList<CssDeclaration> decls, string blockText, int blockStart,
        Dictionary<CssDeclaration, Status> statuses)
    {
        var list = new List<StyleDeclarationInfo>(decls.Count);
        foreach (var d in decls)
        {
            var st = statuses.TryGetValue(d, out var s) ? s : ParseStatus(d);
            var info = new StyleDeclarationInfo(d.Name, d.ValueText) { Important = d.Important, Status = st.Value, Reason = st.Reason };
            int a = d.Range.Start.Offset - blockStart, b = d.Range.End.Offset - blockStart;
            if (a >= 0 && b >= a && b <= blockText.Length) info.Range = (a, b);
            list.Add(info);
        }
        AddDisabled(blockText, list);
        return list;
    }

    /// <summary>扫描块文本中的注释，内容恰好是一条声明的报告为 <see cref="StyleDeclarationInfo.Disabled"/>。线性时间。</summary>
    static void AddDisabled(string text, List<StyleDeclarationInfo> list)
    {
        int i = 0;
        List<StyleDeclarationInfo>? disabled = null;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1);
                i = close < 0 ? text.Length : close + 1;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 2;
                var inner = text.Substring(i + 2, (close < 0 ? text.Length : close) - i - 2).Trim();
                if (inner.Length > 0 && inner.Length < 4096 && inner.IndexOf(':') > 0)
                {
                    var parsed = CssInlineStyle.ParseDeclarations(inner.TrimEnd(';'));
                    if (parsed.Count == 1)
                    {
                        var d = parsed[0];
                        (disabled ??= new List<StyleDeclarationInfo>()).Add(new StyleDeclarationInfo(d.Name, d.ValueText)
                        {
                            Important = d.Important,
                            Disabled = true,
                            Status = StyleDeclarationStatus.Active,
                            Range = (i, end),
                        });
                    }
                }
                i = end;
                continue;
            }
            i++;
        }
        if (disabled is null) return;
        list.AddRange(disabled);
        list.Sort((x, y) => (x.Range?.Start ?? int.MaxValue).CompareTo(y.Range?.Start ?? int.MaxValue));
    }

    StyleRuleInfo RuleInfo(CascadeStyleSheet cs, CssRule rule, string? layer, IReadOnlyList<Css.Media.MediaQueryList>? media,
        Dictionary<CssDeclaration, Status> statuses)
    {
        var text = cs.Sheet.Text;
        var info = new StyleRuleInfo(RuleId(cs, rule), SelectorTextOf(rule), OriginOf(cs.Origin), SheetId(cs))
        {
            Layer = string.IsNullOrEmpty(layer) ? null : layer,
            Source = new SourceLocation(cs.Sheet.SourceName ?? SheetId(cs), rule.Range.Start.Line, rule.Range.Start.Column),
        };
        if (media is { Count: > 0 })
        {
            var parts = new string[media.Count];
            for (int i = 0; i < media.Count; i++) parts[i] = media[i].ToString();
            info.Media = string.Join(" and ", parts);
        }
        var (start, end) = BlockRange(text, rule);
        info.Declarations.AddRange(BuildDeclarations(DeclarationsOf(rule), text.Substring(start, end - start), start, statuses));
        return info;
    }

    StyleRuleMatch Match(MatchedRule mr, Dictionary<CssDeclaration, Status> statuses)
    {
        var cs = mr.StyleSheet!;
        var rule = mr.Rule!;
        string matched = mr.SelectorText;
        if (mr.MatchedSelector is { } sel)
        {
            var text = cs.Sheet.Text;
            int a = sel.Range.Start.Offset, b = sel.Range.End.Offset;
            if (a >= 0 && b > a && b <= text.Length) matched = text.Substring(a, b - a).Trim();
        }
        var spec = mr.Specificity;
        return new StyleRuleMatch(RuleInfo(cs, rule, mr.LayerName, mr.Media, statuses), matched, new StyleSpecificity(spec.Ids, spec.Classes, spec.Types));
    }

    // ------------------------------------------------------------ 读取

    /// <summary>对元素做一次带记录的级联（不改变元素的样式）。</summary>
    StyleMatchRecord Record(Element e, PseudoElementKind pseudo = PseudoElementKind.None)
    {
        var record = new StyleMatchRecord();
        var view = RenderBox.Get(e).GetView(_pipeline);
        var parentStyle = pseudo == PseudoElementKind.None ? e.Parent?.Render?.Style : e.Render?.Style ?? ComputedStyle.Initial;
        try
        {
            if (pseudo == PseudoElementKind.None) _pipeline.StyleEngine.Resolve(view, parentStyle, record);
            else _pipeline.StyleEngine.Resolve(view, parentStyle, pseudo, record);
        }
        catch (InsufficientExecutionStackException)
        {
            // 极深的树上选择器匹配可能耗尽栈：返回已收集的部分
        }
        return record;
    }

    public override MatchedStylesInfo? GetMatchedRules(long nodeId)
    {
        var e = _tree.Find(nodeId);
        if (e is null) return _tree.ContainsNode(nodeId) ? new MatchedStylesInfo() : null;
        var result = new MatchedStylesInfo();
        if (e.IsTextNode) return result;

        var record = Record(e);
        var statuses = StatusMap(record);
        foreach (var mr in record.MatchedRules)
        {
            if (mr.IsInline)
            {
                var text = ElementInspectableTree.EffectiveInlineStyle(e) ?? "";
                result.Inline.AddRange(BuildDeclarations(CssInlineStyle.ParseDeclarations(text), text, 0, statuses));
            }
            else if (mr.Rule is not null && mr.StyleSheet is not null) result.Matched.Add(Match(mr, statuses));
        }
        if (result.Inline.Count == 0 && ElementInspectableTree.EffectiveInlineStyle(e) is { Length: > 0 } inlineText)
        {
            // 内联样式只有注释或无效声明时级联记录里没有它：仍然列出（含被注释掉的声明）
            result.Inline.AddRange(BuildDeclarations(CssInlineStyle.ParseDeclarations(inlineText), inlineText, 0, statuses));
        }

        // 继承：从近到远的祖先中含可继承属性的规则
        int depth = 0;
        for (var a = e.Parent; a is not null && depth < MaxInheritedAncestors; a = a.Parent, depth++)
        {
            var rec = Record(a);
            var st = StatusMap(rec);
            InheritedStyleInfo? info = null;
            foreach (var mr in rec.MatchedRules)
            {
                if (mr.IsInline || mr.Rule is null || mr.StyleSheet is null || !HasInheritedDeclaration(mr.Rule)) continue;
                (info ??= new InheritedStyleInfo(a.UniqueId)).Matched.Add(Match(mr, st));
            }
            if (info is not null) result.Inherited.Add(info);
        }

        foreach (var (kind, name) in new[] { (PseudoElementKind.Before, "before"), (PseudoElementKind.After, "after") })
        {
            var rec = Record(e, kind);
            if (rec.MatchedRules.Count == 0) continue;
            var st = StatusMap(rec);
            var pe = new PseudoElementStyleInfo(name);
            foreach (var mr in rec.MatchedRules)
                if (!mr.IsInline && mr.Rule is not null && mr.StyleSheet is not null) pe.Matched.Add(Match(mr, st));
            if (pe.Matched.Count > 0) result.PseudoElements.Add(pe);
        }
        return result;
    }

    static bool HasInheritedDeclaration(CssRule rule)
    {
        foreach (var d in DeclarationsOf(rule))
        {
            if (d.IsCustomProperty) return true;
            if (CssProperties.TryGet(d.Name, out var p))
            {
                if (!p.IsShorthand) { if (p.Inherited) return true; }
                else
                    foreach (var id in p.Longhands)
                        if (CssProperties.Get(id).Inherited) return true;
            }
        }
        return false;
    }

    static string[]? s_sortedNames;
    static CssPropertyId[]? s_sortedIds;

    static void EnsureSorted()
    {
        if (s_sortedIds is not null) return;
        var props = new List<CssProperty>();
        foreach (var p in CssProperties.All)
            if (!p.IsShorthand) props.Add(p);
        props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var names = new string[props.Count];
        var ids = new CssPropertyId[props.Count];
        for (int i = 0; i < props.Count; i++)
        {
            names[i] = props[i].Name;
            ids[i] = props[i].Id;
        }
        s_sortedNames = names;
        s_sortedIds = ids;
    }

    public override IReadOnlyList<ComputedStyleProperty>? GetComputedStyle(long nodeId)
    {
        var e = _tree.Find(nodeId);
        if (e is null) return _tree.ContainsNode(nodeId) ? Array.Empty<ComputedStyleProperty>() : null;
        if (e.IsTextNode) return Array.Empty<ComputedStyleProperty>();
        EnsureSorted();
        var record = Record(e);
        var style = e.Render?.Style ?? _pipeline.StyleEngine.Resolve(RenderBox.Get(e).GetView(_pipeline), e.Parent?.Render?.Style);
        var ancestorRecords = new List<StyleMatchRecord?>();
        var list = new List<ComputedStyleProperty>(s_sortedIds!.Length + style.CustomProperties.Count);
        for (int i = 0; i < s_sortedIds.Length; i++)
        {
            var id = s_sortedIds[i];
            var p = new ComputedStyleProperty(s_sortedNames![i], style.GetPropertyValueText(id));
            var winner = record.GetWinner(id);
            if (winner is null && CssProperties.Get(id).Inherited)
                (winner, p.FromNodeId) = InheritedWinner(e, ancestorRecords, r => r.GetWinner(id));
            Describe(p, winner);
            list.Add(p);
        }
        foreach (var kv in style.CustomProperties.Enumerate())
        {
            var p = new ComputedStyleProperty(kv.Key, kv.Value.ToString());
            var winner = record.GetWinner(kv.Key);
            if (winner is null) (winner, p.FromNodeId) = InheritedWinner(e, ancestorRecords, r => r.GetWinner(kv.Key));
            Describe(p, winner);
            list.Add(p);
        }
        return list;
    }

    /// <summary>继承来的值：最近的、自身有胜出声明的祖先（祖先的匹配记录按需计算并缓存，最多回溯 <see cref="MaxInheritedAncestors"/> 层）。</summary>
    (DeclarationMatch?, long) InheritedWinner(Element e, List<StyleMatchRecord?> cache, Func<StyleMatchRecord, DeclarationMatch?> pick)
    {
        int depth = 0;
        for (var a = e.Parent; a is not null && depth < MaxInheritedAncestors; a = a.Parent, depth++)
        {
            while (cache.Count <= depth) cache.Add(null);
            var rec = cache[depth] ??= Record(a);
            if (pick(rec) is { } w) return (w, a.UniqueId);
        }
        return (null, 0);
    }

    void Describe(ComputedStyleProperty p, DeclarationMatch? winner)
    {
        if (winner is null) return;
        var rule = winner.Rule;
        p.FromOrigin = rule.IsInline ? StyleOrigin.Author : OriginOf(rule.Origin);
        if (!rule.IsInline && rule.Rule is not null && rule.StyleSheet is not null) p.FromRuleId = RuleId(rule.StyleSheet, rule.Rule);
    }

    public override IReadOnlyList<StyleDeclarationInfo>? GetInlineStyle(long nodeId)
    {
        var e = _tree.Find(nodeId);
        if (e is null) return _tree.ContainsNode(nodeId) ? Array.Empty<StyleDeclarationInfo>() : null;
        if (e.IsTextNode) return Array.Empty<StyleDeclarationInfo>();
        return InlineDeclarations(e);
    }

    List<StyleDeclarationInfo> InlineDeclarations(Element e)
    {
        var text = ElementInspectableTree.EffectiveInlineStyle(e) ?? "";
        var statuses = StatusMap(Record(e));
        return BuildDeclarations(CssInlineStyle.ParseDeclarations(text), text, 0, statuses);
    }

    public override IReadOnlyList<StyleSheetInfo> GetStyleSheets()
    {
        var list = new List<StyleSheetInfo>();
        foreach (var cs in _pipeline.StyleEngine.StyleSheets) list.Add(SheetInfo(cs));
        return list;
    }

    public override string? GetStyleSheetText(string sheetId) => FindSheet(sheetId)?.Sheet.Text;

    // ------------------------------------------------------------ 编辑

    public override IReadOnlyList<StyleDeclarationInfo>? SetStyleText(StyleTextTarget target, string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (target.IsInline)
        {
            var e = _tree.Find(target.NodeId);
            if (e is null) return _tree.ContainsNode(target.NodeId) ? throw new NotSupportedException("Text nodes have no inline style.") : null;
            if (e.IsTextNode) throw new NotSupportedException("Text nodes have no inline style.");
            ValidateDeclarationText(text, "An inline style");
            _tree.SetInlineOverride(e, text);
            TrackOverride(e);
            return InlineDeclarations(e);
        }

        if (!TryFindRule(target.RuleId!, out var cs, out int index)) return null;
        var sheetText = cs.Sheet.Text;
        var rules = Rules(cs.Sheet);
        var rule = rules.Rules[index];
        var (start, end) = BlockRange(sheetText, rule);
        // 只替换声明块：会改变规则结构的文本（花括号、未闭合的注释 / 字符串 / 括号、末尾转义）视为无法解析
        ValidateDeclarationText(text, "A declaration block");
        var newText = sheetText.Substring(0, start) + text + sheetText.Substring(end);
        var replacement = StyleSheet.Parse(newText, cs.Sheet.SourceName);
        var newTable = Rules(replacement);
        if (newTable.Rules.Count != rules.Rules.Count || newTable.Rules[index].Kind != rule.Kind)
        {
            _ruleTables.Remove(replacement);
            throw new FormatException("The declaration text changes the structure of the style sheet.");
        }
        var id = SheetId(cs);
        if (!_originals.ContainsKey(id)) _originals[id] = cs.Sheet;
        var now = _pipeline.ReplaceStyleSheet(cs, replacement)!;
        var newRule = newTable.Rules[index];
        var info = RuleInfo(now, newRule, null, null, new Dictionary<CssDeclaration, Status>());
        return info.Declarations;
    }

    /// <summary>
    /// DevTools 写入的声明文本（或选择器）会被拼接进样式表源文本：它必须是自成一体的记号序列，拼接后不能改变周围的规则结构。
    /// 拒绝花括号、未闭合的注释 / 字符串 / url（分词诊断）、不配对的圆括号与方括号，以及末尾的转义符或 '/' 记号
    /// （<c>\}</c> 会把规则的右花括号转义掉、<c>/</c> 与后面的 <c>*</c> 会组成注释；以完整注释结尾是允许的）。O(文本长度)。
    /// </summary>
    internal static void ValidateDeclarationText(string text, string what)
    {
        if (text.IndexOf('{') >= 0 || text.IndexOf('}') >= 0) throw new FormatException(what + " cannot contain '{' or '}'.");
        int last = text.Length - 1;
        while (last >= 0 && char.IsWhiteSpace(text[last])) last--;
        if (last >= 0 && text[last] == '\\')
            throw new FormatException(what + " cannot end with '" + text[last] + "'.");
        var diagnostics = new List<CssDiagnostic>();
        var tokenizer = new Css.Syntax.CssTokenizer(text, diagnostics);
        // 括号栈只记录类型（'(' 或 '['）：深度不超过文本长度
        var open = new Stack<char>();
        while (true)
        {
            var t = tokenizer.Next();
            // 末尾的 '/' 记号会与后面拼接的 '*' 组成注释；以完整注释 "/* … */" 结尾的 '/' 属于注释，不是记号，不受影响
            if (t.Type == Css.Syntax.CssTokenType.Delim && t.Delim == '/' && t.Range.Start.Offset == last)
                throw new FormatException(what + " cannot end with '/'.");
            switch (t.Type)
            {
                case Css.Syntax.CssTokenType.EndOfFile:
                    if (diagnostics.Count > 0) throw new FormatException(what + " is not well-formed: " + diagnostics[0].Message);
                    if (open.Count > 0) throw new FormatException(what + " has an unclosed '" + open.Peek() + "'.");
                    return;
                case Css.Syntax.CssTokenType.BadString:
                case Css.Syntax.CssTokenType.BadUrl:
                    throw new FormatException(what + " contains an unterminated string or url().");
                case Css.Syntax.CssTokenType.Function:
                case Css.Syntax.CssTokenType.LeftParen:
                    open.Push('(');
                    break;
                case Css.Syntax.CssTokenType.LeftBracket:
                    open.Push('[');
                    break;
                case Css.Syntax.CssTokenType.RightParen:
                case Css.Syntax.CssTokenType.RightBracket:
                {
                    char want = t.Type == Css.Syntax.CssTokenType.RightParen ? '(' : '[';
                    if (open.Count == 0 || open.Pop() != want) throw new FormatException(what + " has unbalanced brackets.");
                    break;
                }
            }
        }
    }

    public override IReadOnlyList<StyleDiagnostic>? SetStyleSheetText(string sheetId, string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var cs = FindSheet(sheetId);
        if (cs is null) return null;
        var replacement = StyleSheet.Parse(text, cs.Sheet.SourceName);
        if (!_originals.ContainsKey(sheetId)) _originals[sheetId] = cs.Sheet;
        var now = _pipeline.ReplaceStyleSheet(cs, replacement)!;
        var list = new List<StyleDiagnostic>();
        foreach (var d in replacement.Diagnostics) list.Add(ToDiagnostic(d));
        // 值层诊断（不支持的属性、无效值）在编译时产生：此处逐条规则解析一遍声明得到同样的结果
        var diags = new List<CssDiagnostic>();
        foreach (var r in Rules(replacement).Rules) PropertyDeclarationBlock.Parse(DeclarationsOf(r), diags);
        foreach (var x in diags) list.Add(ToDiagnostic(x));
        GC.KeepAlive(now);
        return list;
    }

    static StyleDiagnostic ToDiagnostic(CssDiagnostic d) =>
        new(d.Severity switch
        {
            CssDiagnosticSeverity.Error => StyleDiagnosticSeverity.Error,
            CssDiagnosticSeverity.Warning => StyleDiagnosticSeverity.Warning,
            _ => StyleDiagnosticSeverity.Info,
        }, d.Message)
        {
            Code = d.Code,
            Line = d.Range.Start.Line,
            Column = d.Range.Start.Column,
        };

    public override StyleRuleInfo AddRule(string selector, string? text)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        ElementInspectableTree.ParseSelector(selector);
        if (selector.IndexOf('{') >= 0 || selector.IndexOf('}') >= 0) throw new FormatException("Invalid selector: " + selector);
        text ??= "";
        ValidateDeclarationText(selector, "A selector");
        ValidateDeclarationText(text, "A declaration block");
        _devToolsSheet ??= _pipeline.AddStyleSheet(StyleSheet.Parse("", "devtools"), CascadeOrigin.DevTools);
        var cs = _devToolsSheet;
        var old = cs.Sheet.Text;
        var newText = old + (old.Length > 0 ? "\n" : "") + selector.Trim() + " { " + text + " }";
        var replacement = StyleSheet.Parse(newText, "devtools");
        var table = Rules(replacement);
        if (table.Rules.Count != Rules(cs.Sheet).Rules.Count + 1)
        {
            _ruleTables.Remove(replacement);
            throw new FormatException("Invalid rule: " + selector);
        }
        var now = _pipeline.ReplaceStyleSheet(cs, replacement)!;
        return RuleInfo(now, table.Rules[table.Rules.Count - 1], null, null, new Dictionary<CssDeclaration, Status>());
    }

    public override bool ForcePseudoState(long nodeId, IReadOnlyList<string> states)
    {
        var e = _tree.Find(nodeId);
        if (e is null || e.IsTextNode) return false;
        e.ForcedStates = ElementInspectableTree.ParseStates(states);
        if (e.ForcedStates != PseudoStates.None) TrackOverride(e);
        return true;
    }

    public override bool ClearOverrides(long? nodeId)
    {
        if (nodeId is { } id)
        {
            var e = _tree.Find(id);
            if (e is null) return false;
            ClearElement(e);
            _overridden.Remove(e);
            return true;
        }
        foreach (var e in _overridden) ClearElement(e);
        _overridden.Clear();
        foreach (var kv in new List<KeyValuePair<string, StyleSheet>>(_originals))
        {
            if (FindSheet(kv.Key) is { } cs) _pipeline.ReplaceStyleSheet(cs, kv.Value);
        }
        _originals.Clear();
        if (_devToolsSheet is { } dev)
        {
            _devToolsSheet = null;
            _pipeline.RemoveStyleSheet(dev);
        }
        return true;
    }

    int _overridePruneAt = 64;

    /// <summary>
    /// 记录被 DevTools 修改过的元素（供 clearOverrides 还原）。集合超过阈值时先剔除已卸载的元素再把阈值翻倍：
    /// 卸载的元素不会因为曾被编辑而一直被引用，均摊 O(1)。
    /// </summary>
    void TrackOverride(Element e)
    {
        _overridden.Add(e);
        if (_overridden.Count < _overridePruneAt) return;
        _overridden.RemoveWhere(x => !ReferenceEquals(x.Root, _root));
        _overridePruneAt = Math.Max(64, _overridden.Count * 2);
    }

    void ClearElement(Element e)
    {
        if (e.Render?.InlineOverride is not null) _tree.SetInlineOverride(e, null);
        e.ForcedStates = PseudoStates.None;
    }
}
