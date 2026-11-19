using System;
using System.Collections.Generic;
using AwAUI.Text.Shaping;
using AwAUI.Unicode;

namespace AwAUI.Text.OpenType.Layout;

/// <summary>一个字型的 OpenType 布局数据（GDEF / GSUB / GPOS / kern）与整形计划缓存。损坏的表视为不存在。线程安全。</summary>
internal sealed class OtFaceData
{
    const int MaxPlans = 64;

    readonly Dictionary<PlanKey, ShapePlan> _plans = new();
    readonly object _lock = new();

    public OtFaceData(OpenTypeFace face)
    {
        Face = face;
        Gdef = TryParse(face, OpenTypeTag.Make("GDEF"), d => GdefTable.Parse(d));
        Gsub = TryParse(face, OpenTypeTag.Make("GSUB"), d => LayoutTable.Parse(d, false));
        Gpos = TryParse(face, OpenTypeTag.Make("GPOS"), d => LayoutTable.Parse(d, true));
        Kern = face.Kern;
        face.TryGetGlyph(' ', out var sp);
        SpaceGlyph = sp;
    }

    public OpenTypeFace Face { get; }
    public GdefTable? Gdef { get; }
    public LayoutTable? Gsub { get; }
    public LayoutTable? Gpos { get; }
    public KernTable? Kern { get; }
    public ushort SpaceGlyph { get; }

    static T? TryParse<T>(OpenTypeFace face, uint tag, Func<ReadOnlyMemory<byte>, T> parse) where T : class
    {
        var data = face.GetTable(tag);
        if (data.IsEmpty) return null;
        try
        {
            return parse(data);
        }
        catch (InvalidFontException)
        {
            return null;
        }
    }

    readonly record struct PlanKey(Script Script, uint Lang, bool Rtl, string? Features);

    [ThreadStatic] static OtFaceData? t_lastData;
    [ThreadStatic] static PlanKey t_lastKey;
    [ThreadStatic] static ShapePlan? t_lastPlan;

    public ShapePlan GetPlan(Script script, uint langTag, bool rtl, ReadOnlySpan<(uint Tag, uint Value, bool Global)> features)
    {
        // 快速路径：同一线程连续整形同一字型、同一设置（逐词整形的常见情况）不加锁、不查字典
        if (features.Length == 0 && ReferenceEquals(t_lastData, this) && t_lastKey == new PlanKey(script, langTag, rtl, null) && t_lastPlan is { } last)
            return last;
        var plan = GetPlanSlow(script, langTag, rtl, features);
        if (features.Length == 0)
        {
            t_lastData = this;
            t_lastKey = new PlanKey(script, langTag, rtl, null);
            t_lastPlan = plan;
        }
        return plan;
    }

    ShapePlan GetPlanSlow(Script script, uint langTag, bool rtl, ReadOnlySpan<(uint Tag, uint Value, bool Global)> features)
    {
        string? sig = null;
        if (features.Length > 0)
        {
            var sb = new System.Text.StringBuilder(features.Length * 12);
            foreach (var (tag, value, global) in features) sb.Append(tag).Append('=').Append(value).Append(global ? 'g' : 'r').Append(';');
            sig = sb.ToString();
        }
        var key = new PlanKey(script, langTag, rtl, sig);
        lock (_lock)
            if (_plans.TryGetValue(key, out var p)) return p;
        ShapePlan plan;
        try
        {
            plan = ShapePlan.Create(this, script, langTag, rtl, features);
        }
        catch (InvalidFontException)
        {
            plan = ShapePlan.Create(Empty, script, langTag, rtl, features);
        }
        lock (_lock)
        {
            if (_plans.Count >= MaxPlans) _plans.Clear();
            _plans[key] = plan;
        }
        return plan;
    }

    OtFaceData(OpenTypeFace face, bool empty)
    {
        Face = face;
        _ = empty;
    }

    OtFaceData? _empty;
    /// <summary>布局表损坏时使用的空数据（只做 cmap 映射与 hmtx 前进量）。</summary>
    OtFaceData Empty => _empty ??= new OtFaceData(Face, true);

    /// <summary>选择语言系统：首选文字标签 → 第二候选 → DFLT → dflt → latn。返回 LangSys 偏移，找不到时 −1。</summary>
    public int SelectLangSys(LayoutTable? table, uint scriptTag, uint scriptTag2, uint langTag)
    {
        if (table is null) return -1;
        int script = -1;
        if (scriptTag != 0) script = table.FindScript(scriptTag);
        if (script < 0 && scriptTag2 != 0) script = table.FindScript(scriptTag2);
        if (script < 0) script = table.FindScript(OpenTypeTag.Make("DFLT"));
        if (script < 0) script = table.FindScript(OpenTypeTag.Make("dflt"));
        if (script < 0) script = table.FindScript(OpenTypeTag.Make("latn"));
        if (script < 0) return -1;
        return table.FindLangSys(script, langTag);
    }

    [ThreadStatic] static List<int>? t_tmp;

    /// <summary>语言系统中具有给定标签的特性序号，没有时 −1。</summary>
    public int FindFeature(LayoutTable? table, int langSys, uint tag)
    {
        if (table is null || langSys < 0) return -1;
        var list = t_tmp ??= new List<int>();
        list.Clear();
        table.GetLangSysFeatures(langSys, list, out _);
        foreach (int fi in list)
            if (table.GetFeatureTag(fi) == tag) return fi;
        return -1;
    }

    public (int Index, uint Tag) RequiredFeature(LayoutTable? table, int langSys)
    {
        if (table is null || langSys < 0) return (-1, 0);
        var list = t_tmp ??= new List<int>();
        list.Clear();
        table.GetLangSysFeatures(langSys, list, out int required);
        return required < 0 ? (-1, 0) : (required, table.GetFeatureTag(required));
    }
}
