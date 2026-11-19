using System;
using System.Collections.Generic;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 受控 / 非受控状态（Radix 的 <c>useControllableState</c>）：组件的值既可以由父级控制（<c>Value</c> + <c>OnValueChange</c>，
/// 可 <c>@bind</c>），也可以由组件自持（只给 <c>DefaultValue</c>）。作为组件的字段使用：
/// <code>
/// readonly ControllableState&lt;bool&gt; open = new();
///
/// protected override Node Build()
/// {
///     bool isOpen = open.Sync(Open, DefaultOpen, OnOpenChange);   // Open 为 bool?：null 表示非受控
///     ...  OnClick = _ =&gt; open.Set(!isOpen) ...
/// }
/// </code>
/// <para>
/// <see cref="Sync(bool, T, T, Action{T})"/> 在 <c>Build()</c> 中调用：它读取（并登记依赖于）内部的 <see cref="State{T}"/>，
/// 所以非受控时 <see cref="Set"/> 会让组件重建；受控时 <see cref="Set"/> 只调用回调，由父级更新属性后重建。
/// <c>DefaultValue</c> 只在第一次 <see cref="Sync(bool, T, T, Action{T})"/> 时采用（与 React 的 useState 初值相同）。
/// </para>
/// <para>
/// 与 Radix 相同：非受控时值变化也会调用回调；受控时，新值与当前受控值相等则不调用。组件生命期中途在受控与非受控之间切换属于用法错误，
/// 记入 <see cref="ModeSwitched"/>（Radix 在开发模式下警告）；切换到非受控时保留最后的受控值作为当前值。
/// </para>
/// <para>只在 UI 线程使用。<see cref="Set"/> 不能在 <c>Build()</c> 中调用（会写 State，见 AW1005）。</para>
/// </summary>
public sealed class ControllableState<T>
{
    readonly IEqualityComparer<T> _comparer;
    State<T>? _state;
    bool _initialized;
    bool _controlled;
    T _controlledValue = default!;
    Action<T>? _onChange;

    public ControllableState() : this(null) { }

    public ControllableState(IEqualityComparer<T>? comparer) => _comparer = comparer ?? EqualityComparer<T>.Default;

    /// <summary>最近一次 <see cref="Sync(bool, T, T, Action{T})"/> 时是否受控。</summary>
    public bool IsControlled => _controlled;

    /// <summary>是否发生过受控 / 非受控的切换（用法错误的诊断标记）。</summary>
    public bool ModeSwitched { get; private set; }

    /// <summary>
    /// 在 <c>Build()</c> 中同步属性并返回当前值。<paramref name="isControlled"/> 为 true 时值来自 <paramref name="value"/>；
    /// 否则来自内部状态（首次以 <paramref name="defaultValue"/> 初始化）。
    /// </summary>
    public T Sync(bool isControlled, T value, T defaultValue, Action<T>? onChange)
    {
        _onChange = onChange;
        if (!_initialized)
        {
            _initialized = true;
            _controlled = isControlled;
            _state = new State<T>(isControlled ? value : defaultValue, _comparer);
        }
        else if (_controlled != isControlled)
        {
            ModeSwitched = true;
            if (!isControlled)
            {
                // 受控 → 非受控：从最后的受控值继续（写入发生在 Build 中，但不读取它的观察者不受影响；用 Untrack 避免登记）
                var last = _controlledValue;
                Reactive.Untrack(() => { _state!.Value = last; return 0; });
            }
            _controlled = isControlled;
        }
        if (isControlled)
        {
            _controlledValue = value;
            return value;
        }
        return _state!.Value;
    }

    /// <summary>当前值（不登记依赖）。还没有 Sync 过时为默认值。</summary>
    public T Value => _controlled ? _controlledValue : _state is null ? default! : _state.Peek();

    /// <summary>
    /// 请求改变值（事件处理器中调用）：非受控时写入内部状态（组件重建）并在值变化时调用回调；受控时只在值与当前受控值不同时调用回调。
    /// </summary>
    public void Set(T next)
    {
        if (_controlled)
        {
            if (!_comparer.Equals(next, _controlledValue)) _onChange?.Invoke(next);
            return;
        }
        if (_state is null)
        {
            // 还没有 Build 过：只能作为初值
            _initialized = true;
            _state = new State<T>(next, _comparer);
            _onChange?.Invoke(next);
            return;
        }
        if (_comparer.Equals(_state.Peek(), next)) return;
        _state.Value = next;
        _onChange?.Invoke(next);
    }

    /// <summary>基于当前值计算新值（<c>open.Update(v =&gt; !v)</c>）。</summary>
    public void Update(Func<T, T> updater)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));
        Set(updater(Value));
    }

    public override string ToString() => (_controlled ? "controlled: " : "uncontrolled: ") + Value;
}

/// <summary><see cref="ControllableState{T}"/> 的便捷重载：用可空属性表示「未提供 = 非受控」。</summary>
public static class ControllableStateExtensions
{
    /// <summary>值类型：<paramref name="value"/> 为 null 表示非受控（<c>public bool? Open { get; set; }</c>）。</summary>
    public static T Sync<T>(this ControllableState<T> state, T? value, T defaultValue, Action<T>? onChange) where T : struct
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        return state.Sync(value.HasValue, value.GetValueOrDefault(), defaultValue, onChange);
    }

    /// <summary>引用类型：<paramref name="value"/> 为 null 表示非受控（受控为 null 的值请用四参数重载）。</summary>
    public static T Sync<T>(this ControllableState<T> state, T? value, T defaultValue, Action<T>? onChange) where T : class
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        return state.Sync(value is not null, value!, defaultValue, onChange);
    }
}
