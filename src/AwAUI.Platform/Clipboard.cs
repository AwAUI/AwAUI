using System;
using System.Threading.Tasks;

namespace AwAUI.Platform;

/// <summary>
/// 系统剪贴板（design/04）。接口全异步：Web 上的剪贴板只能异步访问且受权限与用户手势限制；桌面平台的实现通常同步完成并返回已完成的任务。
/// 只在 UI 线程上调用；返回的任务可能在 UI 线程之外完成，调用方自行回到 UI 线程。目前只有纯文本，HTML / 图片等格式以后作为新的虚成员追加。
/// <para>
/// 剪贴板内容来自其他程序，视为不可信：读取的文本长度不超过 <see cref="MaxTextLength"/>（超出时截断，不切开代理对），
/// 实现不得因对方声明的尺寸而无界分配或等待。
/// </para>
/// </summary>
public abstract class Clipboard
{
    /// <summary>读取与写入的文本长度上限（UTF-16 码元，16M）。</summary>
    public const int MaxTextLength = 1 << 24;

    /// <summary>读取纯文本；剪贴板为空、没有文本格式、被拒绝或超时时返回 null。</summary>
    public abstract Task<string?> GetTextAsync();

    /// <summary>写入纯文本（替换剪贴板的全部内容）；返回是否成功。超过 <see cref="MaxTextLength"/> 的文本被截断。</summary>
    public abstract Task<bool> SetTextAsync(string text);

    /// <summary>清空剪贴板；默认写入空文本。</summary>
    public virtual Task<bool> ClearAsync() => SetTextAsync(string.Empty);

    /// <summary>截断到 <see cref="MaxTextLength"/> 以内且不切开代理对（实现的公共辅助）。</summary>
    public static string LimitLength(string text, int max = MaxTextLength)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length <= max) return text;
        int n = Math.Max(0, max);
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text.Substring(0, n);
    }

    /// <summary>把 CRLF 与单独的 CR 规范为 LF（Windows 剪贴板文本是 CRLF）。</summary>
    public static string NormalizeNewlines(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.IndexOf('\r') < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
