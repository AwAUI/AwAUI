using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using WasmProbe;

// 用法：WasmProbe.Native [--warmup N] [--frames N] [--filter name] [--tier Scalar|Vector|Vector128] [--dump <dir>]
if (Array.IndexOf(args, "--micro") >= 0)
{
    MicroBench.Run(int.TryParse(Arg("--iterations"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var it) ? it : 2_000_000);
    return;
}

int warmup = int.TryParse(Arg("--warmup"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 10;
int frames = int.TryParse(Arg("--frames"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 30;
Bench.Run(warmup, frames, Arg("--filter") ?? "", Arg("--tier"), Array.IndexOf(args, "--series") >= 0);

if (Arg("--dump") is { } dir)
{
    // 与 node/bench.mjs 的 --dump 相同：固定时间渲染一帧，写原始 RGBA（预乘）
    Directory.CreateDirectory(dir);
    foreach (var scale in new[] { 1, 2 })
    {
        using var target = Bench.RenderScene(scale, 0.25);
        var bytes = new byte[target.Bitmap.RowBytes * target.Bitmap.Height];
        Marshal.Copy(target.Bitmap.Address, bytes, 0, bytes.Length);
        File.WriteAllBytes(Path.Combine(dir, $"scene@{scale}x.rgba"), bytes);
    }
    Console.WriteLine($"# dumped reference frames to {dir}");
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
