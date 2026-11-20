using System;
using System.Globalization;

namespace WasmProbe;

/// <summary>
/// 入口。Node 下由 node/bench.mjs 以 <c>bench</c> 参数调用 Main 运行基准；
/// 浏览器下 main.js 不调用 Main，直接使用 <see cref="CanvasHost"/> 的 JSExport。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "bench")
        {
            int warmup = IntArg(args, "--warmup", 10);
            int frames = IntArg(args, "--frames", 30);
            string filter = StringArg(args, "--filter") ?? "";
            Bench.Run(warmup, frames, filter, StringArg(args, "--tier"), Array.IndexOf(args, "--series") >= 0);
        }
        else if (args.Length > 0 && args[0] == "micro")
            MicroBench.Run(IntArg(args, "--iterations", 2_000_000));
        return 0;
    }

    static int IntArg(string[] args, string name, int fallback) =>
        int.TryParse(StringArg(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static string? StringArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
