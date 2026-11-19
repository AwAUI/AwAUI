using System.Collections.Generic;
using System.Runtime.InteropServices;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>AppInfo（design/20 会话）。宿主在创建 Agent 时填写平台、渲染后端等；运行时信息有默认值。</summary>
public sealed class DevToolsAppInfo
{
    /// <summary>应用名。</summary>
    public string Name { get; set; } = "AwAUI App";
    /// <summary>框架版本，默认取 AwAUI.Diagnostics 程序集版本。</summary>
    public string FrameworkVersion { get; set; } = typeof(DevToolsAppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    /// <summary>运行时描述，如 ".NET 10.0.1"、".NET Framework 4.8.1"。</summary>
    public string Runtime { get; set; } = RuntimeInformation.FrameworkDescription;
    /// <summary>平台名，默认按当前操作系统推断。</summary>
    public string Platform { get; set; } = DetectPlatform();
    /// <summary>渲染后端名。</summary>
    public string RenderBackend { get; set; } = "unknown";
    /// <summary>能力列表。</summary>
    public List<string> Capabilities { get; } = new();
    public bool HotReload { get; set; }
    public bool RenderThread { get; set; }
    /// <summary>是否运行在无 JIT 的 AOT 环境中（net8+ 自动检测）。</summary>
    public bool Aot { get; set; } =
#if NET8_0_OR_GREATER
        !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
#else
        false;
#endif

    static string DetectPlatform()
    {
#if NET8_0_OR_GREATER
        if (System.OperatingSystem.IsBrowser()) return "browser";
        if (System.OperatingSystem.IsAndroid()) return "android";
        if (System.OperatingSystem.IsIOS()) return "ios";
#endif
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }

    public JsonObject ToJson()
    {
        var caps = new JsonArray();
        foreach (var c in Capabilities) caps.Add(c);
        return new JsonObject
        {
            ["name"] = Name,
            ["frameworkVersion"] = FrameworkVersion,
            ["runtime"] = Runtime,
            ["platform"] = Platform,
            ["renderBackend"] = RenderBackend,
            ["capabilities"] = caps,
            ["features"] = new JsonObject
            {
                ["hotReload"] = HotReload,
                ["renderThread"] = RenderThread,
                ["aot"] = Aot,
            },
        };
    }
}
