using System;
using System.Collections.Generic;

namespace AwAUI.Render.Gpu.Atlas;

/// <summary>
/// 天际线（skyline）矩形装箱：维护一条按 x 排序的「天际线」，新矩形放在使其顶边最低的位置（bottom-left 规则）。
/// 每次插入 O(段数)，段数不超过宽度；适合字形、路径掩码这类大小相近、持续插入的小矩形。
/// 不支持单独删除：图集按页整体淘汰后 <see cref="Reset"/>。
/// </summary>
internal sealed class SkylinePacker
{
    struct Segment
    {
        public int X, Y, Width;
        public Segment(int x, int y, int width) { X = x; Y = y; Width = width; }
    }

    readonly List<Segment> _skyline = new();

    public SkylinePacker(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Reset();
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>已放入矩形的总面积（像素）。</summary>
    public long UsedArea { get; private set; }

    /// <summary>天际线段数（测试与诊断用）。</summary>
    public int SegmentCount => _skyline.Count;

    public void Reset()
    {
        _skyline.Clear();
        _skyline.Add(new Segment(0, 0, Width));
        UsedArea = 0;
    }

    /// <summary>放入 <paramref name="width"/> × <paramref name="height"/> 的矩形；放不下时返回 false。</summary>
    public bool TryPack(int width, int height, out int x, out int y)
    {
        x = y = 0;
        if (width <= 0 || height <= 0 || width > Width || height > Height) return false;

        int bestIndex = -1, bestTop = int.MaxValue, bestWidth = int.MaxValue, bestY = 0;
        for (int i = 0; i < _skyline.Count; i++)
        {
            if (!Fits(i, width, height, out int top)) continue;
            int segWidth = _skyline[i].Width;
            if (top + height < bestTop || top + height == bestTop && segWidth < bestWidth)
            {
                bestIndex = i;
                bestTop = top + height;
                bestWidth = segWidth;
                bestY = top;
            }
        }
        if (bestIndex < 0) return false;

        x = _skyline[bestIndex].X;
        y = bestY;
        Place(bestIndex, x, y + height, width);
        UsedArea += (long)width * height;
        return true;
    }

    /// <summary>以第 <paramref name="index"/> 段的左端为起点放 width 宽时，矩形底边要落在的高度（跨越各段的最大值）。</summary>
    bool Fits(int index, int width, int height, out int top)
    {
        int x = _skyline[index].X;
        top = 0;
        if (x + width > Width) return false;
        int remaining = width;
        for (int i = index; remaining > 0; i++)
        {
            var s = _skyline[i];
            if (s.Y > top) top = s.Y;
            if (top + height > Height) return false;
            remaining -= s.Width;
        }
        return true;
    }

    void Place(int index, int x, int y, int width)
    {
        _skyline.Insert(index, new Segment(x, y, width));
        int end = x + width;
        // 被新段覆盖的后续段：整段删除或左端截短
        int i = index + 1;
        while (i < _skyline.Count)
        {
            var s = _skyline[i];
            if (s.X >= end) break;
            int sEnd = s.X + s.Width;
            if (sEnd <= end)
            {
                _skyline.RemoveAt(i);
                continue;
            }
            _skyline[i] = new Segment(end, s.Y, sEnd - end);
            break;
        }
        // 合并等高的相邻段
        for (int k = 0; k + 1 < _skyline.Count;)
        {
            if (_skyline[k].Y == _skyline[k + 1].Y)
            {
                _skyline[k] = new Segment(_skyline[k].X, _skyline[k].Y, _skyline[k].Width + _skyline[k + 1].Width);
                _skyline.RemoveAt(k + 1);
            }
            else k++;
        }
    }
}
