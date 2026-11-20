using System;
using System.Collections.Generic;
using AwAUI.Css.Values;

namespace AwAUI.Css.Animation;

/// <summary><c>animation-composition</c> 的取值（CSS Animations 2 / Web Animations 的 composite operation）。</summary>
public enum CssCompositeOperation : byte
{
    Replace,
    Add,
    Accumulate,
}

/// <summary>
/// 编译后的 <c>@keyframes</c> 规则（CSS Animations 1 §3）：关键帧按位置排序；同一位置、同一缓动与合成方式的关键帧合并为一组，
/// 组内后出现的声明优先（关键帧中的 <c>!important</c> 声明已在解析时丢弃）。关键帧中的 <c>animation-timing-function</c> 是该帧到下一帧
/// 区间的缓动，<c>animation-composition</c> 是该帧的合成方式；不可动画的属性（<c>animation-*</c>、<c>transition-*</c> 等）被忽略。
/// </summary>
public sealed class CssKeyframes
{
    internal CssKeyframes(CssKeyframesRule rule, List<CssDiagnostic>? diagnostics)
    {
        Rule = rule;
        var groups = new List<KeyframeGroup>();
        var ids = new HashSet<CssPropertyId>();
        var custom = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in rule.Keyframes)
        {
            var block = PropertyDeclarationBlock.Parse(frame.Declarations, diagnostics);
            CssEasingFunction? easing = null;
            CssCompositeOperation? composite = null;
            var decls = new List<ParsedDeclaration>(block.Normal.Length);
            foreach (var d in block.Normal)
            {
                if (d.IsCustom)
                {
                    decls.Add(d);
                    continue;
                }
                if (d.Id == CssPropertyId.AnimationTimingFunction)
                {
                    if (d.Value is PrecomputedValue<CssList<CssEasingFunction>> e && e.Value.Count > 0) easing = e.Value[0];
                    continue;
                }
                if (d.Id == CssPropertyId.AnimationComposition)
                {
                    if (d.Value is PrecomputedValue<CssList<CssAnimationComposition>> c && c.Value.Count > 0)
                        composite = (CssCompositeOperation)(byte)c.Value[0];
                    continue;
                }
                if (CssProperties.Get(d.Id).Animatable == CssAnimatable.No) continue;
                decls.Add(d);
            }
            foreach (var offset in frame.Offsets)
            {
                KeyframeGroup? g = null;
                foreach (var x in groups)
                {
                    if (x.Offset == offset && Equals(x.Easing, easing) && x.Composite == composite)
                    {
                        g = x;
                        break;
                    }
                }
                if (g is null) groups.Add(g = new KeyframeGroup(offset, easing, composite));
                g.Declarations.AddRange(decls);
            }
        }
        // 按位置稳定排序（同一位置保持出现顺序）
        var sorted = new List<KeyframeGroup>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            int k = sorted.Count;
            while (k > 0 && sorted[k - 1].Offset > groups[i].Offset) k--;
            sorted.Insert(k, groups[i]);
        }
        foreach (var g in sorted)
        {
            g.Seal();
            foreach (var d in g.Frozen)
            {
                if (d.IsCustom) custom.Add(d.CustomName!);
                else ids.Add(d.Id);
            }
        }
        Groups = sorted;
        var idList = new List<CssPropertyId>(ids);
        idList.Sort();
        Properties = idList;
        var customList = new List<string>(custom);
        customList.Sort(StringComparer.Ordinal);
        CustomProperties = customList;
    }

    /// <summary>源规则。</summary>
    public CssKeyframesRule Rule { get; }

    /// <summary>名称。</summary>
    public string Name => Rule.Name;

    /// <summary>关键帧中出现的（可动画的）长属性。</summary>
    public IReadOnlyList<CssPropertyId> Properties { get; }

    /// <summary>关键帧中出现的自定义属性。</summary>
    public IReadOnlyList<string> CustomProperties { get; }

    internal IReadOnlyList<KeyframeGroup> Groups { get; }
}

/// <summary>同一位置、同一缓动与合成方式的关键帧（合并后的声明，后出现的优先）。</summary>
internal sealed class KeyframeGroup
{
    public KeyframeGroup(double offset, CssEasingFunction? easing, CssCompositeOperation? composite)
    {
        Offset = offset;
        Easing = easing;
        Composite = composite;
    }

    public double Offset { get; }

    /// <summary>该帧到下一帧的缓动；null 表示用 <c>animation-timing-function</c>。</summary>
    public CssEasingFunction? Easing { get; }

    /// <summary>该帧的合成方式；null 表示用 <c>animation-composition</c>。</summary>
    public CssCompositeOperation? Composite { get; }

    public readonly List<ParsedDeclaration> Declarations = new();

    /// <summary>去重后的声明（每个属性只保留最后一个，按出现顺序）。</summary>
    public ParsedDeclaration[] Frozen { get; private set; } = Array.Empty<ParsedDeclaration>();

    public void Seal()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new List<ParsedDeclaration>(Declarations.Count);
        for (int i = Declarations.Count - 1; i >= 0; i--)
        {
            var d = Declarations[i];
            if (seen.Add(d.IsCustom ? d.CustomName! : CssProperties.Get(d.Id).Name)) keep.Add(d);
        }
        keep.Reverse();
        Frozen = keep.ToArray();
    }

    public bool Declares(CssPropertyId id)
    {
        foreach (var d in Frozen) if (!d.IsCustom && d.Id == id) return true;
        return false;
    }

    public bool Declares(string custom)
    {
        foreach (var d in Frozen) if (d.IsCustom && d.CustomName == custom) return true;
        return false;
    }
}

/// <summary>级联中胜出层级高于动画 / 过渡槽的属性（<c>!important</c> 声明不被动画覆盖；DevTools 覆盖不被过渡覆盖）。</summary>
internal sealed class CascadeLevelSink
{
    public readonly List<CssPropertyId> AboveAnimation = new();
    public readonly List<CssPropertyId> AboveTransition = new();

    public void Clear()
    {
        AboveAnimation.Clear();
        AboveTransition.Clear();
    }
}

/// <summary>需要在级联计算中途替换的动画值：其他属性的计算依赖它们（em、lh、var()）。</summary>
internal sealed class EarlyAnimatedValues
{
    public object? FontSize;
    public object? LineHeight;
    public Dictionary<string, CustomPropertyValue>? Custom;

    public bool IsEmpty => FontSize is null && LineHeight is null && (Custom is null || Custom.Count == 0);
}
