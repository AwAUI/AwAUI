namespace AwAUI.Tailwind;

/// <summary><c>@import "tailwindcss"</c> 使用哪一份 preflight。</summary>
public enum PreflightKind
{
    /// <summary>为 AwAUI 裁剪过的 preflight（默认；与原版的逐条差异见程序集内 <c>EmbeddedCss.AwAUIPreflight</c> 的注释）。</summary>
    AwAUI,
    /// <summary>Tailwind CSS v4 原版 preflight（逐字）。</summary>
    Tailwind,
}

/// <summary><see cref="TailwindCompiler"/> 的选项。</summary>
public sealed class TailwindOptions
{
    /// <summary>解析内置包以外的 <c>@import</c> / <c>@reference</c>；为 null 时遇到这类导入报错。</summary>
    public StylesheetResolver? Resolver { get; set; }

    /// <summary>入口样式表所在目录（传给 <see cref="StylesheetResolver"/> 作为相对路径的基准）。</summary>
    public string? BaseDirectory { get; set; }

    /// <summary><c>@import "tailwindcss"</c> 与 <c>"tailwindcss/preflight.css"</c> 使用的 preflight。</summary>
    public PreflightKind Preflight { get; set; } = PreflightKind.AwAUI;

    /// <summary>输出压缩的 CSS（去掉缩进、换行与最后一个分号）。</summary>
    public bool Minify { get; set; }

    /// <summary>
    /// 承载主题变量的规则选择器。默认 <c>:root, :host</c> 与 Tailwind v4 输出一致；
    /// AwAUI.Css 目前不支持 <c>:host</c> 且按规范会丢弃整条规则（含全部主题变量），AwAUI 集成在引擎补齐前应设为 <c>:root</c>。
    /// </summary>
    public string ThemeRootSelector { get; set; } = ":root, :host";
}

/// <summary>被导入样式表的内容。</summary>
public sealed class ResolvedStylesheet
{
    /// <summary>创建结果。</summary>
    public ResolvedStylesheet(string content, string? baseDirectory = null)
    {
        Content = content;
        BaseDirectory = baseDirectory;
    }

    /// <summary>样式表文本。</summary>
    public string Content { get; }

    /// <summary>该样式表所在目录（它内部的相对导入以此为基准）。</summary>
    public string? BaseDirectory { get; }
}

/// <summary>
/// 解析 <c>@import</c> 的扩展点。内置的 <c>tailwindcss</c>、<c>tailwindcss/theme.css</c>、<c>tailwindcss/preflight.css</c>、
/// <c>tailwindcss/utilities.css</c> 与 <c>tw-animate-css</c> 不经过它。构建期（源生成器）的实现读取 AdditionalFiles，运行时可读取嵌入资源。
/// </summary>
public abstract class StylesheetResolver
{
    /// <summary>解析 <paramref name="id"/>（引号内的原文）；找不到时返回 null（编译器会报错）。</summary>
    public abstract ResolvedStylesheet? Resolve(string id, string? baseDirectory);
}
