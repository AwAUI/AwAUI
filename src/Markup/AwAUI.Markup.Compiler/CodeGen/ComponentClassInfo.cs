using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.CodeGen;

/// <summary>组件的一个 Prop 声明（用于生成 Props 胶水）。值相等。</summary>
public sealed class ComponentPropDeclaration : IEquatable<ComponentPropDeclaration>
{
    public ComponentPropDeclaration(string name, string typeName, bool isInitOnly = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        IsInitOnly = isInitOnly;
    }

    /// <summary>属性名（不含 <c>@</c> 转义）。</summary>
    public string Name { get; }

    /// <summary>在生成的类中可书写的类型（源生成器给出 <c>global::</c> 全限定名，语法扫描给出原文）。</summary>
    public string TypeName { get; }

    /// <summary>只有 <c>init</c> 访问器：实例上无法更新，变化时组件重新挂载。</summary>
    public bool IsInitOnly { get; }

    public bool Equals(ComponentPropDeclaration? other) =>
        other is not null && Name == other.Name && TypeName == other.TypeName && IsInitOnly == other.IsInitOnly;

    public override bool Equals(object? obj) => Equals(obj as ComponentPropDeclaration);

    public override int GetHashCode() => (Name.GetHashCode() * 31 + TypeName.GetHashCode()) * 31 + (IsInitOnly ? 1 : 0);

    public override string ToString() => TypeName + " " + Name + (IsInitOnly ? " { init; }" : "");
}

/// <summary>
/// 语义层面的组件类信息：由源生成器根据 Roslyn 符号（<c>@code</c> 成员 + 代码后置文件 + 基类链）得出，传给代码生成；
/// 不提供时代码生成对 <c>@code</c> 做纯语法扫描（只看得到 <c>@code</c>，且假定 <c>@inherits</c> 的基类自带胶水）。值相等。
/// </summary>
public sealed class ComponentClassInfo : IEquatable<ComponentClassInfo>
{
    public ComponentClassInfo(
        IReadOnlyList<ComponentPropDeclaration> props,
        bool baseHasGlue,
        bool declaresPropsEqual = false,
        bool declaresAdoptProps = false,
        bool declaresCanAdoptProps = false,
        IReadOnlyList<string>? stateBindTargets = null)
    {
        Props = props ?? throw new ArgumentNullException(nameof(props));
        BaseHasGlue = baseHasGlue;
        DeclaresPropsEqual = declaresPropsEqual;
        DeclaresAdoptProps = declaresAdoptProps;
        DeclaresCanAdoptProps = declaresCanAdoptProps;
        StateBindTargets = stateBindTargets ?? Array.Empty<string>();
    }

    /// <summary>本类负责的 Props（含没有胶水的基类组件上声明的 Props）。</summary>
    public IReadOnlyList<ComponentPropDeclaration> Props { get; }

    /// <summary>基类组件自己有胶水（生成的或手写的）：生成的方法先调用基类实现。</summary>
    public bool BaseHasGlue { get; }

    /// <summary>类中（<c>@code</c> 或代码后置文件）已手写了 <c>PropsEqual</c> 重写，不再生成。</summary>
    public bool DeclaresPropsEqual { get; }

    public bool DeclaresAdoptProps { get; }

    public bool DeclaresCanAdoptProps { get; }

    /// <summary>
    /// <c>@bind</c> / <c>@bind-X</c> 的目标表达式中类型为 <c>State&lt;T&gt;</c> 的那些（去掉首尾空白后的原文）：
    /// 它们展开为 <c>Binding.Get / Binding.Set</c>，其余目标按可赋值表达式展开（<c>x = value</c>）。
    /// </summary>
    public IReadOnlyList<string> StateBindTargets { get; }

    public bool Equals(ComponentClassInfo? other)
    {
        if (other is null) return false;
        if (BaseHasGlue != other.BaseHasGlue || DeclaresPropsEqual != other.DeclaresPropsEqual
            || DeclaresAdoptProps != other.DeclaresAdoptProps || DeclaresCanAdoptProps != other.DeclaresCanAdoptProps) return false;
        if (Props.Count != other.Props.Count || StateBindTargets.Count != other.StateBindTargets.Count) return false;
        for (int i = 0; i < Props.Count; i++)
            if (!Props[i].Equals(other.Props[i])) return false;
        for (int i = 0; i < StateBindTargets.Count; i++)
            if (StateBindTargets[i] != other.StateBindTargets[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ComponentClassInfo);

    public override int GetHashCode() => Props.Count * 31 + StateBindTargets.Count;
}
