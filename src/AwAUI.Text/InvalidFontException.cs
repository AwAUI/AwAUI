using System;

namespace AwAUI.Text;

/// <summary>字体数据损坏或不是可识别的字体格式。解析器对任何越界或不一致的数据都抛出此异常，而不是 IndexOutOfRangeException。</summary>
public sealed class InvalidFontException : Exception
{
    public InvalidFontException(string message) : base(message) { }
    public InvalidFontException(string message, Exception inner) : base(message, inner) { }
}
