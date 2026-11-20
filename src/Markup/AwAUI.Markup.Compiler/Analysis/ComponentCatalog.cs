using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.Analysis;

/// <summary>组件属性的类别（design/17「组件信息从哪里来」）。</summary>
public enum PropKind
{
    /// <summary>字符串：引号内是字面量，可混合 @expr。</summary>
    String,
    /// <summary>布尔：只写属性名即为 true。</summary>
    Bool,
    /// <summary>NodeList 类型的插槽。</summary>
    Slot,
    /// <summary><c>Func&lt;T, Node&gt;</c> 类型的模板。</summary>
    Template,
    /// <summary>委托（事件回调）。</summary>
    Delegate,
    /// <summary>其他类型：引号内整体是 C# 表达式。</summary>
    Other,
}

/// <summary>组件的一个 Prop：名称 + 类别（+ 可选的类型全名）。值相等。</summary>
public sealed class PropInfo : IEquatable<PropInfo>
{
    public PropInfo(string name, PropKind kind, string? typeName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Kind = kind;
        TypeName = typeName;
    }

    public string Name { get; }
    public PropKind Kind { get; }

    /// <summary>
    /// 属性类型在生成代码中的写法（<c>global::</c> 全限定，如 <c>global::System.Action&lt;global::AwAUI.UI.PointerEvent&gt;</c>）；
    /// 纯语法目录不知道类型时为 null。用于决定 <c>@onxxx</c> 是否需要按参数个数包装。
    /// </summary>
    public string? TypeName { get; }

    public bool Equals(PropInfo? other) =>
        other is not null && Name == other.Name && Kind == other.Kind && TypeName == other.TypeName;

    public override bool Equals(object? obj) => Equals(obj as PropInfo);

    public override int GetHashCode() => (Name.GetHashCode() * 31 + (int)Kind) * 31 + (TypeName?.GetHashCode() ?? 0);
}

/// <summary>组件信息：全名与 Props。值相等（供源生成器的增量缓存比较）。</summary>
public sealed class ComponentInfo : IEquatable<ComponentInfo>
{
    Dictionary<string, PropInfo>? _byName;

    public ComponentInfo(string fullName, IReadOnlyList<PropInfo> props, bool isElement = false, string? captureUnmatchedProp = null)
    {
        FullName = fullName ?? throw new ArgumentNullException(nameof(fullName));
        Props = props ?? throw new ArgumentNullException(nameof(props));
        IsElement = isElement;
        CaptureUnmatchedProp = captureUnmatchedProp;
    }

    /// <summary>
    /// 标了 <c>[CaptureUnmatchedAttributes]</c> 的 Prop 名（类型为 <c>AwAUI.UI.AttributeDictionary</c>）：标签上小写开头、没有对应 Prop 的属性、
    /// 没有对应 Prop 的 <c>@onxxx</c> 与 <c>@attributes="..."</c> 收集到这里（相当于 Blazor 的 CaptureUnmatchedValues）：先写入全部展开值，
    /// 再写显式属性（显式优先、与位置无关；<c>class</c> / <c>style</c> 拼接），与元素上的展开规则一致。没有为 null。
    /// </summary>
    public string? CaptureUnmatchedProp { get; }

    /// <summary>可在 <c>global::</c> 之后书写的类型全名（不含类型实参），如 <c>MyApp.TodoRow</c>。</summary>
    public string FullName { get; }
    public IReadOnlyList<PropInfo> Props { get; }

    /// <summary>
    /// 标签解析到的是内建元素描述类型（派生自 <c>AwAUI.UI.ElementNode</c>，如大写的 <c>&lt;Button&gt;</c> → <c>AwAUI.UI.Button</c>）
    /// 而不是组件：它产出的元素属于当前组件，参与作用域样式；小写的 HTML 式属性名（<c>aria-*</c>、<c>tabindex</c> ...）按内建元素处理。
    /// </summary>
    public bool IsElement { get; }

    public PropInfo? FindProp(string name)
    {
        if (Props.Count <= 8)
        {
            foreach (var p in Props)
                if (p.Name == name) return p;
            return null;
        }
        if (_byName is null)
        {
            var map = new Dictionary<string, PropInfo>(StringComparer.Ordinal);
            foreach (var p in Props)
                if (!map.ContainsKey(p.Name)) map.Add(p.Name, p);
            _byName = map;
        }
        return _byName.TryGetValue(name, out var found) ? found : null;
    }

    public bool Equals(ComponentInfo? other)
    {
        if (other is null) return false;
        if (FullName != other.FullName || IsElement != other.IsElement || CaptureUnmatchedProp != other.CaptureUnmatchedProp
            || Props.Count != other.Props.Count) return false;
        for (int i = 0; i < Props.Count; i++)
            if (!Props[i].Equals(other.Props[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ComponentInfo);

    public override int GetHashCode() => FullName.GetHashCode() * 31 + Props.Count;
}

/// <summary>解析标签名时可见的命名空间：当前文件的命名空间与 @using。</summary>
public sealed class ImportScope
{
    public ImportScope(string? currentNamespace, IReadOnlyList<string> usings)
    {
        CurrentNamespace = currentNamespace;
        Usings = usings;
    }

    public string? CurrentNamespace { get; }
    public IReadOnlyList<string> Usings { get; }
}

/// <summary>
/// 组件目录：编译器本体不依赖 Roslyn，通过它查询组件的 Props（design/17）。
/// 源生成器基于 Roslyn 的编译实现它；命令行等场景可用 <see cref="StaticComponentCatalog"/>。
/// </summary>
public abstract class ComponentCatalog
{
    /// <summary>
    /// 按当前文件的 @using 与命名空间解析标签名；找不到返回 null。泛型标签的 <paramref name="tagName"/> 带元数后缀（<c>Grid`1</c>）。
    /// </summary>
    public abstract ComponentInfo? Resolve(string tagName, ImportScope scope);
}

/// <summary>由固定清单构成的纯语法目录：按全名，或按「当前命名空间 / @using + 标签名」匹配。</summary>
public sealed class StaticComponentCatalog : ComponentCatalog
{
    readonly Dictionary<string, ComponentInfo> _byFullName = new(StringComparer.Ordinal);

    public StaticComponentCatalog(IEnumerable<ComponentInfo> components)
    {
        foreach (var c in components) _byFullName[c.FullName] = c;
    }

    public override ComponentInfo? Resolve(string tagName, ImportScope scope)
    {
        int tick = tagName.IndexOf('`');
        if (tick >= 0) tagName = tagName.Substring(0, tick); // 清单按不带元数的全名登记
        if (_byFullName.TryGetValue(tagName, out var info)) return info;
        if (scope.CurrentNamespace != null && _byFullName.TryGetValue(scope.CurrentNamespace + "." + tagName, out info)) return info;
        foreach (var u in scope.Usings)
            if (_byFullName.TryGetValue(u + "." + tagName, out info)) return info;
        return null;
    }
}
