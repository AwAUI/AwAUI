using System;
using System.Collections.Generic;
using AwAUI.Css.Animation;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.Cascade;

/// <summary>一条 <c>@keyframes</c> 规则及其在级联中的位置（同名规则按来源、层、出现顺序取最后一条，CSS Animations 1 §3 / Cascade 5 §6.4）。</summary>
internal sealed class KeyframesCandidate
{
    public KeyframesCandidate(CssKeyframesRule rule, CascadeStyleSheet sheet, LayerNode layer, MediaGate? media, int order)
    {
        Rule = rule;
        Sheet = sheet;
        Layer = layer;
        Media = media;
        Order = order;
    }

    public CssKeyframesRule Rule { get; }
    public CascadeStyleSheet Sheet { get; }
    public LayerNode Layer { get; }
    public MediaGate? Media { get; }
    public int Order { get; }
    public CssKeyframes? Compiled;
}

public sealed partial class StyleEngine
{
    int _compileGeneration;

    /// <summary>每次重新编译样式表（增删、替换样式表或层序变化）加一；动画引擎据此重新查找 <c>@keyframes</c>。</summary>
    public int CompileGeneration
    {
        get
        {
            EnsureCompiled();
            return _compileGeneration;
        }
    }

    /// <summary>
    /// 按名称查找生效的 <c>@keyframes</c>（名称区分大小写）：当前 <c>@media</c> 条件成立的同名规则中，来源更高、层更高、出现更晚的胜出。
    /// 没有时返回 null（该动画不运行，也不产生事件）。
    /// </summary>
    public CssKeyframes? FindKeyframes(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        EnsureCompiled();
        if (!_keyframes.TryGetValue(name, out var list)) return null;
        KeyframesCandidate? best = null;
        foreach (var c in list)
        {
            if (c.Media is { } gate && !gate.Evaluate(_mediaGeneration, Environment.Media)) continue;
            if (best is null || Compare(c, best) > 0) best = c;
        }
        if (best is null) return null;
        return best.Compiled ??= new CssKeyframes(best.Rule, best.Sheet._diagnostics);

        static int Compare(KeyframesCandidate a, KeyframesCandidate b)
        {
            if (a.Sheet.Origin != b.Sheet.Origin) return a.Sheet.Origin.CompareTo(b.Sheet.Origin);
            if (a.Layer.Rank != b.Layer.Rank) return a.Layer.Rank.CompareTo(b.Layer.Rank);
            return a.Order.CompareTo(b.Order);
        }
    }

    /// <summary>
    /// 在元素已计算的样式 <paramref name="baseStyle"/> 之上计算一组声明（关键帧）：<c>var()</c> 取元素的自定义属性，em / lh / 容器单位按元素求值，
    /// <c>inherit</c> 取父样式。返回的样式中只有 <paramref name="declarations"/> 声明的属性有意义。
    /// </summary>
    internal ComputedStyle ComputeDeclarations(StyleElement element, ComputedStyle baseStyle, ComputedStyle? parentStyle, ParsedDeclaration[] declarations)
    {
        EnsureCompiled();
        try
        {
            // 后出现的声明优先
            for (int i = declarations.Length - 1; i >= 0; i--)
            {
                var d = declarations[i];
                if (d.IsCustom)
                {
                    if (!_customSlots.ContainsKey(d.CustomName!))
                        _customSlots[d.CustomName!] = new Slot { Value = d.Value, State = SlotState.Decided, Level = LevelAnimation };
                    continue;
                }
                ref var slot = ref _slots[(int)d.Id];
                if (slot.State != SlotState.Free) continue;
                _touched.Add((int)d.Id);
                slot.State = SlotState.Decided;
                slot.Value = d.Value;
                slot.Level = LevelAnimation;
            }
            return ComputeOnto(element, baseStyle, parentStyle);
        }
        finally
        {
            ResetSlots();
        }
    }

    ComputedStyle ComputeOnto(StyleElement element, ComputedStyle baseStyle, ComputedStyle? parentStyle)
    {
        bool isRoot = parentStyle is null;
        var parent = parentStyle ?? ComputedStyle.Initial;
        var c = _computer;
        c.Begin(isRoot ? null : parent, isRoot);
        c.Element = element;
        _containerSource.Begin(element, PseudoElementKind.None);
        c.Containers = _containerSource;
        _vars.Begin(baseStyle.CustomProperties, Environment, _registry, c, ownMap: true);
        foreach (var kv in _customSlots)
            if (kv.Value.Value is not null) _vars.Declare(kv.Key, kv.Value.Value);

        _explicitBorderWidths = 0;
        _inheritsNonInherited = false;
        var b = _builder;
        b.ResetGroupsFrom(baseStyle);

        ApplySlot(CssPropertyId.FontSize, ref _slots[(int)CssPropertyId.FontSize], parent);
        c.FontSize = b.Font.FontSize;
        if (isRoot) c.RootFontSize = b.Font.FontSize;
        ApplySlot(CssPropertyId.LineHeight, ref _slots[(int)CssPropertyId.LineHeight], parent);
        c.LineHeight = b.Font.LineHeight.Resolve(b.Font.FontSize);
        if (isRoot) c.RootLineHeight = c.LineHeight;

        var customMap = _vars.NeedsMap ? _vars.BuildMap() : baseStyle.CustomProperties;
        foreach (int i in _touched)
        {
            if (i is (int)CssPropertyId.FontSize or (int)CssPropertyId.LineHeight) continue;
            ApplySlot((CssPropertyId)i, ref _slots[i], parent);
        }
        b.CustomProperties = customMap;
        b.RootFontSize = baseStyle.RootFontSize;
        b.RootLineHeight = baseStyle.RootLineHeight;
        b.Flags = baseStyle.Flags;
        c.Element = null;
        c.Containers = null;
        return new ComputedStyle(b);
    }

    readonly StyleBuilder _overrideBuilder = new();

    /// <summary>
    /// 在 <paramref name="baseStyle"/> 上覆盖若干属性的计算值（动画与过渡的结果）。没有任何值与基础样式不同时返回 <paramref name="baseStyle"/> 本身。
    /// </summary>
    internal ComputedStyle ApplyComputedValues(ComputedStyle baseStyle, List<KeyValuePair<CssPropertyId, object>> values)
    {
        if (values.Count == 0) return baseStyle;
        var b = _overrideBuilder;
        b.ResetGroupsFrom(baseStyle);
        foreach (var kv in values) AnimationDispatch.SetValue(b, kv.Key, kv.Value);
        if (!b.AnyGroupOwned) return baseStyle;
        b.CustomProperties = baseStyle.CustomProperties;
        b.RootFontSize = baseStyle.RootFontSize;
        b.RootLineHeight = baseStyle.RootLineHeight;
        b.Flags = baseStyle.Flags;
        return new ComputedStyle(b);
    }
}
