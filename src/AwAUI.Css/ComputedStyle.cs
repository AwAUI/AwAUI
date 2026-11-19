using System;
using AwAUI.Css.Cascade;
using AwAUI.Css.Syntax;
using AwAUI.Css.Values;

namespace AwAUI.Css;

/// <summary>计算样式的附加标志。</summary>
[Flags]
public enum ComputedStyleFlags
{
    None = 0,
    /// <summary>用到了视口单位（vw/vh/dvh…）：视口变化时需要重算该元素的样式。</summary>
    DependsOnViewport = 1 << 0,
    /// <summary>用到了 <c>env()</c>：安全区等环境变量变化时需要重算。</summary>
    DependsOnEnvironment = 1 << 1,
    /// <summary>
    /// 有非继承属性取了 <c>inherit</c>（如 <c>background-color: inherit</c>）：父元素的非继承分组变化时也需要重算该元素
    /// （只比较继承分组的增量重算会漏掉它）。
    /// </summary>
    InheritsNonInherited = 1 << 2,
}

/// <summary>
/// 元素的计算样式（design/16「ComputedStyle 的存储」）：不可变，按变化频率与继承性分组；
/// 各分组在样式之间按引用共享（写时复制）——非继承分组未被覆盖时共享 <see cref="Initial"/> 的实例，
/// 继承分组未被覆盖时共享父元素的实例。自定义属性存于与父元素共享的持久化映射。
/// 长度保留为计算值（百分比与含百分比的 calc 在布局时解析）。
/// </summary>
public sealed partial class ComputedStyle
{
    /// <summary>所有属性取初始值的样式（根元素的继承来源）。</summary>
    public static ComputedStyle Initial { get; } = CreateInitial();

    /// <summary>自定义属性的计算值。</summary>
    public CustomPropertyMap CustomProperties { get; }

    /// <summary>根元素的字体大小（rem 的基准，px）。</summary>
    public float RootFontSize { get; }

    public ComputedStyleFlags Flags { get; }

    /// <summary>得到颜色的使用值：<c>currentColor</c> 取本元素的 <c>color</c>。</summary>
    public global::AwAUI.Color ResolveColor(CssColor color) => color.Resolve(TextGroup.Color.Value);

    /// <summary>自定义属性的计算值文本；未定义返回 null。</summary>
    public string? GetCustomPropertyText(string name) => CustomProperties.GetText(name);

    /// <summary>按属性名取计算值文本（自定义属性返回其值；简写与未知属性返回 null）。</summary>
    public string? GetPropertyValueText(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (name.StartsWith("--", StringComparison.Ordinal)) return GetCustomPropertyText(name);
        return CssProperties.TryGet(name, out var p) && !p.IsShorthand ? GetPropertyValueText(p.Id) : null;
    }

    /// <summary>
    /// 比较新旧样式，返回变化影响的阶段（design/05 的脏标记表）。引用相同的分组直接跳过，
    /// 因此大多数样式重算后（分组共享）的比较只需几次引用比较。
    /// </summary>
    public static StyleDifference Diff(ComputedStyle? oldStyle, ComputedStyle? newStyle)
    {
        if (ReferenceEquals(oldStyle, newStyle)) return StyleDifference.None;
        if (oldStyle is null || newStyle is null) return StyleDifference.Structure;
        var d = DiffGroups(oldStyle, newStyle);
        if (!ReferenceEquals(oldStyle.CustomProperties, newStyle.CustomProperties)) d |= StyleDifference.CustomProperties;
        return d;
    }

    static ComputedStyle CreateInitial()
    {
        var b = new StyleBuilder();
        b.CreateEmptyGroups();
        var computer = new StyleComputer(new CssEnvironment());
        computer.Begin(null, isRoot: true);
        for (int i = 0; i < CssProperties.LonghandCount; i++)
        {
            var id = (CssPropertyId)i;
            var text = CssProperties.Get(id).InitialText!;
            var value = PropertyParsers.ParseLonghand(id, CssComponentValue.ParseList(text))
                        ?? throw new InvalidOperationException($"属性 {CssProperties.Get(id).Name} 的初始值“{text}”无法解析。");
            b.Apply(id, value, computer);
            if (id == CssPropertyId.FontSize) computer.FontSize = b.Font.FontSize;
        }
        // 初始边框样式为 none：计算后的边框宽度为 0
        StyleEngine.FixUpBorderWidths(b, explicitMask: 0);
        b.CustomProperties = CustomPropertyMap.Empty;
        b.RootFontSize = (float)computer.Environment.DefaultFontSize;
        b.Flags = ComputedStyleFlags.None;
        return new ComputedStyle(b);
    }

    public override string ToString() => $"ComputedStyle(display: {GetPropertyValueText(CssPropertyId.Display)}, font-size: {GetPropertyValueText(CssPropertyId.FontSize)})";
}

/// <summary>构造 <see cref="ComputedStyle"/> 的可变缓冲：分组写时复制，可复用。</summary>
internal sealed partial class StyleBuilder
{
    public CustomPropertyMap CustomProperties = CustomPropertyMap.Empty;
    public float RootFontSize;
    public ComputedStyleFlags Flags;
}
