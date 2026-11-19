using System;

namespace AwAUI.Tailwind;

/// <summary>
/// Tailwind 输入样式表中的错误（非法的 <c>@utility</c> / <c>@custom-variant</c> 名称、未知的 <c>@apply</c> 类名、循环依赖、语法错误等）。
/// 错误信息与 Tailwind CSS v4 保持一致，便于对照官方文档排查。
/// </summary>
public sealed class TailwindException : Exception
{
    /// <summary>创建异常。</summary>
    public TailwindException(string message) : base(message)
    {
    }
}
