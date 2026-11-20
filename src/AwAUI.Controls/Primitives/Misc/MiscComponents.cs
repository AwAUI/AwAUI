using System;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 视觉隐藏但对辅助技术可见的内容（Radix <c>VisuallyHidden</c>，即 Tailwind 的 <c>sr-only</c>）：1×1 像素、溢出隐藏、绝对定位。
/// </summary>
public sealed partial class VisuallyHidden : Component
{
    /// <summary>与 Tailwind <c>sr-only</c> 相同的声明（AwAUI.Css 尚不支持的 <c>clip</c> 省略）。</summary>
    public const string HiddenStyle = "position: absolute; border-width: 0; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; white-space: nowrap";

    public NodeList? Children { get; set; }

    public ClassValue Class { get; set; }

    public StyleValue Style { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Attributes { get; set; }

    protected override Node Build() =>
        PartProps.Apply(new Div { Style = HiddenStyle, Children = Children ?? new NodeList() }, Class, Style, Attributes);
}

/// <summary>
/// 滚动锁定（Radix 的 <c>RemoveScroll</c>）：挂载且 <see cref="Enabled"/> 时锁定视口滚动（<see cref="UIRoot.LockScroll"/>），
/// 子节点的元素（及更高的覆盖层）内仍可用滚轮滚动。子节点原样渲染。
/// </summary>
public sealed partial class ScrollLock : Component
{
    IDisposable? _lock;

    public bool Enabled { get; set; } = true;

    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        UseEffect(() =>
        {
            if (!Enabled || Root is null) return null;
            var token = Root.LockScroll(DismissableLayer.FirstElement(this));
            _lock = token;
            return () =>
            {
                token.Dispose();
                if (ReferenceEquals(_lock, token)) _lock = null;
            };
        }, Enabled);
        return new Fragment(Children ?? new NodeList());
    }

    /// <summary>当前是否持有锁。</summary>
    public bool IsLocked => _lock is not null;
}
