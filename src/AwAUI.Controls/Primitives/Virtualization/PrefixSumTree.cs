using System;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 树状数组（Fenwick tree）：O(log n) 的单点修改、前缀和与按前缀和二分定位；O(n) 建树。虚拟化用它存放每行的占位长度（尺寸 + 间距）。
/// </summary>
internal sealed class PrefixSumTree
{
    double[] _tree = new double[1];   // 1 起始
    int _count;

    public int Count => _count;

    /// <summary>以 <paramref name="values"/> 的前 <paramref name="count"/> 项重建（O(n)）。</summary>
    public void Build(double[] values, int count)
    {
        if (_tree.Length < count + 1) _tree = new double[Math.Max(count + 1, _tree.Length * 2)];
        else Array.Clear(_tree, 0, count + 1);
        _count = count;
        for (int i = 1; i <= count; i++)
        {
            _tree[i] += values[i - 1];
            int j = i + (i & -i);
            if (j <= count) _tree[j] += _tree[i];
        }
    }

    /// <summary>第 <paramref name="index"/> 项（0 起始）增加 <paramref name="delta"/>。</summary>
    public void Add(int index, double delta)
    {
        for (int i = index + 1; i <= _count; i += i & -i) _tree[i] += delta;
    }

    /// <summary>前 <paramref name="n"/> 项之和。</summary>
    public double Prefix(int n)
    {
        double s = 0;
        for (int i = Math.Min(n, _count); i > 0; i -= i & -i) s += _tree[i];
        return s;
    }

    /// <summary>
    /// 满足「前 k 项之和 ≤ <paramref name="target"/>」（<paramref name="strict"/> 时为 &lt;）的最大 k（0..Count）。
    /// 要求各项非负。
    /// </summary>
    public int Search(double target, bool strict)
    {
        int pos = 0;
        double rem = target;
        int step = HighestPowerOfTwo(_count);
        for (; step > 0; step >>= 1)
        {
            int next = pos + step;
            if (next > _count) continue;
            double v = _tree[next];
            if (strict ? v < rem : v <= rem)
            {
                pos = next;
                rem -= v;
            }
        }
        return pos;
    }

    static int HighestPowerOfTwo(int n)
    {
        int p = 1;
        while (p <= n >> 1) p <<= 1;
        return n == 0 ? 0 : p;
    }
}
