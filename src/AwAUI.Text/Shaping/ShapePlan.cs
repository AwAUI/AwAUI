using System;
using System.Collections.Generic;
using AwAUI.Text.OpenType;
using AwAUI.Text.OpenType.Layout;
using AwAUI.Unicode;

namespace AwAUI.Text.Shaping;

/// <summary>整形器种类（决定特性集合、掩码设置与标记宽度清零时机）。</summary>
internal enum ScriptShaperKind : byte
{
    Default,
    /// <summary>阿拉伯式连写（阿拉伯、叙利亚、N'Ko、蒙古等）：init / medi / fina / isol 状态机。</summary>
    Arabic,
    /// <summary>希伯来文：标记宽度在 GPOS 之前清零。</summary>
    Hebrew,
}

/// <summary>一个已选定的查找及其掩码与 ZWJ / ZWNJ 行为。</summary>
internal readonly struct PlannedLookup
{
    public PlannedLookup(int index, uint mask, bool autoZwj, bool autoZwnj, OtLookup lookup)
    {
        Index = index;
        Mask = mask;
        AutoZwj = autoZwj;
        AutoZwnj = autoZwnj;
        Lookup = lookup;
    }

    public int Index { get; }
    public uint Mask { get; }
    public bool AutoZwj { get; }
    public bool AutoZwnj { get; }
    public OtLookup Lookup { get; }
}

/// <summary>特性到掩码位的分配。</summary>
internal readonly struct FeatureMask
{
    public FeatureMask(uint tag, int shift, uint mask)
    {
        Tag = tag;
        Shift = shift;
        Mask = mask;
    }

    public uint Tag { get; }
    public int Shift { get; }
    public uint Mask { get; }
}

/// <summary>
/// 整形计划（对应 HarfBuzz 的 hb_ot_shape_plan_t）：按字型、文字、语言、方向与特性设置选定的 GSUB / GPOS 查找（分阶段）与掩码分配。
/// 不可变；按键缓存在字型的 <see cref="OtFaceData"/> 上。
/// </summary>
internal sealed class ShapePlan
{
    public const int GlobalBitShift = 31;
    public const uint GlobalBit = 1u << GlobalBitShift;
    const int MaxBitsPerFeature = 8;
    const uint MaxValue = (1u << MaxBitsPerFeature) - 1;

    public ScriptShaperKind Kind;
    public bool RightToLeft;
    public uint GlobalMask = GlobalBit;
    public PlannedLookup[][] GsubStages = Array.Empty<PlannedLookup[]>();
    public PlannedLookup[] GposLookups = Array.Empty<PlannedLookup>();
    public FeatureMask[] Features = Array.Empty<FeatureMask>();
    /// <summary>阿拉伯连写动作 → 掩码（下标为 <see cref="ArabicShaping"/> 的动作值）。</summary>
    public uint[] ArabicMasks = Array.Empty<uint>();
    /// <summary>阿拉伯文字体没有 init / medi / fina / isol 时，用 Unicode 表现形式回退。</summary>
    public bool ArabicFallback;
    public bool ApplyGpos;
    /// <summary>GPOS 中没有 'kern' 时使用旧式 'kern' 表。</summary>
    public bool ApplyKernTable;
    public uint KernMask;
    /// <summary>GPOS 'kern' 特性中的成对定位查找（供词边界的字距补偿使用）。</summary>
    public OtLookup[] PairKernLookups = Array.Empty<OtLookup>();
    /// <summary>字体没有 GPOS 时按 HarfBuzz 的做法在清零标记宽度时保留其相对位置。</summary>
    public bool AdjustMarkOffsetsWhenZeroing;

    public FeatureMask? FindFeature(uint tag)
    {
        foreach (var f in Features)
            if (f.Tag == tag) return f;
        return null;
    }

    // ------------------------------------------------------------------ 构建

    [Flags]
    enum FFlags : byte
    {
        None = 0,
        Global = 1,
        HasFallback = 2,
        ManualZwj = 4,
        ManualZwnj = 8,
    }

    struct FeatureInfo
    {
        public uint Tag;
        public int Seq;
        public uint MaxValue;
        public FFlags Flags;
        public uint DefaultValue;
        public int Stage0;
    }

    sealed class Builder
    {
        public readonly List<FeatureInfo> Infos = new();
        public int Stage0;

        public void Add(uint tag, FFlags flags, uint value = 1)
        {
            Infos.Add(new FeatureInfo
            {
                Tag = tag,
                Seq = Infos.Count,
                MaxValue = Math.Min(value, MaxValue),
                Flags = flags,
                DefaultValue = (flags & FFlags.Global) != 0 ? Math.Min(value, MaxValue) : 0,
                Stage0 = Stage0,
            });
        }

        public void Enable(uint tag, FFlags flags = FFlags.None, uint value = 1) => Add(tag, flags | FFlags.Global, value);

        public void GsubPause() => Stage0++;
    }

    static uint T(string s) => OpenTypeTag.Make(s);

    static readonly uint[] s_arabicFeatures = { T("isol"), T("fina"), T("fin2"), T("fin3"), T("medi"), T("med2"), T("init") };

    /// <summary>构建计划。用户特性：全局设置（覆盖整个整形范围）与范围设置（只分配掩码位，按范围设置字形掩码）。</summary>
    public static ShapePlan Create(OtFaceData data, Script script, uint langTag, bool rtl,
        ReadOnlySpan<(uint Tag, uint Value, bool Global)> userFeatures)
    {
        var plan = new ShapePlan { RightToLeft = rtl, Kind = KindOf(script) };
        var b = new Builder();

        // 与 HarfBuzz hb_ot_shape_collect_features 的顺序相同
        b.Enable(T("rvrn"));
        b.GsubPause();
        if (!rtl)
        {
            b.Enable(T("ltra"));
            b.Enable(T("ltrm"));
        }
        else b.Enable(T("rtla"));
        b.Add(T("frac"), FFlags.None);
        b.Add(T("numr"), FFlags.None);
        b.Add(T("dnom"), FFlags.None);

        if (plan.Kind == ScriptShaperKind.Arabic)
        {
            b.Enable(T("stch"));
            b.GsubPause();
            b.Enable(T("ccmp"), FFlags.ManualZwj);
            b.Enable(T("locl"), FFlags.ManualZwj);
            b.GsubPause();
            bool fallback = script == Script.Arabic;
            foreach (var tag in s_arabicFeatures)
            {
                bool syriac = tag == T("fin2") || tag == T("fin3") || tag == T("med2");
                b.Add(tag, fallback && !syriac ? FFlags.HasFallback : FFlags.None);
                b.GsubPause();
            }
            b.GsubPause();
            b.Enable(T("rlig"), FFlags.ManualZwj | FFlags.HasFallback);
            if (fallback) b.GsubPause();
            b.Enable(T("calt"), FFlags.ManualZwj);
            // HarfBuzz 在此处检查的是计划中是否已有 rclt（此时总是没有）
            b.GsubPause();
            b.Enable(T("rclt"), FFlags.ManualZwj);
            b.Enable(T("liga"), FFlags.ManualZwj);
            b.Enable(T("clig"), FFlags.ManualZwj);
            b.Enable(T("mset"), FFlags.ManualZwj);
        }

        // 公共特性
        b.Enable(T("abvm"));
        b.Enable(T("blwm"));
        b.Enable(T("ccmp"));
        b.Enable(T("locl"));
        b.Enable(T("mark"), FFlags.ManualZwj);
        b.Enable(T("mkmk"), FFlags.ManualZwj);
        b.Enable(T("rlig"));
        // 水平特性
        b.Enable(T("calt"));
        b.Enable(T("clig"));
        b.Enable(T("curs"));
        b.Enable(T("dist"));
        b.Enable(T("kern"), FFlags.HasFallback);
        b.Enable(T("liga"));
        b.Enable(T("rclt"));

        foreach (var (tag, value, global) in userFeatures)
            b.Add(tag, global ? FFlags.Global : FFlags.None, value);

        Compile(plan, data, b, script, langTag);
        return plan;
    }

    internal static ScriptShaperKind KindOf(Script script) => script switch
    {
        Script.Arabic or Script.Syriac or Script.Nko or Script.Mandaic or Script.Manichaean or Script.Mongolian or Script.PhagsPa
            or Script.PsalterPahlavi or Script.Adlam or Script.HanifiRohingya or Script.Sogdian or Script.OldUyghur or Script.Chorasmian
            => ScriptShaperKind.Arabic,
        Script.Hebrew => ScriptShaperKind.Hebrew,
        _ => ScriptShaperKind.Default,
    };

    static void Compile(ShapePlan plan, OtFaceData data, Builder b, Script script, uint langTag)
    {
        var gsub = data.Gsub;
        var gpos = data.Gpos;
        uint scriptTag = ScriptTags.ToOpenType(script), scriptTag2 = ScriptTags.Fallback(script);
        int gsubLang = data.SelectLangSys(gsub, scriptTag, scriptTag2, langTag);
        int gposLang = data.SelectLangSys(gpos, scriptTag, scriptTag2, langTag);

        // 排序并合并重复的特性（后出现的全局设置覆盖之前的设置）
        var infos = b.Infos;
        infos.Sort((x, y) => x.Tag != y.Tag ? x.Tag.CompareTo(y.Tag) : x.Seq.CompareTo(y.Seq));
        var merged = new List<FeatureInfo>(infos.Count);
        foreach (var fi in infos)
        {
            if (merged.Count == 0 || merged[merged.Count - 1].Tag != fi.Tag)
            {
                merged.Add(fi);
                continue;
            }
            var j = merged[merged.Count - 1];
            if ((fi.Flags & FFlags.Global) != 0)
            {
                j.Flags |= FFlags.Global;
                j.MaxValue = fi.MaxValue;
                j.DefaultValue = fi.DefaultValue;
            }
            else
            {
                j.Flags &= ~FFlags.Global;
                j.MaxValue = Math.Max(j.MaxValue, fi.MaxValue);
            }
            j.Flags |= fi.Flags & FFlags.HasFallback;
            j.Stage0 = Math.Min(j.Stage0, fi.Stage0);
            merged[merged.Count - 1] = j;
        }

        var gsubRequired = data.RequiredFeature(gsub, gsubLang);
        var gposRequired = data.RequiredFeature(gpos, gposLang);
        int gsubRequiredStage = 0;

        int nextBit = 0;
        var features = new List<(FeatureInfo Info, FeatureMask Map, int GsubIndex, int GposIndex)>();
        foreach (var fi in merged)
        {
            int bits = (fi.Flags & FFlags.Global) != 0 && fi.MaxValue == 1 ? 0 : Math.Min(MaxBitsPerFeature, BitStorage(fi.MaxValue));
            if (fi.MaxValue == 0 || nextBit + bits >= GlobalBitShift) continue;
            if (gsubRequired.Tag == fi.Tag) gsubRequiredStage = fi.Stage0;
            int gi = data.FindFeature(gsub, gsubLang, fi.Tag);
            int pi = data.FindFeature(gpos, gposLang, fi.Tag);
            bool found = gi >= 0 || pi >= 0;
            if (!found && (fi.Flags & FFlags.HasFallback) == 0) continue;
            int shift;
            uint mask;
            if ((fi.Flags & FFlags.Global) != 0 && fi.MaxValue == 1)
            {
                shift = GlobalBitShift;
                mask = GlobalBit;
            }
            else
            {
                shift = nextBit;
                mask = (uint)((1L << (nextBit + bits)) - (1L << nextBit));
                nextBit += bits;
                plan.GlobalMask |= (fi.DefaultValue << shift) & mask;
            }
            features.Add((fi, new FeatureMask(fi.Tag, shift, mask), gi, pi));
        }
        var fmaps = new FeatureMask[features.Count];
        for (int i = 0; i < fmaps.Length; i++) fmaps[i] = features[i].Map;
        plan.Features = fmaps;

        // 查找：按阶段收集，每阶段内按查找序号排序并合并
        var lookups = new List<PlannedLookup>();
        var tmp = new List<int>();
        var stages = new List<PlannedLookup[]>();
        if (gsub is not null)
        {
            for (int stage = 0; stage <= b.Stage0; stage++)
            {
                lookups.Clear();
                if (gsubRequired.Index >= 0 && gsubRequiredStage == stage) AddLookups(gsub, gsubRequired.Index, GlobalBit, true, true, lookups, tmp);
                foreach (var f in features)
                    if (f.Info.Stage0 == stage && f.GsubIndex >= 0)
                        AddLookups(gsub, f.GsubIndex, f.Map.Mask, (f.Info.Flags & FFlags.ManualZwj) == 0, (f.Info.Flags & FFlags.ManualZwnj) == 0, lookups, tmp);
                if (lookups.Count > 0) stages.Add(SortMerge(lookups));
            }
        }
        plan.GsubStages = stages.ToArray();

        lookups.Clear();
        if (gpos is not null)
        {
            if (gposRequired.Index >= 0) AddLookups(gpos, gposRequired.Index, GlobalBit, true, true, lookups, tmp);
            foreach (var f in features)
                if (f.GposIndex >= 0)
                    AddLookups(gpos, f.GposIndex, f.Map.Mask, (f.Info.Flags & FFlags.ManualZwj) == 0, (f.Info.Flags & FFlags.ManualZwnj) == 0, lookups, tmp);
            plan.GposLookups = SortMerge(lookups);
        }

        // 'kern'
        uint kernTag = T("kern");
        bool gposKern = false;
        foreach (var f in features)
        {
            if (f.Info.Tag != kernTag) continue;
            plan.KernMask = f.Map.Mask;
            if (f.GposIndex >= 0 && gpos is not null)
            {
                gposKern = true;
                tmp.Clear();
                gpos.GetFeatureLookups(f.GposIndex, tmp);
                var pairs = new List<OtLookup>();
                foreach (int li in tmp)
                    if (gpos.GetLookup(li) is { Type: 2, Broken: false } l) pairs.Add(l);
                plan.PairKernLookups = pairs.ToArray();
            }
        }
        plan.ApplyGpos = gpos is not null && gpos.LookupCount > 0;
        plan.ApplyKernTable = !gposKern && data.Kern is not null && plan.KernMask != 0;
        plan.AdjustMarkOffsetsWhenZeroing = !plan.ApplyGpos;

        if (plan.Kind == ScriptShaperKind.Arabic)
        {
            var masks = new uint[ArabicShaping.ActionCount];
            bool fallback = script == Script.Arabic;
            for (int i = 0; i < s_arabicFeatures.Length; i++)
            {
                uint tag = s_arabicFeatures[i];
                (FeatureInfo Info, FeatureMask Map, int GsubIndex, int GposIndex)? found = null;
                foreach (var f in features)
                    if (f.Info.Tag == tag) found = f;
                if (found is { } ff) masks[ArabicShaping.ActionForFeatureIndex(i)] = (1u << ff.Map.Shift) & ff.Map.Mask;
                bool syriac = i is 2 or 3 or 5;
                bool needsFallback = found is null || found.Value.GsubIndex < 0;
                fallback = fallback && (syriac || needsFallback);
            }
            plan.ArabicMasks = masks;
            plan.ArabicFallback = fallback;
        }
    }

    static int BitStorage(uint v)
    {
        int n = 0;
        while (v != 0)
        {
            n++;
            v >>= 1;
        }
        return n;
    }

    static void AddLookups(LayoutTable table, int featureIndex, uint mask, bool autoZwj, bool autoZwnj, List<PlannedLookup> into, List<int> tmp)
    {
        tmp.Clear();
        try
        {
            table.GetFeatureLookups(featureIndex, tmp);
        }
        catch (InvalidFontException)
        {
            return;
        }
        foreach (int li in tmp)
        {
            var l = table.GetLookup(li);
            if (l is null || l.Broken || l.Type == 0) continue;
            into.Add(new PlannedLookup(li, mask, autoZwj, autoZwnj, l));
        }
    }

    static PlannedLookup[] SortMerge(List<PlannedLookup> list)
    {
        list.Sort((a, c) => a.Index.CompareTo(c.Index));
        var result = new List<PlannedLookup>(list.Count);
        foreach (var l in list)
        {
            if (result.Count > 0 && result[result.Count - 1].Index == l.Index)
            {
                var p = result[result.Count - 1];
                result[result.Count - 1] = new PlannedLookup(p.Index, p.Mask | l.Mask, p.AutoZwj && l.AutoZwj, p.AutoZwnj && l.AutoZwnj, p.Lookup);
            }
            else result.Add(l);
        }
        return result.ToArray();
    }
}

/// <summary>Unicode 文字 → OpenType 文字标签。</summary>
internal static class ScriptTags
{
    /// <summary>首选标签（Indic 文字为第二版标签，见 <see cref="Fallback"/>）；Common / Inherited / Unknown 返回 0。</summary>
    public static uint ToOpenType(Script script)
    {
        switch (script)
        {
            case Script.Common:
            case Script.Inherited:
            case Script.Unknown:
                return 0;
            case Script.Hiragana:
            case Script.Katakana:
                return OpenTypeTag.Make("kana");
            case Script.Lao:
                return OpenTypeTag.Make("lao ");
            case Script.Yi:
                return OpenTypeTag.Make("yi  ");
            case Script.Nko:
                return OpenTypeTag.Make("nko ");
            case Script.Vai:
                return OpenTypeTag.Make("vai ");
            case Script.Devanagari: return OpenTypeTag.Make("dev2");
            case Script.Bengali: return OpenTypeTag.Make("bng2");
            case Script.Gujarati: return OpenTypeTag.Make("gjr2");
            case Script.Gurmukhi: return OpenTypeTag.Make("gur2");
            case Script.Kannada: return OpenTypeTag.Make("knd2");
            case Script.Malayalam: return OpenTypeTag.Make("mlm2");
            case Script.Oriya: return OpenTypeTag.Make("ory2");
            case Script.Tamil: return OpenTypeTag.Make("tml2");
            case Script.Telugu: return OpenTypeTag.Make("tel2");
            case Script.Myanmar: return OpenTypeTag.Make("mym2");
        }
        // ISO 15924 代码的首字母小写
        return UnicodeData.GetScriptTag(script) | 0x20000000u;
    }

    /// <summary>第二候选标签（Indic 的旧版标签；其他文字为 0）。</summary>
    public static uint Fallback(Script script) => script switch
    {
        Script.Devanagari or Script.Bengali or Script.Gujarati or Script.Gurmukhi or Script.Kannada or Script.Malayalam
            or Script.Oriya or Script.Tamil or Script.Telugu or Script.Myanmar => UnicodeData.GetScriptTag(script) | 0x20000000u,
        _ => 0,
    };
}

/// <summary>BCP 47 语言标签 → OpenType 语言系统标签（只覆盖常用语言；未知语言用默认语言系统）。</summary>
internal static class LanguageTags
{
    static readonly Dictionary<string, string> s_map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ARA ", ["az"] = "AZE ", ["be"] = "BEL ", ["bg"] = "BGR ", ["bn"] = "BEN ", ["ca"] = "CAT ", ["cs"] = "CSY ",
        ["cy"] = "WEL ", ["da"] = "DAN ", ["de"] = "DEU ", ["el"] = "ELL ", ["en"] = "ENG ", ["es"] = "ESP ", ["et"] = "ETI ",
        ["eu"] = "EUQ ", ["fa"] = "FAR ", ["fi"] = "FIN ", ["fr"] = "FRA ", ["ga"] = "IRI ", ["gl"] = "GAL ", ["gu"] = "GUJ ",
        ["he"] = "IWR ", ["iw"] = "IWR ", ["hi"] = "HIN ", ["hr"] = "HRV ", ["hu"] = "HUN ", ["hy"] = "HYE ", ["id"] = "IND ",
        ["is"] = "ISL ", ["it"] = "ITA ", ["ja"] = "JAN ", ["ka"] = "KAT ", ["kk"] = "KAZ ", ["km"] = "KHM ", ["kn"] = "KAN ",
        ["ko"] = "KOR ", ["ku"] = "KUR ", ["ky"] = "KIR ", ["lt"] = "LTH ", ["lv"] = "LVI ", ["mk"] = "MKD ", ["ml"] = "MAL ",
        ["mn"] = "MNG ", ["mr"] = "MAR ", ["ms"] = "MLY ", ["mt"] = "MTS ", ["my"] = "BRM ", ["ne"] = "NEP ", ["nl"] = "NLD ",
        ["no"] = "NOR ", ["nb"] = "NOR ", ["nn"] = "NYN ", ["pa"] = "PAN ", ["pl"] = "PLK ", ["ps"] = "PAS ", ["pt"] = "PTG ",
        ["ro"] = "ROM ", ["mo"] = "MOL ", ["ru"] = "RUS ", ["sd"] = "SND ", ["sk"] = "SKY ", ["sl"] = "SLV ", ["sq"] = "SQI ",
        ["sr"] = "SRB ", ["sv"] = "SVE ", ["sw"] = "SWK ", ["ta"] = "TAM ", ["te"] = "TEL ", ["th"] = "THA ", ["tk"] = "TKM ",
        ["tr"] = "TRK ", ["tt"] = "TAT ", ["ug"] = "UYG ", ["uk"] = "UKR ", ["ur"] = "URD ", ["uz"] = "UZB ", ["vi"] = "VIT ",
        ["yi"] = "JII ", ["zh"] = "ZHS ",
    };

    [ThreadStatic] static string? t_lastLanguage;
    [ThreadStatic] static uint t_lastTag;

    public static uint ToOpenType(string? language)
    {
        if (string.IsNullOrEmpty(language)) return 0;
        if (ReferenceEquals(language, t_lastLanguage)) return t_lastTag;
        uint tag = Map(language!);
        t_lastLanguage = language;
        t_lastTag = tag;
        return tag;
    }

    static uint Map(string language)
    {
        var parts = language!.Replace('_', '-').Split('-');
        string primary = parts[0];
        if (primary.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in parts)
            {
                if (p.Equals("Hant", StringComparison.OrdinalIgnoreCase) || p.Equals("TW", StringComparison.OrdinalIgnoreCase))
                    return OpenTypeTag.Make("ZHT ");
                if (p.Equals("HK", StringComparison.OrdinalIgnoreCase) || p.Equals("MO", StringComparison.OrdinalIgnoreCase))
                    return OpenTypeTag.Make("ZHH ");
            }
            return OpenTypeTag.Make("ZHS ");
        }
        return s_map.TryGetValue(primary, out var tag) ? OpenTypeTag.Make(tag) : 0;
    }
}
