using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Text.OpenType;
using AwAUI.Text.OpenType.Layout;
using AwAUI.Text.Shaping;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>
/// OpenType 整形器（design/19「整形器插件」中的通用整形引擎）：cmap 映射 → 文字整形器的掩码设置 → GSUB → 前进量（hmtx）→ GPOS → 附着偏移。
/// 流程与默认特性与 HarfBuzz 的 hb-ot 整形一致：
/// <list type="bullet">
/// <item>GSUB 查找类型 1–8（单一、多重、替代、连字、上下文 1–3、链式上下文 1–3、扩展、反向链式），查找标志（忽略基字形 / 连字 / 标记、标记附着类、GDEF 标记过滤集）。</item>
/// <item>GPOS 查找类型 1–9（单一、成对含类别字距、草书、标记到基字形、标记到连字、标记到标记、上下文、链式上下文、扩展）；设备表被忽略。</item>
/// <item>默认特性：rvrn、ltra/ltrm 或 rtla、ccmp、locl、rlig、calt、clig、liga、rclt、curs、dist、kern、mark、mkmk、abvm、blwm；
/// GPOS 没有 'kern' 时使用旧式 'kern' 表。<see cref="ShapeRequest.Features"/> 可开关任意特性（值 0 关闭，1 开启，大于 1 选择替代字形），可限定范围。</item>
/// <item>阿拉伯式连写（init / medi / fina / isol 状态机，含 ZWJ / ZWNJ 与透明标记；字体没有这些特性时用 Unicode 表现形式回退）；希伯来文的标记宽度在 GPOS 之前清零。</item>
/// </list>
/// 不处理：Indic / USE / 泰文 / 藏文等需要音节划分与重排的文字（按默认整形器处理：cmap + 默认特性，不重排，结果可能不正确）；
/// Unicode 规范分解与组合（只做组合标记的规范排序：字体缺少预组合字形时不分解，有预组合字形时也不组合）；
/// 没有 GPOS 时的回退标记定位只做水平居中（HarfBuzz 另按组合类调整竖直位置）；AAT（morx / kerx / trak）；
/// 可变字体的 FeatureVariations 与非默认实例；竖排。
/// 簇值按 HarfBuzz 的默认级别（单调字素）：组合标记并入基字符的簇，连字取组件的最小簇值。
/// 非 <see cref="OpenTypeFace"/> 的字型退回 <see cref="BasicTextShaper"/>。线程安全。
/// </summary>
public sealed class OpenTypeShaper : TextShaper
{
    public static OpenTypeShaper Instance { get; } = new();

    sealed class Scratch
    {
        public readonly GlyphBuffer Buffer = new();
        public readonly List<(uint Tag, uint Value, bool Global)> Features = new();
        public readonly List<(uint Tag, uint Value, int Start, int End)> Ranged = new();
        public int[] Next = Array.Empty<int>();
        public long[] Prefix = Array.Empty<long>();
    }

    [ThreadStatic] static Scratch? t_scratch;

    public override ShapedRun Shape(in ShapeRequest request)
    {
        if (request.Face is not OpenTypeFace face) return BasicTextShaper.Instance.Shape(request);
        var data = face.LayoutData;
        var scratch = t_scratch ??= new Scratch();
        t_scratch = null;   // 重入（例如自定义字型在回调中整形）时使用新的缓冲
        try
        {
            return ShapeCore(request, face, data, scratch);
        }
        finally
        {
            t_scratch = scratch;
        }
    }

    ShapedRun ShapeCore(in ShapeRequest request, OpenTypeFace face, OtFaceData data, Scratch scratch)
    {
        var range = request.Range;
        var text = request.Text.Slice(0, range.Max);
        bool rtl = request.RightToLeft;

        // ---- 特性设置：全局（覆盖整个范围）与范围设置
        var userFeatures = scratch.Features;
        var ranged = scratch.Ranged;
        userFeatures.Clear();
        ranged.Clear();
        foreach (var f in request.Features)
        {
            bool global = f.Start <= range.Min && f.End >= range.Max;
            if (!global && (f.End <= range.Min || f.Start >= range.Max || f.End <= f.Start)) continue;   // 与整形范围不相交
            userFeatures.Add((f.Tag, f.Value, global));
            if (!global) ranged.Add((f.Tag, f.Value, f.Start, f.End));
        }
        var plan = data.GetPlan(request.Script, LanguageTags.ToOpenType(request.Language), rtl, CollectionsSpan(userFeatures));

        // ---- 码点 → 字形
        var b = scratch.Buffer;
        int n0 = range.Length;
        b.Reset(n0);
        b.RightToLeft = rtl;
        b.MaxLength = Math.Max(4096, 8 * n0);
        // 操作预算按文本长度线性给出（每字符 8192 步）：真实的重型字体（DecoType Nastaleeq Urdu）每字符约 1200–1600 步，
        // 旧的 1024 / 字符会在长段落中途耗尽、静默地留下未整形的字形；下限只照顾很短的文本，避免逐词整形时恶意字体的固定开销累加
        b.MaxOps = (int)Math.Min(int.MaxValue, Math.Max(1L << 16, 8192L * n0));
        var gdef = data.Gdef;
        bool gdefClasses = gdef is { HasGlyphClasses: true };
        bool prevZwj = false, anyMarks = false;
        for (int i = range.Min; i < text.Length;)
        {
            int cp = CodePoint.Decode(text, i, out int len);
            if ((uint)(cp - 0xD800) <= 0x7FF) cp = CodePoint.ReplacementCharacter;
            var info = new GlyphInfo { CodePoint = cp, Cluster = i, Mask = plan.GlobalMask };
            UFlags fl = 0;
            if (BasicTextShaper.IsDefaultIgnorable(cp))
            {
                fl |= UFlags.DefaultIgnorable;
                if (cp == 0x200D) fl |= UFlags.Zwj;
                else if (cp == 0x200C) fl |= UFlags.Zwnj;
            }
            if (cp >= 0x300)
            {
                var gc = UnicodeData.GetGeneralCategory(cp);
                if (gc == GeneralCategory.NonspacingMark) fl |= UFlags.NonspacingMark;
                // 延续字符（与 HarfBuzz 相同）：组合标记、ZWJ（及其后的扩展象形字符）、emoji 肤色修饰符、半角浊点、标签字符
                bool continuation = gc is GeneralCategory.NonspacingMark or GeneralCategory.SpacingMark or GeneralCategory.EnclosingMark
                    || cp == 0x200D || cp >= 0x1F3FB && cp <= 0x1F3FF || cp is 0xFF9E or 0xFF9F || cp >= 0xE0020 && cp <= 0xE007F
                    || prevZwj && UnicodeData.IsExtendedPictographic(cp);
                if (b.Len > 0 && continuation)
                {
                    fl |= UFlags.Continuation;
                    info.Cluster = b.Info[b.Len - 1].Cluster;   // 并入前一字符的簇
                }
            }
            prevZwj = cp == 0x200D;
            if (cp >= 0x300)
            {
                info.Ccc = MarkOrdering.ModifiedCombiningClass(cp);
                anyMarks |= info.Ccc != 0;
            }
            info.Flags = fl;
            face.TryGetGlyph(cp, out info.Glyph);
            if (gdefClasses) info.Props = gdef!.GetGlyphProps(info.Glyph);
            else info.Props = (fl & UFlags.NonspacingMark) != 0 && (fl & UFlags.DefaultIgnorable) == 0 ? GlyphProps.Mark : GlyphProps.BaseGlyph;
            b.Add(info);
            i += len;
        }

        // ---- 组合标记的规范排序（按修正后的组合类稳定排序；阿拉伯文另把修饰性组合标记移到前面），与 HarfBuzz 规范化的第二轮相同
        if (anyMarks) MarkOrdering.Reorder(b, plan.Kind == ScriptShaperKind.Arabic);

        // ---- 掩码
        if (plan.Kind == ScriptShaperKind.Arabic)
            ArabicShaping.SetupMasks(b, plan.ArabicMasks, request.Text.Slice(0, range.Min), request.Text.Slice(range.Max));
        if (ranged.Count > 0) ApplyRangedFeatures(plan, b, ranged, scratch);

        // ---- GSUB（缓冲的字形摘要与查找的覆盖摘要不相交时整条查找跳过；替换后重算摘要）
        var ctx = new OtApplyContext(b, gdef);
        var digest = BufferDigest(b);
        if (data.Gsub is { } gsub)
        {
            ctx.SetTable(gsub, false);
            foreach (var stage in plan.GsubStages)
                foreach (var l in stage)
                {
                    if (!l.Lookup.Digest.MayIntersect(digest)) continue;
                    if (ApplyLookup(ctx, l)) digest = BufferDigest(b);
                }
        }
        if (plan.ArabicFallback)
        {
            ArabicFallback(face, b, gdef);
            digest = BufferDigest(b);
        }

        int len2 = b.Len;
        var infos = b.Info;

        // ---- 定位
        b.ClearPositions();
        var pos = b.Pos;
        for (int i = 0; i < len2; i++) pos[i].XAdvance = face.GetAdvance(infos[i].Glyph);

        bool adjust = plan.AdjustMarkOffsetsWhenZeroing && !rtl;
        if (plan.Kind == ScriptShaperKind.Hebrew) ZeroMarkWidths(b, adjust);
        bool attachments = false;
        if (plan.ApplyGpos && data.Gpos is { } gpos)
        {
            ctx.SetTable(gpos, true);
            foreach (var l in plan.GposLookups)
                if (l.Lookup.Digest.MayIntersect(digest)) ApplyLookup(ctx, l);
            for (int i = 0; i < len2 && !attachments; i++) attachments = pos[i].AttachChain != 0;
        }
        if (plan.Kind != ScriptShaperKind.Hebrew) ZeroMarkWidths(b, adjust);
        if (!plan.ApplyGpos) FallbackMarkPositions(face, b, rtl);
        // 默认可忽略字符：GPOS 之后才变为不可见的零宽字形（之前它们参与 ZWJ / ZWNJ 的匹配规则）
        ushort space = data.SpaceGlyph;
        for (int i = 0; i < len2; i++)
            if (infos[i].IsDefaultIgnorable)
            {
                infos[i].Flags |= UFlags.Hidden;
                infos[i].Glyph = space;
                pos[i].XAdvance = 0;
                pos[i].XOffset = pos[i].YOffset = 0;
            }
        if (attachments) PropagateAttachments(b, rtl, scratch);

        if (rtl) b.Reverse();
        if (plan.ApplyKernTable) ApplyKernTable(face, b, plan.KernMask);

        // ---- 输出
        float scale = face.Metrics.GetScale(request.Size);
        var glyphs = new ushort[len2];
        var clusters = new int[len2];
        var advances = new Vector2[len2];
        var offsets = new Vector2[len2];
        var flags = new GlyphFlags[len2];
        for (int i = 0; i < len2; i++)
        {
            glyphs[i] = infos[i].Glyph;
            clusters[i] = infos[i].Cluster;
            advances[i] = new Vector2(pos[i].XAdvance * scale, 0);
            offsets[i] = new Vector2(pos[i].XOffset * scale, -pos[i].YOffset * scale);
            GlyphFlags gf = 0;
            if ((infos[i].Flags & UFlags.Hidden) != 0) gf |= GlyphFlags.Hidden;
            if ((infos[i].Flags & UFlags.UnsafeToBreak) != 0) gf |= GlyphFlags.UnsafeToBreak;
            flags[i] = gf;
        }
        return new ShapedRun(face, request.Size, range, rtl, glyphs, clusters, advances, offsets, flags, owned: true);
    }

    static ReadOnlySpan<(uint Tag, uint Value, bool Global)> CollectionsSpan(List<(uint Tag, uint Value, bool Global)> list)
    {
        if (list.Count == 0) return default;
        return list.ToArray();
    }

    static GlyphDigest BufferDigest(GlyphBuffer b)
    {
        var d = new GlyphDigest();
        var infos = b.Info;
        for (int i = 0; i < b.Len; i++) d.Add(infos[i].Glyph);
        return d;
    }

    /// <summary>应用一个查找；返回是否改变了缓冲（GSUB 时用于重算字形摘要）。</summary>
    static bool ApplyLookup(OtApplyContext ctx, in PlannedLookup l)
    {
        var lookup = l.Lookup;
        if (lookup.Broken) return false;
        ctx.LookupMask = l.Mask;
        ctx.AutoZwj = l.AutoZwj;
        ctx.AutoZwnj = l.AutoZwnj;
        try
        {
            return ctx.ApplyLookup(lookup);
        }
        catch (InvalidFontException)
        {
            // 损坏的查找：此后跳过（缓冲可能处于一遍查找的中途，按已输出的内容继续）
            lookup.Broken = true;
            ctx.RecoverAfterError();
            return true;
        }
    }

    /// <summary>
    /// 范围特性：同一标签的多个设置中后出现的覆盖先出现的。按设置的逆序处理，已设置的字形用并查集跳过，
    /// 整体为 O((字形数 + 设置数) · α)，而不是逐设置扫描的二次方。
    /// </summary>
    static void ApplyRangedFeatures(ShapePlan plan, GlyphBuffer b, List<(uint Tag, uint Value, int Start, int End)> ranged, Scratch scratch)
    {
        int n = b.Len;
        var infos = b.Info;
        if (scratch.Next.Length < n + 1) scratch.Next = new int[Math.Max(n + 1, scratch.Next.Length * 2)];
        var next = scratch.Next;
        var done = new HashSet<uint>();
        for (int k = ranged.Count - 1; k >= 0; k--)
        {
            uint tag = ranged[k].Tag;
            if (!done.Add(tag)) continue;
            if (plan.FindFeature(tag) is not { } fm) continue;
            for (int i = 0; i <= n; i++) next[i] = i;
            for (int r = k; r >= 0; r--)
            {
                var (t, value, start, end) = ranged[r];
                if (t != tag) continue;
                uint bits = (value << fm.Shift) & fm.Mask;
                int i0 = LowerBound(infos, n, start);
                int i1 = LowerBound(infos, n, end);
                for (int i = Find(next, i0); i < i1; i = Find(next, i))
                {
                    infos[i].Mask = (infos[i].Mask & ~fm.Mask) | bits;
                    next[i] = i + 1;
                }
            }
        }

        static int Find(int[] next, int i)
        {
            int root = i;
            while (next[root] != root) root = next[root];
            while (next[i] != root)
            {
                int t = next[i];
                next[i] = root;
                i = t;
            }
            return root;
        }

        static int LowerBound(GlyphInfo[] infos, int n, int cluster)
        {
            int lo = 0, hi = n;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (infos[mid].Cluster < cluster) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }

    static void ZeroMarkWidths(GlyphBuffer b, bool adjustOffsets)
    {
        var infos = b.Info;
        var pos = b.Pos;
        for (int i = 0; i < b.Len; i++)
            if (infos[i].IsMark)
            {
                if (adjustOffsets) pos[i].XOffset -= pos[i].XAdvance;
                pos[i].XAdvance = 0;
            }
    }

    /// <summary>
    /// 没有 GPOS 的字体：标记水平居中到所属基字形的墨迹上方 / 下方（竖直位置保持字体的设计位置）。
    /// HarfBuzz 另外按组合类调整竖直位置；这里没有组合类数据，只做水平居中。
    /// </summary>
    static void FallbackMarkPositions(OpenTypeFace face, GlyphBuffer b, bool rtl)
    {
        var infos = b.Info;
        var pos = b.Pos;
        int n = b.Len;
        int baseIndex = -1;
        bool any = false;
        for (int i = 0; i < n && !any; i++) any = infos[i].IsMark;
        if (!any) return;
        // 逻辑顺序中每个字形的视觉 x（LTR 为前缀和，RTL 为后缀和）
        var x = new long[n];
        long acc = 0;
        if (!rtl)
            for (int i = 0; i < n; i++)
            {
                x[i] = acc;
                acc += pos[i].XAdvance;
            }
        else
            for (int i = n - 1; i >= 0; i--)
            {
                x[i] = acc;
                acc += pos[i].XAdvance;
            }
        for (int i = 0; i < n; i++)
        {
            if (!infos[i].IsMark)
            {
                baseIndex = i;
                continue;
            }
            if (baseIndex < 0 || infos[baseIndex].Cluster != infos[i].Cluster) continue;
            if (!face.TryGetGlyphBounds(infos[baseIndex].Glyph, out var bb) || !face.TryGetGlyphBounds(infos[i].Glyph, out var mb)) continue;
            float baseCenter = x[baseIndex] + pos[baseIndex].XOffset + (bb.Left + bb.Right) / 2;
            float markCenter = x[i] + (mb.Left + mb.Right) / 2;
            pos[i].XOffset = (int)Math.Round(baseCenter - markCenter);
        }
    }

    /// <summary>
    /// 把附着字形的偏移累加到其附着目标上（HarfBuzz propagate_attachment_offsets）。
    /// 中间字形的前进量用前缀和求得（线性），链深度限制为 64。
    /// </summary>
    static void PropagateAttachments(GlyphBuffer b, bool rtl, Scratch scratch)
    {
        int n = b.Len;
        var pos = b.Pos;
        if (scratch.Prefix.Length < n + 1) scratch.Prefix = new long[Math.Max(n + 1, scratch.Prefix.Length * 2)];
        var prefix = scratch.Prefix;
        prefix[0] = 0;
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + pos[i].XAdvance;
        Span<int> path = stackalloc int[64];
        for (int start = 0; start < n; start++)
        {
            if (pos[start].AttachChain == 0) continue;
            // 沿链收集尚未处理的祖先
            int depth = 0;
            int cur = start;
            while (depth < path.Length && pos[cur].AttachChain != 0)
            {
                int j = cur + pos[cur].AttachChain;
                if ((uint)j >= (uint)n) break;
                path[depth++] = cur;
                cur = j;
                if (cur == start) break;   // 环：放弃
            }
            for (int k = depth - 1; k >= 0; k--)
            {
                int i = path[k];
                int chain = pos[i].AttachChain;
                if (chain == 0) continue;
                int j = i + chain;
                byte type = pos[i].AttachType;
                pos[i].AttachChain = 0;
                if ((uint)j >= (uint)n) continue;
                if ((type & GlyphPos.AttachCursive) != 0) pos[i].YOffset += pos[j].YOffset;
                else
                {
                    // 标记：竖直偏移已在附着时解析（见 OtApplyContext.MarkArrayApply），这里只累加水平偏移与中间字形的前进量
                    pos[i].XOffset += pos[j].XOffset;
                    if (j < i)
                    {
                        if (!rtl) pos[i].XOffset -= (int)(prefix[i] - prefix[j]);
                        else pos[i].XOffset += (int)(prefix[i + 1] - prefix[j + 1]);
                    }
                    else if (j > i)
                    {
                        if (!rtl) pos[i].XOffset += (int)(prefix[j] - prefix[i]);
                        else pos[i].XOffset -= (int)(prefix[j + 1] - prefix[i + 1]);
                    }
                }
            }
            // 超过深度的部分直接断开
            for (int k = 0; k < depth; k++) pos[path[k]].AttachChain = 0;
        }
    }

    /// <summary>旧式 'kern' 表：视觉顺序的相邻字形对（跳过标记与不可见字形），值加在左侧字形上。</summary>
    static void ApplyKernTable(OpenTypeFace face, GlyphBuffer b, uint kernMask)
    {
        var infos = b.Info;
        var pos = b.Pos;
        int prev = -1;
        for (int i = 0; i < b.Len; i++)
        {
            if (infos[i].IsMark || (infos[i].Flags & UFlags.Hidden) != 0) continue;
            if (prev >= 0 && (infos[prev].Mask & kernMask) != 0 && (infos[i].Mask & kernMask) != 0)
            {
                int k = face.GetKerning(infos[prev].Glyph, infos[i].Glyph);
                if (k != 0)
                {
                    pos[prev].XAdvance += k;
                    infos[b.RightToLeft ? prev : i].Flags |= UFlags.UnsafeToBreak;
                }
            }
            prev = i;
        }
    }

    /// <summary>字体没有阿拉伯形式特性时：按连写动作换成 Unicode 表现形式字形，并合成 lam-alef 连字。</summary>
    static void ArabicFallback(OpenTypeFace face, GlyphBuffer b, GdefTable? gdef)
    {
        var infos = b.Info;
        int n = b.Len;
        for (int i = 0; i < n; i++)
        {
            ref var info = ref infos[i];
            if (info.JoiningAction == ArabicShaping.None || (info.Props & GlyphProps.Substituted) != 0) continue;
            int form = ArabicShaping.PresentationForm(info.CodePoint, info.JoiningAction);
            if (form != 0 && face.TryGetGlyph(form, out var g))
            {
                info.Glyph = g;
                info.Props |= GlyphProps.Substituted;
            }
        }
        // lam-alef：lam（init / medi）后紧跟 alef（fina），可有透明标记之外的其他字符则不合成
        int w = 0;
        for (int r = 0; r < n; r++)
        {
            var cur = infos[r];
            if (cur.CodePoint == 0x0644 && r + 1 < n && (cur.JoiningAction is ArabicShaping.Init or ArabicShaping.Medi))
            {
                var next = infos[r + 1];
                int lig = ArabicShaping.LamAlef(next.CodePoint, cur.JoiningAction == ArabicShaping.Medi);
                if (lig != 0 && next.JoiningAction == ArabicShaping.Fina && face.TryGetGlyph(lig, out var g))
                {
                    cur.Glyph = g;
                    cur.Cluster = Math.Min(cur.Cluster, next.Cluster);
                    cur.Props = (ushort)((cur.Props & GlyphProps.Preserve) | GlyphProps.Ligature | GlyphProps.Ligated | GlyphProps.Substituted);
                    infos[w++] = cur;
                    r++;
                    continue;
                }
            }
            infos[w++] = cur;
        }
        b.Len = w;
        _ = gdef;
    }

    // ------------------------------------------------------------------ 词边界字距

    /// <summary>
    /// 两段分别整形的文本在边界处相邻时应插入的额外间距（逻辑像素）：GPOS 'kern' 特性中的成对定位（第一个字形的 XAdvance），
    /// 没有时用旧式 'kern' 表。只处理从左到右的文本；从右到左时返回 0（RTL 字距通常由放置量与前进量共同表达，无法在边界处单独应用）。
    /// </summary>
    public override float GetBoundaryKerning(in ShapeRequest request, ushort first, ushort second)
    {
        if (request.RightToLeft || request.Face is not OpenTypeFace face) return 0;
        var data = face.LayoutData;
        var scratch = t_scratch ??= new Scratch();
        var userFeatures = scratch.Features;
        userFeatures.Clear();
        foreach (var f in request.Features)
            if (f.Start <= 0 && f.End == int.MaxValue) userFeatures.Add((f.Tag, f.Value, true));
        var plan = data.GetPlan(request.Script, LanguageTags.ToOpenType(request.Language), false, CollectionsSpan(userFeatures));
        int total = 0;
        if (plan.PairKernLookups.Length > 0 && data.Gpos is { } gpos)
        {
            foreach (var l in plan.PairKernLookups)
            {
                if (l.Broken) continue;
                try
                {
                    if (OtApplyContext.TryGetPairAdjustment(gpos, l, first, second, out int adv)) total += adv;
                }
                catch (InvalidFontException)
                {
                    l.Broken = true;
                }
            }
        }
        else if (plan.ApplyKernTable) total = face.GetKerning(first, second);
        return total * face.Metrics.GetScale(request.Size);
    }
}
