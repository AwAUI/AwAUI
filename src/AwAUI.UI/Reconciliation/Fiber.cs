using System;
using System.Collections.Generic;

namespace AwAUI.UI;

internal enum FiberKind : byte
{
    Root,
    Element,
    Text,
    Component,
    Fragment,
}

/// <summary>
/// 协调用的内部实例记录，镜像描述树的结构：元素、文本、组件、片段各一种。
/// 组件与片段不产生元素，它们的元素放在最近的宿主（元素或根）下。
/// </summary>
internal sealed class Fiber
{
    public FiberKind Kind;
    public Type? Type;
    public object? Key;
    public Fiber? Parent;
    public List<Fiber>? Children;

    /// <summary>元素 / 文本 / 根：对应的存活元素。</summary>
    public Element? Element;

    /// <summary>元素：上一次的描述（供 Diff 比较）。</summary>
    public ElementNode? ElementNode;

    /// <summary>文本：上一次的描述。</summary>
    public TextNode? TextNode;

    /// <summary>片段：上一次的描述。</summary>
    public Fragment? FragmentNode;

    /// <summary>组件：存活实例。</summary>
    public Component? Component;

    /// <summary>匹配过程中的临时标记。</summary>
    public long Stamp;
    public int Ordinal;

    public bool IsHost => Kind == FiberKind.Element || Kind == FiberKind.Text || Kind == FiberKind.Root;

    public override string ToString() => Kind + ":" + (Type?.Name ?? "") + (Key is null ? "" : "#" + Key);
}

/// <summary>最长递增子序列：找出相对顺序不变、无需移动的最大集合（O(n log n)）。</summary>
internal static class Lis
{
    /// <summary>
    /// <paramref name="positions"/> 中 &lt; 0 的项不参与；返回的 <paramref name="keep"/>[i] 为 true 表示第 i 项在 LIS 中（不需要移动）。
    /// </summary>
    public static void Compute(int[] positions, int count, bool[] keep)
    {
        Array.Clear(keep, 0, count);
        if (count == 0) return;
        var tails = new int[count];      // tails[k] = 长度为 k+1 的递增子序列末尾元素在 positions 中的下标
        var prev = new int[count];
        int len = 0;
        for (int i = 0; i < count; i++)
        {
            int p = positions[i];
            if (p < 0) continue;
            int lo = 0, hi = len;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (positions[tails[mid]] < p) lo = mid + 1;
                else hi = mid;
            }
            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            tails[lo] = i;
            if (lo == len) len++;
        }
        if (len == 0) return;
        for (int i = tails[len - 1]; i >= 0; i = prev[i]) keep[i] = true;
    }
}
