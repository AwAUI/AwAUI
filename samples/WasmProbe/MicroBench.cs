using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
#endif

namespace WasmProbe;

/// <summary>
/// 单个向量操作的微基准：找出解释器（Mono interp + jiterpreter）没有内建化、退化成慢速软件实现的 API。
/// 每个操作循环 N 次，报告 ns/次。
/// </summary>
public static class MicroBench
{
    public static void Run(int iterations)
    {
        Console.WriteLine($"# micro: {iterations} iterations per op (ns/op)");
        var f = new float[64];
        var b = new byte[256];
        for (int i = 0; i < f.Length; i++) f[i] = i * 0.37f;
        for (int i = 0; i < b.Length; i++) b[i] = (byte)i;

        Measure("scalar float mul-add", iterations, n =>
        {
            float acc = 0;
            for (int i = 0; i < n; i++) acc += f[i & 63] * 1.5f + 0.25f;
            return acc;
        });
        Measure("Vector4 mul-add", iterations, n =>
        {
            var acc = Vector4.Zero; var k = new Vector4(1.5f);
            for (int i = 0; i < n; i++) acc += new Vector4(f[i & 63]) * k + Vector4.One;
            return acc.X;
        });
        Measure("Vector<float> mul-add", iterations, n =>
        {
            var acc = Vector<float>.Zero; var k = new Vector<float>(1.5f);
            for (int i = 0; i < n; i++) acc += new Vector<float>(f, (i & 7) * 4) * k + Vector<float>.One;
            return acc[0];
        });
#if NET8_0_OR_GREATER
        Measure("V128 Load+mul-add", iterations, n =>
        {
            var acc = Vector128<float>.Zero; var k = Vector128.Create(1.5f);
            for (int i = 0; i < n; i++) acc += Vector128.Create(f.AsSpan((i & 7) * 4)) * k + Vector128<float>.One;
            return acc.ToScalar();
        });
        Measure("V128 ConditionalSelect", iterations, n =>
        {
            var acc = Vector128<float>.Zero; var k = Vector128.Create(1.5f);
            for (int i = 0; i < n; i++) acc = Vector128.ConditionalSelect(Vector128.GreaterThan(acc, k), acc - k, acc + k);
            return acc.ToScalar();
        });
        Measure("V128 Min/Max", iterations, n =>
        {
            var acc = Vector128.Create(0.5f); var lo = Vector128<float>.Zero; var hi = Vector128<float>.One; var k = Vector128.Create(1.01f);
            for (int i = 0; i < n; i++) acc = Vector128.Min(Vector128.Max(acc * k, lo), hi);
            return acc.ToScalar();
        });
        Measure("V128 vector * scalar", iterations, n =>
        {
            var acc = Vector128.Create(0.5f);
            for (int i = 0; i < n; i++) acc = acc * 1.0001f;
            return acc.ToScalar();
        });
        Measure("V128 vector * vector", iterations, n =>
        {
            var acc = Vector128.Create(0.5f); var k = Vector128.Create(1.0001f);
            for (int i = 0; i < n; i++) acc = acc * k;
            return acc.ToScalar();
        });
        Measure("V128 ConvertToInt32", iterations, n =>
        {
            var acc = Vector128<int>.Zero; var v = Vector128.Create(3.7f);
            for (int i = 0; i < n; i++) acc += Vector128.ConvertToInt32(v);
            return acc.ToScalar();
        });
        Measure("V128 ConvertToSingle", iterations, n =>
        {
            var acc = Vector128<float>.Zero; var v = Vector128.Create(3);
            for (int i = 0; i < n; i++) acc += Vector128.ConvertToSingle(v);
            return acc.ToScalar();
        });
        Measure("V128 Floor", iterations, n =>
        {
            var acc = Vector128<float>.Zero; var v = Vector128.Create(3.7f);
            for (int i = 0; i < n; i++) acc += Vector128.Floor(v + acc);
            return acc.ToScalar();
        });
        Measure("V128 Sqrt", iterations, n =>
        {
            var acc = Vector128.Create(2f);
            for (int i = 0; i < n; i++) acc = Vector128.Sqrt(acc) + Vector128<float>.One;
            return acc.ToScalar();
        });
        Measure("V128 Abs", iterations, n =>
        {
            var acc = Vector128.Create(-2f);
            for (int i = 0; i < n; i++) acc = -Vector128.Abs(acc);
            return acc.ToScalar();
        });
        Measure("V128 Narrow int->short", iterations, n =>
        {
            var acc = Vector128<short>.Zero; var v = Vector128.Create(7);
            for (int i = 0; i < n; i++) acc += Vector128.Narrow(v, v);
            return acc.ToScalar();
        });
        Measure("V128 Narrow ushort->byte", iterations, n =>
        {
            var acc = Vector128<byte>.Zero; var v = Vector128.Create((ushort)7);
            for (int i = 0; i < n; i++) acc += Vector128.Narrow(v, v);
            return acc.ToScalar();
        });
        Measure("V128 Widen byte->ushort", iterations, n =>
        {
            var acc = Vector128<ushort>.Zero; var v = Vector128.Create((byte)7);
            for (int i = 0; i < n; i++) { var (lo, hi) = Vector128.Widen(v); acc += lo + hi; }
            return acc.ToScalar();
        });
        Measure("V128 Shuffle byte", iterations, n =>
        {
            var acc = Vector128<byte>.Zero; var v = Vector128.Create(b.AsSpan(0, 16));
            var idx = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            for (int i = 0; i < n; i++) acc += Vector128.Shuffle(v, idx);
            return acc.ToScalar();
        });
        Measure("V128 ShiftLeft/Right", iterations, n =>
        {
            var acc = Vector128.Create(1u);
            for (int i = 0; i < n; i++) acc = Vector128.ShiftRightLogical(Vector128.ShiftLeft(acc, 3), 2);
            return acc.ToScalar();
        });
        Measure("V128 GreaterThanOrEqualAll", iterations, n =>
        {
            int c = 0; var v = Vector128.Create(1f); var k = Vector128.Create(0.5f);
            for (int i = 0; i < n; i++) if (Vector128.GreaterThanOrEqualAll(v, k)) c++;
            return c;
        });
        // 替代写法：看哪些等价操作被内建化了
        Measure("V128 ExtractMSB (all-check)", iterations, n =>
        {
            int c = 0; var v = Vector128.Create(1f); var k = Vector128.Create(0.5f);
            for (int i = 0; i < n; i++) if (Vector128.ExtractMostSignificantBits(Vector128.LessThan(v, k)) == 0) c++;
            return c;
        });
        Measure("V128 EqualsAll", iterations, n =>
        {
            int c = 0; var v = Vector128.Create(1f); var k = Vector128.Create(1f);
            for (int i = 0; i < n; i++) if (Vector128.EqualsAll(v, k)) c++;
            return c;
        });
        Measure("V128 clamp via CondSelect", iterations, n =>
        {
            var acc = Vector128.Create(0.5f); var lo = Vector128<float>.Zero; var hi = Vector128<float>.One; var k = Vector128.Create(1.01f);
            for (int i = 0; i < n; i++)
            {
                acc *= k;
                acc = Vector128.ConditionalSelect(Vector128.LessThan(acc, lo), lo, acc);
                acc = Vector128.ConditionalSelect(Vector128.GreaterThan(acc, hi), hi, acc);
            }
            return acc.ToScalar();
        });
        if (PackedSimd.IsSupported)
        {
            Measure("PackedSimd Min/Max", iterations, n =>
            {
                var acc = Vector128.Create(0.5f); var lo = Vector128<float>.Zero; var hi = Vector128<float>.One; var k = Vector128.Create(1.01f);
                for (int i = 0; i < n; i++) acc = PackedSimd.Min(PackedSimd.Max(acc * k, lo), hi);
                return acc.ToScalar();
            });
            Measure("PackedSimd NarrowSat i32->i16", iterations, n =>
            {
                var acc = Vector128<short>.Zero; var v = Vector128.Create(7);
                for (int i = 0; i < n; i++) acc += PackedSimd.ConvertNarrowingSaturateSigned(v, v);
                return acc.ToScalar();
            });
            // PackedSimd.AllTrue 在 .NET 10.0.15 的 jiterpreter 中生成无效的 wasm（v128.store 接收 i32），这里不测
        }
#endif
    }

    static void Measure(string name, int iterations, Func<int, float> body)
    {
        body(iterations / 10); // 预热（让 jiterpreter / 分层编译介入）
        body(iterations / 10);
        long t0 = Stopwatch.GetTimestamp();
        float r = body(iterations);
        double ns = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / iterations;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-28} {1,9:F2} ns/op   (checksum {2})", name, ns, r));
    }
}
