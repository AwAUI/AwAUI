using System;
using System.Numerics;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>
/// P1 的基础整形器：码点经 cmap 一对一映射为字形，前进量取自 hmtx，相邻字形应用 'kern' 表的成对字距。
/// 不执行 GSUB/GPOS，因此连字、标记定位与复杂文字的重排都不处理；适用于拉丁、西里尔、希腊与 CJK。
/// <see cref="DefaultTextEngine"/> 默认使用 <see cref="OpenTypeShaper"/>；本整形器用于测试引擎与需要最简单、最快路径的场合。
/// 默认可忽略字符（ZWJ、变体选择符、软连字符等）映射为零宽的空格字形。
/// </summary>
public sealed class BasicTextShaper : TextShaper
{
    public static BasicTextShaper Instance { get; } = new();

    public override ShapedRun Shape(in ShapeRequest request)
    {
        var face = request.Face;
        var text = request.Text.Slice(0, request.Range.Max);   // 不读取 Range 之后的上下文，避免代理对跨越边界
        int start = request.Range.Min;
        int capacity = request.Range.Length;

        var glyphs = new ushort[capacity];
        var clusters = new int[capacity];
        var advances = new int[capacity];
        var ignorable = new bool[capacity];
        int n = 0;

        ushort space = face.TryGetGlyph(' ', out var sp) ? sp : (ushort)0;
        for (int i = start; i < text.Length;)
        {
            int cp = CodePoint.Decode(text, i, out int len);
            // 孤立代理项按 U+FFFD 查找字形（AwAUI.Unicode 的解码保留代理码位本身，以保证偏移一一对应）
            if ((uint)(cp - 0xD800) <= 0x7FF) cp = CodePoint.ReplacementCharacter;
            ushort g;
            int advance;
            bool ignore = IsDefaultIgnorable(cp);
            if (ignore)
            {
                g = space;
                advance = 0;
            }
            else
            {
                face.TryGetGlyph(cp, out g);
                advance = face.GetAdvance(g);
            }
            glyphs[n] = g;
            clusters[n] = i;
            advances[n] = advance;
            ignorable[n] = ignore;
            n++;
            i += len;
        }

        // 视觉顺序：从右到左的文本反转字形序列（簇偏移随之递减）
        bool rtl = request.RightToLeft;
        if (rtl)
        {
            Array.Reverse(glyphs, 0, n);
            Array.Reverse(clusters, 0, n);
            Array.Reverse(advances, 0, n);
            Array.Reverse(ignorable, 0, n);
        }

        var flags = new GlyphFlags[n];
        // 默认可忽略字符不可见：即使字体没有空格字形（此时映射为 .notdef）也不会被绘制出来
        for (int j = 0; j < n; j++)
            if (ignorable[j]) flags[j] = GlyphFlags.Hidden;
        // 成对字距：'kern' 表按视觉顺序定义（左, 右），值加在左侧字形的前进量上；跳过默认可忽略字符
        var kernOff = KerningDisabled(request.Features, start, text.Length);
        int prev = -1;
        for (int j = 0; j < n; j++)
        {
            if (ignorable[j]) continue;
            if (prev >= 0 && !IsOff(kernOff, clusters[prev] - start) && !IsOff(kernOff, clusters[j] - start))
            {
                int k = face.GetKerning(glyphs[prev], glyphs[j]);
                if (k != 0)
                {
                    advances[prev] += k;
                    // 在逻辑顺序中靠后的那个字形之前断开需要重新整形
                    flags[rtl ? prev : j] |= GlyphFlags.UnsafeToBreak;
                }
            }
            prev = j;
        }

        float scale = face.Metrics.GetScale(request.Size);
        var outGlyphs = new ushort[n];
        var outClusters = new int[n];
        var outAdvances = new Vector2[n];
        Array.Copy(glyphs, outGlyphs, n);
        Array.Copy(clusters, outClusters, n);
        for (int j = 0; j < n; j++) outAdvances[j] = new Vector2(advances[j] * scale, 0);
        return new ShapedRun(face, request.Size, request.Range, rtl, outGlyphs, outClusters, outAdvances, new Vector2[n], flags, owned: true);
    }

    /// <summary>词边界的字距：'kern' 表中的成对值（只处理从左到右；从右到左返回 0）。全局关闭 'kern' 时为 0。</summary>
    public override float GetBoundaryKerning(in ShapeRequest request, ushort first, ushort second)
    {
        foreach (var f in request.Features)
            if (f.Tag == FontFeature.Kerning && f.Start <= 0 && f.End == int.MaxValue && f.Value == 0) return 0;
        var face = request.Face;
        int k = request.RightToLeft ? 0 : face.GetKerning(first, second);
        return k * face.Metrics.GetScale(request.Size);
    }

    static bool IsOff(bool[]? off, int i) => off is not null && off[i];

    /// <summary>
    /// 每个 UTF-16 位置是否关闭了 'kern'（后出现的设置覆盖先前的）。没有 'kern' 设置时返回 null（全部开启）。
    /// 按设置的逆序处理，已确定的位置用并查集跳过：O((长度 + 设置数) · α)。旧实现对每个字形扫描全部设置，按 span 传特性时为二次方。
    /// </summary>
    static bool[]? KerningDisabled(ReadOnlySpan<FontFeature> features, int start, int end)
    {
        bool any = false;
        foreach (var f in features)
            if (f.Tag == FontFeature.Kerning)
            {
                any = true;
                break;
            }
        if (!any) return null;
        int len = end - start;
        var off = new bool[Math.Max(len, 0)];
        var next = new int[len + 1];
        for (int i = 0; i <= len; i++) next[i] = i;
        for (int k = features.Length - 1; k >= 0; k--)
        {
            var f = features[k];
            if (f.Tag != FontFeature.Kerning) continue;
            int a = Math.Max(f.Start, start) - start, b = Math.Min(f.End, end) - start;
            if (a >= b) continue;
            for (int i = Find(next, a); i < b; i = Find(next, i))
            {
                off[i] = f.Value == 0;
                next[i] = i + 1;
            }
        }
        return off;

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
    }

    /// <summary>Unicode Default_Ignorable_Code_Point（取自 AwAUI.Unicode；不含谚文填充符，它们由谚文整形器处理）。</summary>
    internal static bool IsDefaultIgnorable(int cp)
    {
        if (cp < 0x00AD) return false;
        if (cp == 0x115F || cp == 0x1160 || cp == 0x3164 || cp == 0xFFA0) return false;
        return UnicodeData.IsDefaultIgnorable(cp);
    }
}
