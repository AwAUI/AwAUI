using AwAUI.Css;

namespace AwAUI.UI;

/// <summary>
/// 默认样式表（design/08「样式从哪里来」中的「默认样式表」，相当于浏览器的 UA 样式表），以 <c>UserAgent</c> 来源加入级联，
/// 应用样式表总能覆盖它。刻意保持很小：只定义内建元素的 display、盒模型与可交互元素的基础外观。
/// <list type="bullet">
/// <item><c>box-sizing: border-box</c>（design/08「与浏览器的已知差异」）；<c>scroll-behavior: smooth</c>（程序滚动默认平滑，
/// 作者样式 <c>scroll-behavior: auto</c> 改为立即）。</item>
/// <item>根元素填满视口高度；字体、颜色在根上设置并向下继承。</item>
/// <item><c>div</c>、<c>text</c> 为块级；<c>span</c> 为行内（只在 <c>text</c> 内部排版）；<c>img</c>、<c>svg</c> 为行内块（替换元素，尺寸取固有尺寸）；
/// <see cref="Icon"/> 的 <c>svg.aw-icon</c> 为 <c>1em × 1em</c>。</item>
/// <item><c>button</c> 为 <c>inline-flex</c> 居中内容，带悬停、按下、禁用与键盘焦点环（<c>:focus-visible</c>）样式。</item>
/// <item><c>input</c> 为带边框的单行框，占位文本用较淡的颜色（没有 <c>::placeholder</c> 规则时在 <see cref="RenderPipeline"/> 中按 <c>color</c>
/// 的 50% 不透明度绘制）；<c>field-sizing</c> 取 CSS 的初始值 fixed：框宽由 <c>min-width</c> 等决定，不随文字变宽（<c>field-sizing: content</c> 时变宽）。</item>
/// </list>
/// </summary>
public static class DefaultStyleSheet
{
    /// <summary>默认样式表的 CSS 文本。</summary>
    public const string Text = """
        * { box-sizing: border-box; scroll-behavior: smooth; }
        root {
          display: block;
          height: 100%;
          font-family: sans-serif;
          font-size: 14px;
          color: #1f2328;
          background-color: #ffffff;
        }
        div, text { display: block; }
        span { display: inline; }
        img, svg { display: inline-block; }
        svg.aw-icon { width: 1em; height: 1em; flex-shrink: 0; }
        button {
          display: inline-flex;
          align-items: center;
          justify-content: center;
          padding: 6px 14px;
          border: 1px solid #c8ced6;
          border-radius: 6px;
          background-color: #f6f8fa;
          color: #1f2328;
          white-space: nowrap;
          cursor: default;
          user-select: none;
        }
        button:hover { background-color: #eef1f4; }
        button:active { background-color: #e1e5ea; }
        button:focus-visible { outline: 2px solid #4f8cf7; outline-offset: 2px; }
        button:disabled { opacity: 0.5; }
        input {
          display: inline-block;
          min-width: 160px;
          padding: 6px 8px;
          border: 1px solid #c8ced6;
          border-radius: 6px;
          background-color: #ffffff;
          white-space: pre;
          cursor: text;
        }
        input:focus-visible { outline: 2px solid #4f8cf7; outline-offset: 0px; border-color: #4f8cf7; }
        input:disabled { opacity: 0.6; }
        textarea {
          display: inline-block;
          min-width: 160px;
          min-height: 72px;
          padding: 6px 8px;
          border: 1px solid #c8ced6;
          border-radius: 6px;
          background-color: #ffffff;
          white-space: pre-wrap;
          overflow-wrap: anywhere;
          cursor: text;
        }
        textarea:focus-visible { outline: 2px solid #4f8cf7; outline-offset: 0; border-color: #4f8cf7; }
        textarea:disabled { opacity: 0.6; }
        """;

    static StyleSheet? s_sheet;

    /// <summary>解析后的默认样式表（进程内共享，不可变）。</summary>
    public static StyleSheet Sheet => s_sheet ??= StyleSheet.Parse(Text, "AwAUI default stylesheet");
}
