namespace AwAUI
{
    /// <summary>
    /// 与 System.HashCode 等价的最小实现（netstandard2.0 没有 HashCode）。
    /// <para>
    /// 组合是非线性的：net8+ 直接用 <c>System.HashCode</c>（xxHash32，每进程随机种子）；其余目标框架每步把已有状态与新值混合后
    /// 再过一遍 murmur3 的 fmix32 雪崩（双射）。因此带种子的键（<c>KeyHash.Seed</c>、Text 的整形缓存）的碰撞依赖种子：
    /// 原来的「异或再乘」对最高位的差异是线性的，可以离线构造与种子无关的碰撞。
    /// </para>
    /// <para>
    /// 确定性：netstandard2.0 / .NET Framework 上同一输入在任何进程中结果相同；net8+ 上只在同一进程内稳定（与 <c>string.GetHashCode</c> 相同），
    /// 所以哈希值不能持久化或用于决定输出顺序。相等的输入总是得到相等的哈希。
    /// </para>
    /// </summary>
    internal static class HashCodeCompat
    {
#if NET8_0_OR_GREATER
        public static int Combine(int a, int b) => System.HashCode.Combine(a, b);
        public static int Combine(int a, int b, int c) => System.HashCode.Combine(a, b, c);
        public static int Combine(int a, int b, int c, int d) => System.HashCode.Combine(a, b, c, d);
#else
        const uint Prime1 = 2654435761U;
        const uint Prime2 = 2246822519U;

        public static int Combine(int a, int b) => (int)Step(Start(a), b);
        public static int Combine(int a, int b, int c) => (int)Step(Step(Start(a), b), c);
        public static int Combine(int a, int b, int c, int d) => (int)Step(Step(Step(Start(a), b), c), d);

        static uint Start(int a) => Fmix32(unchecked((uint)a * Prime1 + Prime2));

        /// <summary>状态乘奇数常数、循环移位后与新值相加，再整体雪崩：对固定的状态是关于新值的双射，状态本身非线性地参与。</summary>
        static uint Step(uint h, int v)
        {
            unchecked
            {
                h *= Prime1;
                h = (h << 13) | (h >> 19);
                return Fmix32(h + (uint)v * Prime2);
            }
        }

        /// <summary>murmur3 的最终混合（双射，雪崩）。</summary>
        static uint Fmix32(uint h)
        {
            unchecked
            {
                h ^= h >> 16;
                h *= 0x85EBCA6BU;
                h ^= h >> 13;
                h *= 0xC2B2AE35U;
                h ^= h >> 16;
                return h;
            }
        }
#endif
    }
}
