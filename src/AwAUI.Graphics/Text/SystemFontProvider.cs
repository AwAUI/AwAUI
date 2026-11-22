using System;
using System.Collections.Generic;

namespace AwAUI.Graphics.Text;

/// <summary>
/// 系统字体提供者（design/19「字体回退」）：枚举操作系统安装的字体文件，并按码点与语言环境给出回退族名。
/// 只交出文件路径，<see cref="Text.FontCollection"/> 按需解析（只读取用到的表）。
/// 没有系统字体的平台（Web、Headless）返回 <see cref="Empty"/>；浏览器等由应用配置字体清单后自行实现。
/// </summary>
public abstract class SystemFontProvider
{
    /// <summary>系统字体文件（.ttf / .otf / .ttc / .otc）的路径。</summary>
    public abstract IReadOnlyList<string> FontFiles { get; }

    /// <summary>平台默认的界面字体族名（用于没有指定族名或族名无法解析的文本）；未知时返回 null。</summary>
    public virtual string? DefaultFamilyName => null;

    /// <summary>
    /// 码点（见 <c>AwAUI.Unicode.CodePoint</c>）在给定语言环境（BCP 47，如 "zh-Hans"、"ja"）下的回退族名，
    /// 按优先级排列。族名是系统安装的具体族名（如 "PingFang SC"），不是通用族名。
    /// </summary>
    public virtual IReadOnlyList<string> GetFallbackFamilies(int codePoint, string? locale) => Array.Empty<string>();

    /// <summary>没有系统字体的平台。</summary>
    public static SystemFontProvider Empty { get; } = new EmptySystemFontProvider();

    sealed class EmptySystemFontProvider : SystemFontProvider
    {
        public override IReadOnlyList<string> FontFiles => Array.Empty<string>();
    }
}
