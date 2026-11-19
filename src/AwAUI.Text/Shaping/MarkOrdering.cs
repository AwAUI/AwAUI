using AwAUI.Unicode;

namespace AwAUI.Text.Shaping;

/// <summary>
/// 组合标记的规范排序（HarfBuzz 规范化的第二轮「reorder」）：连续的非零组合类标记按修正后的组合类稳定排序。
/// 修正后的组合类（HarfBuzz 的 modified combining class）调整了希伯来、阿拉伯、叙利亚、泰卢固、泰、老挝、藏文的若干组合类，
/// 使排序结果符合字体的设计习惯（例如阿拉伯 shadda 排在其他元音符号之前）。
/// 不做规范分解与组合：字体缺少预组合字形时不会分解，有预组合字形时也不会组合。
/// </summary>
internal static class MarkOrdering
{
    /// <summary>一段标记超过此长度时不排序（与 HarfBuzz 的 HB_OT_SHAPE_MAX_COMBINING_MARKS 相同，防止二次方开销）。</summary>
    public const int MaxCombiningMarks = 32;

    public static byte ModifiedCombiningClass(int cp)
    {
        int ccc = UnicodeData.GetCombiningClass(cp);
        if (ccc == 0) return 0;
        return ccc switch
        {
            // 希伯来
            10 => 22, 11 => 15, 12 => 16, 13 => 17, 14 => 23, 15 => 18, 16 => 19, 17 => 20, 18 => 21, 19 => 14,
            20 => 24, 21 => 12, 22 => 25, 23 => 13, 24 => 10, 25 => 11, 26 => 26,
            // 阿拉伯
            27 => 28, 28 => 29, 29 => 30, 30 => 31, 31 => 32, 32 => 33, 33 => 27, 34 => 34, 35 => 35,
            // 叙利亚
            36 => 36,
            // 泰卢固
            84 => 0, 91 => 0,
            // 泰
            103 => 3, 107 => 107,
            // 老挝
            118 => 118, 122 => 122,
            // 藏
            129 => 129, 130 => 132, 132 => 131,
            _ => (byte)ccc,
        };
    }

    /// <summary>阿拉伯的修饰性组合标记（hamza above / below 等）：在同组合类的标记中移到最前。</summary>
    static bool IsModifierCombiningMark(int cp) => cp is 0x0654 or 0x0655 or 0x0658 or 0x06DC or 0x06E3 or 0x06E7 or 0x06E8
        or 0x08CA or 0x08CB or 0x08CD or 0x08CE or 0x08CF or 0x08D3 or 0x08F3;

    public static void Reorder(GlyphBuffer b, bool arabic)
    {
        var info = b.Info;
        int count = b.Len;
        for (int i = 0; i < count; i++)
        {
            if (info[i].Ccc == 0) continue;
            int end = i + 1;
            while (end < count && info[end].Ccc != 0) end++;
            if (end - i <= MaxCombiningMarks)
            {
                Sort(b, i, end);
                if (arabic) ReorderArabicMarks(b, i, end);
            }
            i = end;
        }
    }

    /// <summary>稳定插入排序（段长不超过 32）；移动的字形与跨过的字形合并簇值。</summary>
    static void Sort(GlyphBuffer b, int start, int end)
    {
        var info = b.Info;
        for (int i = start + 1; i < end; i++)
        {
            int j = i;
            while (j > start && info[j - 1].Ccc > info[i].Ccc) j--;
            if (i == j) continue;
            MergeClusters(info, j, i + 1);
            var t = info[i];
            System.Array.Copy(info, j, info, j + 1, i - j);
            info[j] = t;
        }
    }

    static void ReorderArabicMarks(GlyphBuffer b, int start, int end)
    {
        var info = b.Info;
        int i = start;
        for (int cc = 220; cc <= 230; cc += 10)
        {
            while (i < end && info[i].Ccc < cc) i++;
            if (i == end) break;
            if (info[i].Ccc > cc) continue;
            int j = i;
            while (j < end && info[j].Ccc == cc && IsModifierCombiningMark(info[j].CodePoint)) j++;
            if (i == j) continue;
            MergeClusters(info, start, j);
            var temp = new GlyphInfo[j - i];
            System.Array.Copy(info, i, temp, 0, j - i);
            System.Array.Copy(info, start, info, start + j - i, i - start);
            System.Array.Copy(temp, 0, info, start, j - i);
            int newStart = start + j - i;
            byte newCc = (byte)(cc == 220 ? 25 : 26);   // HarfBuzz：CCC22 → 25，CCC26 → 26，保持序列递增
            for (; start < newStart; start++) info[start].Ccc = newCc;
            i = j;
        }
    }

    static void MergeClusters(GlyphInfo[] info, int start, int end)
    {
        int cluster = info[start].Cluster;
        for (int k = start + 1; k < end; k++) cluster = System.Math.Min(cluster, info[k].Cluster);
        for (int k = start; k < end; k++) info[k].Cluster = cluster;
    }
}
