using System;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 错误边界：捕获子树中组件的 <c>Build()</c>、生命周期回调、副作用与事件处理器抛出的异常，改为渲染 <see cref="Fallback"/>。
/// 出错的组件保留上一次的输出直到边界在同一帧内切换到后备界面（整个子树随之卸载）。
/// 边界自身（包括后备界面及其中的组件）抛出的异常交给更外层的边界；没有边界时进入 <see cref="UIRoot.UnhandledError"/>。
/// </summary>
public sealed class ErrorBoundary : Component
{
    readonly State<Exception?> _error = new(default(Exception));

    /// <summary>正常情况下渲染的内容。</summary>
    public NodeList? Children { get; set; }

    /// <summary>出错时渲染的后备界面；为 null 时什么也不渲染。</summary>
    public Func<Exception, Node?>? Fallback { get; set; }

    /// <summary>捕获到异常时的回调（可用于上报）。</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>当前捕获的异常；没有错误时为 null。</summary>
    public Exception? Error => _error.Peek();

    /// <summary>清除错误，重新渲染 <see cref="Children"/>（下一帧）。</summary>
    public void Reset() => _error.Value = null;

    /// <summary>上一次 Build 渲染的是后备界面。</summary>
    bool _showingFallback;

    /// <summary>
    /// 接收子树中 <paramref name="source"/> 抛出的异常。返回 false 表示本边界不处理，交给更外层的边界：
    /// 已经在显示后备界面时，来自后备界面内容（而不是正在卸载的出错子树）的异常属于这种情况。
    /// </summary>
    internal bool Capture(Exception ex, Component? source)
    {
        if (_error.Peek() is not null)
        {
            // 切换到后备界面之前，出错子树中的其他异常只保留第一个；之后的异常来自后备界面本身
            return !_showingFallback || (source is not null && source._unmounting);
        }
        _extraBuildAllowed = true;
        _error.Value = ex;
        try
        {
            OnError?.Invoke(ex);
        }
        catch
        {
            // 上报回调本身的异常不再传播，避免掩盖原始错误
        }
        return true;
    }

    protected override Node Build()
    {
        var error = _error.Value;
        _showingFallback = error is not null;
        if (error is not null) return Fallback?.Invoke(error) ?? new Fragment();
        return new Fragment(Children ?? new NodeList());
    }

    protected override bool PropsEqual(Component other)
    {
        var o = (ErrorBoundary)other;
        return ReferenceEquals(Children, o.Children)
            && EqualityComparer<Func<Exception, Node?>?>.Default.Equals(Fallback, o.Fallback)
            && EqualityComparer<Action<Exception>?>.Default.Equals(OnError, o.OnError);
    }

    protected override void AdoptProps(Component other)
    {
        var o = (ErrorBoundary)other;
        Children = o.Children;
        Fallback = o.Fallback;
        OnError = o.OnError;
    }
}
