using System;
using System.IO;
using System.Text;

namespace AwAUI.Tools.CssPropertyGen;

/// <summary>
/// 读取 Properties.json，写出 Generated/*.g.cs。输出确定：相同输入得到逐字节相同的文件。
/// 用法：dotnet run --project src/tools/CssPropertyGen -- [--manifest 路径] [--out 目录] [--check]
/// <c>--check</c> 只比较不写入，文件过期时返回 1。
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        string? manifest = null, output = null;
        bool check = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest": manifest = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--check": check = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    Console.Error.WriteLine("usage: CssPropertyGen [--manifest <Properties.json>] [--out <dir>] [--check]");
                    return 2;
            }
        }
        var root = FindRepoRoot();
        manifest ??= Path.Combine(root, "src", "AwAUI.Css", "Properties.json");
        output ??= Path.Combine(root, "src", "AwAUI.Css", "Generated");
        var files = PropertyCodeGenerator.Generate(File.ReadAllText(manifest));
        Directory.CreateDirectory(output);
        int stale = 0;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(output, name);
            bool same = File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == content;
            if (same) continue;
            stale++;
            if (check) Console.Error.WriteLine($"stale: {path}");
            else
            {
                File.WriteAllText(path, content, utf8);
                Console.WriteLine($"wrote {path}");
            }
        }
        if (stale == 0) Console.WriteLine("generated files are up to date.");
        return check && stale > 0 ? 1 : 0;
    }

    static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (global.json) not found.");
    }
}
