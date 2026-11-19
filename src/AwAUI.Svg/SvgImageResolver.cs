using System;
using AwAUI.Graphics;

namespace AwAUI.Svg;

/// <summary>
/// &lt;image&gt; 元素的图像来源（扩展点）。AwAUI.Svg 只依赖 Foundation 与 Graphics，不含图片解码器：调用方经
/// <see cref="SvgParseOptions.ImageResolver"/> 提供实现（通常用 AwAUI.Imaging 的 <c>ImageDecoder</c> 解码），没有提供时
/// &lt;image&gt; 被忽略并记录诊断。
/// <para>
/// 调用时机与线程：在 <see cref="SvgDocument.Parse(string, SvgParseOptions?)"/> 构建渲染树时同步调用（每个 &lt;image&gt; 元素至多一次，
/// 被 &lt;use&gt; 多次引用的同一元素共用结果），所以实现必须是同步的、对不可信输入有界（解码上限由实现负责），并且可以抛出异常
/// （按解码失败处理并记录诊断）。
/// </para>
/// <para>
/// 返回的位图：文档按引用保存并在绘制时交给 <see cref="DrawingContext.DrawImage(Bitmap, Rect, Rect, ImageSampling, float)"/>，
/// 文档从不释放它（同一位图可以被解析器缓存并在多个文档之间共享）；不再被引用的位图由 <see cref="Bitmap"/> 的终结器回收。
/// 返回之后不要再改写像素（文档是不可变的）。
/// </para>
/// </summary>
public abstract class SvgImageResolver
{
    /// <summary>
    /// 解码 <c>data:</c> URI 的载荷（已完成 base64 或百分号解码）。<paramref name="mediaType"/> 是 URI 中声明的媒体类型（小写，
    /// 不含参数，可能为空串，例如 <c>image/png</c>）；实现可以忽略它而按内容嗅探。不支持的格式或损坏的数据返回 null。
    /// <c>image/svg+xml</c> 不会传到这里（嵌套 SVG 不支持）。
    /// </summary>
    public abstract Bitmap? Decode(ReadOnlySpan<byte> data, string mediaType);

    /// <summary>
    /// 解析不是 <c>data:</c> 的引用（相对或绝对 URL、文件路径）。默认返回 null：与浏览器把 SVG 当作图片使用时一样，不加载外部资源。
    /// 实现若要加载，须自行限制可访问的位置（SVG 常来自不可信来源）。
    /// </summary>
    public virtual Bitmap? ResolveExternal(string href) => null;
}
