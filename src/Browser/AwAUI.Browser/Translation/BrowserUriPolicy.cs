using System;

namespace AwAUI.Browser;

// 本文件属于 Translation/：只用 netstandard2.0 可用的 API，可由 tests/AwAUI.Browser.Tests 以源码链接方式在普通 .NET 上测试。

/// <summary>
/// 浏览器启动外部 URI 的方案白名单（design/04「Launcher」）。浏览器没有 shell，<c>window.open</c> 只能打开
/// http / https / mailto；其它方案（javascript:、data:、file:、blob: 等）可能执行脚本或读取本机文件，一律拒绝。
/// JS 胶水在 <c>launcherOpen</c> 中重复校验（防御纵深），两边保持同一名单。
/// </summary>
internal static class BrowserUriPolicy
{
    /// <summary>是否为允许交给浏览器的绝对 URI。相对 URI 与未知方案返回 false。</summary>
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        return uri.Scheme == Uri.UriSchemeHttp
            || uri.Scheme == Uri.UriSchemeHttps
            || string.Equals(uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase);
    }
}
