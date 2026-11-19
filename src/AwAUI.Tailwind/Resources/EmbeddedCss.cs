namespace AwAUI.Tailwind.Resources;

/// <summary>内置的样式表（<c>@import "tailwindcss"</c> 等由它们解析，不访问文件系统）。</summary>
internal static partial class EmbeddedCss
{
    /// <summary><c>@import "tailwindcss"</c>，原版 preflight：逐字等于 Tailwind 发布的 <c>index.css</c>。</summary>
    public const string Index = IndexHead + "\n" + IndexBase + "\n" + IndexTail;

    /// <summary><c>@import "tailwindcss"</c>，AwAUI preflight：<c>@layer base</c> 换成 <see cref="AwAUIPreflight"/>。</summary>
    public const string IndexAwAUI = IndexHead + "\n@import \"tailwindcss/preflight.css\" layer(base);\n\n" + IndexTail;

    /// <summary><c>@import "tailwindcss/utilities.css"</c>。</summary>
    public const string Utilities = "@tailwind utilities;\n";

    /// <summary>
    /// AwAUI 的 preflight：由 Tailwind v4.3.5 的 preflight 裁剪而来。与原版的差异（逐条）：
    /// <list type="number">
    /// <item>全局重置只针对 <c>*, ::before, ::after</c>：去掉 <c>::backdrop</c> 与 <c>::file-selector-button</c>（AwAUI 没有这两个伪元素，未知伪元素会让整条规则失效）。</item>
    /// <item><c>html</c> 规则去掉 <c>:host</c>（无 Shadow DOM）、<c>-webkit-text-size-adjust</c> 与 <c>-webkit-tap-highlight-color</c>（浏览器专有）。</item>
    /// <item>删除只为修正浏览器差异的规则：<c>abbr[title]</c>、<c>:-moz-focusring</c>、<c>progress</c>、<c>summary</c>、<c>:-moz-ui-invalid</c>、
    /// <c>::-webkit-*</c> 系列、<c>select</c> 的 <c>optgroup</c> 缩进、<c>appearance: button</c> 修正、<c>textarea { resize }</c>。</item>
    /// <item>替换元素只列 AwAUI 支持的 <c>img, svg, canvas</c>（去掉 video / audio / iframe / embed / object）。</item>
    /// <item>表单控件重置只针对 <c>button, input, select, textarea</c>（去掉 <c>optgroup</c> 与 <c>::file-selector-button</c>）。</item>
    /// <item><c>::placeholder</c> 的颜色直接用 <c>color-mix()</c>，不再包在浏览器探测用的 <c>@supports</c> 里（AwAUI 引擎原生支持 color-mix 是第 1 步目标）。</item>
    /// <item><c>[hidden]</c> 不再排除 <c>until-found</c>（AwAUI 没有页内查找）。</item>
    /// <item>去掉所有 <c>-webkit-</c> 前缀的重复声明（如 <c>-webkit-text-decoration</c>）。</item>
    /// </list>
    /// 需要逐字的 v4 preflight 时，用 <see cref="TailwindOptions.Preflight"/> = <see cref="PreflightKind.Tailwind"/>。
    /// </summary>
    public const string AwAUIPreflight = """
*,
::before,
::after {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
  border: 0 solid;
}

html {
  line-height: 1.5;
  tab-size: 4;
  font-family: --theme(
    --default-font-family,
    -apple-system,
    BlinkMacSystemFont,
    'Segoe UI',
    Roboto,
    'Helvetica Neue',
    'Noto Sans',
    Arial,
    sans-serif,
    'Apple Color Emoji',
    'Segoe UI Emoji',
    'Segoe UI Symbol',
    'Noto Color Emoji'
  );
  font-feature-settings: --theme(--default-font-feature-settings, normal);
  font-variation-settings: --theme(--default-font-variation-settings, normal);
}

hr {
  height: 0;
  color: inherit;
  border-top-width: 1px;
}

h1,
h2,
h3,
h4,
h5,
h6 {
  font-size: inherit;
  font-weight: inherit;
}

a {
  color: inherit;
  text-decoration: inherit;
}

b,
strong {
  font-weight: bolder;
}

code,
kbd,
samp,
pre {
  font-family: --theme(
    --default-mono-font-family,
    ui-monospace,
    SFMono-Regular,
    Menlo,
    Monaco,
    Consolas,
    'Liberation Mono',
    'Courier New',
    monospace
  );
  font-feature-settings: --theme(--default-mono-font-feature-settings, normal);
  font-variation-settings: --theme(--default-mono-font-variation-settings, normal);
  font-size: 1em;
}

small {
  font-size: 80%;
}

sub,
sup {
  font-size: 75%;
  line-height: 0;
  position: relative;
  vertical-align: baseline;
}

sub {
  bottom: -0.25em;
}

sup {
  top: -0.5em;
}

table {
  text-indent: 0;
  border-color: inherit;
  border-collapse: collapse;
}

ol,
ul,
menu {
  list-style: none;
}

img,
svg,
canvas {
  display: block;
  vertical-align: middle;
}

img {
  max-width: 100%;
  height: auto;
}

button,
input,
select,
textarea {
  font: inherit;
  font-feature-settings: inherit;
  font-variation-settings: inherit;
  letter-spacing: inherit;
  color: inherit;
  border-radius: 0;
  background-color: transparent;
  opacity: 1;
}

::placeholder {
  opacity: 1;
  color: color-mix(in oklab, currentcolor 50%, transparent);
}

[hidden] {
  display: none !important;
}
""";
}
