using System;

namespace AwAUI.UI;

/// <summary>诊断严重级别。</summary>
public enum DiagnosticSeverity : byte
{
    Info,
    Warning,
    Error,
}

/// <summary>运行时诊断（进入 DevTools 控制台）。代码沿用统一体系（design/12「诊断代码」）。</summary>
public sealed class UIDiagnostic
{
    public UIDiagnostic(string code, DiagnosticSeverity severity, string message, Component? component = null)
    {
        Code = code;
        Severity = severity;
        Message = message;
        Component = component;
    }

    /// <summary>诊断代码，如 <c>AW1005</c>。</summary>
    public string Code { get; }

    public DiagnosticSeverity Severity { get; }

    public string Message { get; }

    /// <summary>相关组件（DevTools 可跳转到其源码）。</summary>
    public Component? Component { get; }

    public override string ToString() => Code + ": " + Message;
}

/// <summary>异常发生的阶段。</summary>
public enum ErrorPhase : byte
{
    Build,
    Mount,
    Unmount,
    Effect,
    EventHandler,
}

/// <summary>未被任何 <see cref="ErrorBoundary"/> 捕获的错误。</summary>
public sealed class UIError
{
    public UIError(Exception exception, Component? component, ErrorPhase phase)
    {
        Exception = exception;
        Component = component;
        Phase = phase;
    }

    public Exception Exception { get; }

    /// <summary>出错的组件（事件处理器的错误归属于产出该元素的组件）。</summary>
    public Component? Component { get; }

    public ErrorPhase Phase { get; }

    public override string ToString() => Phase + " @ " + (Component?.GetType().Name ?? "(root)") + ": " + Exception.Message;
}

/// <summary>标记组件的源文件（<c>.awaui</c> 编译器生成），供 DevTools 跳转。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentSourceAttribute : Attribute
{
    public ComponentSourceAttribute(string path) => Path = path;

    public string Path { get; }
}

/// <summary>事件处理器适配（<c>.awaui</c> 中 <c>@onclick</c> 的值可以是无参或单参的方法组 / lambda）。</summary>
public static class EventHandlers
{
    public static Action<T>? Wrap<T>(Action<T>? handler) where T : Event => handler;

    public static Action<T>? Wrap<T>(Action? handler) where T : Event => handler is null ? null : _ => handler();
}

/// <summary><c>@bind</c> 展开使用的读写辅助。</summary>
public static class Binding
{
    public static T Get<T>(State<T> state) => state.Value;

    public static T Get<T>(T value) => value;

    public static void Set<T>(State<T> state, T value) => state.Value = value;
}
