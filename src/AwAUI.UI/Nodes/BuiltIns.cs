using System;

namespace AwAUI.UI;

/// <summary>通用盒子（标签 <c>div</c>），布局与装饰全由 CSS 决定。</summary>
public sealed class Div : ElementNode
{
    protected internal override Element CreateElement() => new Element("div");
}

/// <summary>行内富文本片段（标签 <c>span</c>），用在 <see cref="Text"/> 内部。</summary>
public sealed class Span : ElementNode
{
    public Span() { }

    public Span(string? content)
    {
        if (content is not null) Children.Add(content);
    }

    protected internal override Element CreateElement() => new Element("span");
}

/// <summary>
/// 文本段落（标签 <c>text</c>）：行内排版只发生在它内部。内容是 <see cref="Content"/>（若有）加上子节点
/// （字符串与 <see cref="Span"/>）。
/// <para>
/// 注意命名：本类型为 <c>AwAUI.UI.Text</c>。位于 <c>AwAUI.*</c>（<c>AwAUI.UI</c> 以外）命名空间中的代码，
/// 简单名 <c>Text</c> 会先解析到命名空间 <c>AwAUI.Text</c>（文本栈），此时请写 <c>UI.Text</c> 或使用别名。
/// </para>
/// </summary>
public sealed class Text : ElementNode
{
    public Text() { }

    public Text(string? content) => Content = content;

    /// <summary>段首的纯文本内容。</summary>
    public string? Content { get; set; }

    protected internal override Element CreateElement() => new TextElement();

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        base.Diff(oldNode, element);
        var old = (Text?)oldNode;
        if (old is null || !string.Equals(old.Content, Content, StringComparison.Ordinal))
            ((TextElement)element).SetContent(Content);
    }
}

/// <summary>
/// Text block (tag <c>text</c>): identical to <see cref="Text"/> and exposed as static text
/// (<c>statictext</c>) in the semantic tree. The name aligns with platform control naming; either type
/// can be used interchangeably.
/// </summary>
public class TextBlock : ElementNode
{
    public TextBlock() { }

    public TextBlock(string? content) => Content = content;

    /// <summary>Plain text at the start of the block.</summary>
    public string? Content { get; set; }

    protected internal override Element CreateElement() => new TextElement();

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        base.Diff(oldNode, element);
        var old = (TextBlock?)oldNode;
        if (old is null || !string.Equals(old.Content, Content, StringComparison.Ordinal))
            ((TextElement)element).SetContent(Content);
    }
}

/// <summary>Paragraph (tag <c>text</c>): semantic alias of <see cref="TextBlock"/> that produces accessible static text.</summary>
public sealed class Paragraph : TextBlock
{
    public Paragraph() { }

    public Paragraph(string? content) : base(content) { }
}

/// <summary>
/// Selectable static text block (tag <c>text</c>): same as <see cref="TextBlock"/>; the semantic tree
/// exposes a read-only TextPattern (value and selection range). Plain <see cref="Text"/> and
/// <see cref="TextBlock"/> are treated as read-only text as well.
/// </summary>
public sealed class SelectableTextBlock : TextBlock
{
    public SelectableTextBlock() { }

    public SelectableTextBlock(string? content) : base(content) { }
}

/// <summary>
/// Label (tag <c>label</c>): contributes text to an accessible name through <see cref="For"/>
/// (HTML <c>for</c>) or by wrapping the control. The label itself does not produce a semantic node;
/// the semantic tree resolves the association when computing names.
/// </summary>
public sealed class Label : ElementNode
{
    public Label() { }

    public Label(string? text)
    {
        if (text is not null) Children.Add(text);
    }

    /// <summary>The associated control's <c>id</c> (HTML <c>for</c>). Empty means a wrapping label.</summary>
    public string? For { get; set; }

    protected override bool MergeAttribute(string name, object value)
    {
        if (string.Equals(name, "for", StringComparison.OrdinalIgnoreCase))
        {
            For ??= AttributeText(name, value);
            return true;
        }
        return base.MergeAttribute(name, value);
    }

    protected internal override Element CreateElement() => new Element("label");

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        // for 以普通属性写到元素上，供语义树按 id 关联；每次 Diff 都同步到当前描述节点的属性表。
        Attributes["for"] = For;
        base.Diff(oldNode, element);
    }
}

/// <summary>
/// 图片（标签 <c>img</c>）：位图（PNG、JPEG、GIF、WebP、BMP、ICO，GIF / WebP 动画会播放）或作为图片的 SVG。
/// 来源见 <see cref="ImageSource"/>（字符串隐式转换为 <see cref="ImageSource.FromUri"/>）；同一来源在 UI 根内共享解码结果，较大的图片在线程池上解码，
/// 解码完成前不占尺寸也不绘制（给出 <see cref="Width"/> / <see cref="Height"/> 可预先占位）。
/// <para>
/// 缩放、位置与采样只由 CSS 决定（<c>object-fit</c>、<c>object-position</c>、<c>image-rendering</c>，另有 <c>aspect-ratio</c>），
/// 与 HTML 一样没有对应的节点属性：写在样式表、类或 <see cref="ElementNode.Style"/> 中，例如 <c>Style = "object-fit: cover"</c>。
/// </para>
/// </summary>
public sealed class Image : ElementNode
{
    /// <summary>图片来源；可直接赋值字符串（<c>data:</c> URI、应用资源名或文件路径）。</summary>
    public ImageSource? Source { get; set; }

    /// <summary>替代文本（无障碍名称；缺失时分析器报 AW3001）。</summary>
    public string? Alt { get; set; }

    /// <summary>固有宽度提示（HTML 的 <c>width</c> 属性，逻辑像素）：加载完成前即可占位，并覆盖图片的固有宽度。</summary>
    public float? Width { get; set; }

    /// <summary>固有高度提示（HTML 的 <c>height</c> 属性）。只给一个时另一个按图片宽高比推出。</summary>
    public float? Height { get; set; }

    protected internal override Element CreateElement() => new ImageElement();

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        base.Diff(oldNode, element);
        var old = (Image?)oldNode;
        var e = (ImageElement)element;
        if (old is null || old.Source != Source) e.SetSource(Source);
        if (old is null || !string.Equals(old.Alt, Alt, StringComparison.Ordinal)) e.SetAlt(Alt);
        if (old is null || old.Width != Width || old.Height != Height) e.SetSizeHints(Width, Height);
    }
}

/// <summary>
/// 矢量图（标签 <c>svg</c>）：显示一个已解析的 <see cref="AwAUI.Svg.SvgDocument"/>。<c>currentColor</c> 取计算样式的 <c>color</c>，
/// 因此图标随文字颜色着色；尺寸默认取文档的固有尺寸，CSS 宽高可覆盖（按文档的 preserveAspectRatio 放置）。
/// 图标的常用写法见 <see cref="Icon"/>。
/// <para>
/// 注意命名：本类型为 <c>AwAUI.UI.Svg</c>，与命名空间 <c>AwAUI.Svg</c>（SVG 解析与渲染）同名。<c>AwAUI.*</c>（<c>AwAUI.UI</c> 以外）命名空间中的代码
/// 写 <c>Svg</c> 会先解析到命名空间，此时请写 <c>UI.Svg</c> 或使用别名（同 <see cref="Text"/>）。
/// </para>
/// </summary>
public sealed class Svg : ElementNode
{
    public Svg() { }

    public Svg(AwAUI.Svg.SvgDocument? document) => Document = document;

    /// <summary>要显示的文档（不可变，可在多个元素间共享）。</summary>
    public AwAUI.Svg.SvgDocument? Document { get; set; }

    /// <summary>固有宽度提示（覆盖文档的固有宽度）。</summary>
    public float? Width { get; set; }

    /// <summary>固有高度提示。</summary>
    public float? Height { get; set; }

    protected internal override Element CreateElement() => new SvgElement();

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        base.Diff(oldNode, element);
        var old = (Svg?)oldNode;
        var e = (SvgElement)element;
        if (old is null || !ReferenceEquals(old.Document, Document)) e.SetDocument(Document);
        if (old is null || old.Width != Width || old.Height != Height) e.SetSizeHints(Width, Height);
    }
}

/// <summary>
/// 内建按钮（标签 <c>button</c>）：默认可聚焦；<see cref="ElementNode.OnClick"/> 由指针点击、回车（按下时）
/// 与空格（抬起时）共同触发；禁用时不可聚焦也不触发。标准控件库的 <c>Button</c> 组件在它之上构建。
/// </summary>
public sealed class Button : ElementNode
{
    public Button() { }

    public Button(string? label)
    {
        if (label is not null) Children.Add(label);
    }

    public bool Disabled { get; set; }

    /// <summary>属性展开：<c>disabled</c>（未禁用时才写入，见 <see cref="ElementNode.MergeAttributes(System.Collections.Generic.IEnumerable{System.Collections.Generic.KeyValuePair{string, object}})"/>）。</summary>
    protected override bool MergeAttribute(string name, object value)
    {
        if (string.Equals(name, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            if (!Disabled) Disabled = AttributeFlag(value);
            return true;
        }
        return base.MergeAttribute(name, value);
    }

    protected internal override Element CreateElement() => new ButtonElement();

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        base.Diff(oldNode, element);
        var old = (Button?)oldNode;
        if (old is null || old.Disabled != Disabled) ((ButtonElement)element).SetDisabled(Disabled);
    }
}

/// <summary>
/// 文本输入（标签 <c>input</c>；<c>textarea</c> 为多行）。支持光标移动、选区、剪贴板、输入法组合与撤销 / 重做；
/// 编辑键、指针定位与选区都由输入管理器处理，输入法经 <see cref="AwAUI.Platform.ITextInputClient"/> 接入。
/// <para>
/// 受控用法：<c>Value = draft.Value, OnInput = e =&gt; draft.Value = e.Value</c>；<c>Value</c> 为 null 时由元素自持内容（非受控）。
/// 受控时重建会把元素的值同步为 <see cref="Value"/>，程序改变值的操作会结束进行中的组合并清空撤销历史。
/// </para>
/// </summary>
public class Input : ElementNode
{
    /// <summary>受控值；为 null 时输入框自持内容（非受控）。</summary>
    public string? Value { get; set; }

    public string? Placeholder { get; set; }

    /// <summary>输入类型（<c>text</c>、<c>password</c>、<c>search</c> ...）。</summary>
    public string? InputType { get; set; }

    public bool Disabled { get; set; }

    public bool ReadOnly { get; set; }

    /// <summary>
    /// 插入点闪烁周期；null 使用 <see cref="RenderPipeline.CaretBlinkInterval"/>，<see cref="TimeSpan.Zero"/> 或负值表示常亮。
    /// </summary>
    public TimeSpan? CaretBlinkInterval { get; set; }

    /// <summary>每次内容变化。</summary>
    public Action<InputEvent>? OnInput { get => Get<InputEvent>(EventKind.Input); set => Set(EventKind.Input, value); }

    /// <summary>失去焦点时，若内容自获得焦点后有变化。</summary>
    public Action<InputEvent>? OnChange { get => Get<InputEvent>(EventKind.Change); set => Set(EventKind.Change, value); }

    /// <summary>按回车提交。</summary>
    public Action<SubmitEvent>? OnSubmit { get => Get<SubmitEvent>(EventKind.Submit); set => Set(EventKind.Submit, value); }

    /// <summary>属性展开：<c>value placeholder type disabled readonly</c> 与 <c>oninput onchange onsubmit</c>（显式设置过的保持不变）。</summary>
    protected override bool MergeAttribute(string name, object value)
    {
        switch (name.ToLowerInvariant())
        {
            case "value": Value ??= AttributeText(name, value); return true;
            case "placeholder": Placeholder ??= AttributeText(name, value); return true;
            case "type": InputType ??= AttributeText(name, value); return true;
            case "disabled": if (!Disabled) Disabled = AttributeFlag(value); return true;
            case "readonly": if (!ReadOnly) ReadOnly = AttributeFlag(value); return true;
            case "oninput": return MergeHandler<InputEvent>(EventKind.Input, name, value);
            case "onchange": return MergeHandler<InputEvent>(EventKind.Change, name, value);
            case "onsubmit": return MergeHandler<SubmitEvent>(EventKind.Submit, name, value);
            default: return base.MergeAttribute(name, value);
        }
    }

    protected internal override Element CreateElement() => new InputElement();

    protected internal override void Diff(ElementNode? oldNode, Element element)
    {
        base.Diff(oldNode, element);
        var old = (Input?)oldNode;
        var e = (InputElement)element;
        // 受控值与元素的当前值比较（用户输入会先改变元素），保证重建后元素与状态一致
        if (Value is not null && !string.Equals(e.Value, Value, StringComparison.Ordinal)) e.SetValue(Value);
        if (old is null || !string.Equals(old.Placeholder, Placeholder, StringComparison.Ordinal)) e.SetPlaceholder(Placeholder);
        if (old is null || !string.Equals(old.InputType, InputType, StringComparison.Ordinal)) e.SetInputType(InputType);
        if (old is null || old.Disabled != Disabled) e.SetDisabled(Disabled);
        if (old is null || old.ReadOnly != ReadOnly) e.SetReadOnly(ReadOnly);
        if (old is null || old.CaretBlinkInterval != CaretBlinkInterval) e.CaretBlinkInterval = CaretBlinkInterval;
    }
}

/// <summary>Multiline text editor with wrapping, selection, and input method support.</summary>
public sealed class TextArea : Input
{
    protected internal override Element CreateElement() => new InputElement("textarea");
}
