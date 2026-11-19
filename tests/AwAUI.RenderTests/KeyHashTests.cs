using AwAUI.Graphics;
using AwAUI.Render.Raster;

namespace AwAUI.RenderTests;

/// <summary>
/// 缓存键哈希（HashCodeCompat）的组合必须是非线性的：原来的「异或再乘」中，两个参数的最高位同时翻转会互相抵消
/// （(s ^ a ^ 2³¹)·P = (s ^ a)·P ^ 2³¹，P 为奇数），与种子无关，所以 Color(r, g, b, a) 与 Color(−r, −g, b, a)
/// （float 的哈希只差符号位）必然碰撞。
/// </summary>
public class KeyHashTests
{
    [Fact]
    public void Sign_flipped_components_do_not_collide()
    {
        int collisions = 0;
        var rng = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            float r = 0.01f + (float)rng.NextDouble(), g = 0.01f + (float)rng.NextDouble(), b = (float)rng.NextDouble(), a = (float)rng.NextDouble();
            if (KeyHash.Hash(new Color(r, g, b, a)) == KeyHash.Hash(new Color(-r, -g, b, a))) collisions++;
        }
        Assert.True(collisions < 5, $"{collisions} of 2000 sign-flipped colours collide");
    }

    [Fact]
    public void Equal_keys_hash_equally_and_nan_is_self_equal()
    {
        Assert.Equal(KeyHash.Hash(new Color(0.1f, 0.2f, 0.3f, 0.4f)), KeyHash.Hash(new Color(0.1f, 0.2f, 0.3f, 0.4f)));
        Assert.Equal(KeyHash.Hash(new Color(float.NaN, 0, 0, 1)), KeyHash.Hash(new Color(float.NaN, 0, 0, 1)));
        Assert.True(KeyHash.Same(new Color(float.NaN, 0, 0, 1), new Color(float.NaN, 0, 0, 1)));
    }
}
