using System;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 按内容比较的缓存键（渐变色标、路径掩码、字形）共用的小工具。软件后端与 GPU 后端共用。
/// </summary>
internal static class KeyHash
{
    /// <summary>
    /// 每进程随机的哈希种子（与 AwAUI.Text 的整形缓存相同的做法）：键的内容来自绘制内容（可能源自不可信的 SVG、CSS 等），
    /// 固定的哈希组合可以被离线构造出大量全哈希碰撞，让每次查找退化为逐个比较整条键。
    /// 种子经 <c>HashCodeCompat.Combine</c> 非线性地混入（net8+ 为 System.HashCode，其余为 fmix32 雪崩），所以碰撞依赖种子。
    /// </summary>
    public static readonly int Seed = Guid.NewGuid().GetHashCode();

    /// <summary>
    /// 浮点的「同一个值」：NaN 等于 NaN（<see cref="float.Equals(float)"/> 的语义）。键必须等于它自己，
    /// 否则含 NaN 的键既命中不了、也无法从字典里删除（缓存会无界增长）。
    /// </summary>
    public static bool Same(float a, float b) => a.Equals(b);

    public static bool Same(Color a, Color b) => Same(a.R, b.R) && Same(a.G, b.G) && Same(a.B, b.B) && Same(a.A, b.A);

    public static int Hash(Color c) => HashCodeCompat.Combine(c.R.GetHashCode(), c.G.GetHashCode(), c.B.GetHashCode(), c.A.GetHashCode());
}
