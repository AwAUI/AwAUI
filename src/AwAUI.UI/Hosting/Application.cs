using System;
using System.Collections.Generic;
using AwAUI.Css;

namespace AwAUI.UI;

/// <summary>
/// 应用对象（design/01 规则 5）：全局样式与生命周期的挂载点。平台、渲染后端与文本引擎仍由组合根
/// （<c>AwAppBuilder</c>）装配；这里只保存与具体平台无关的应用级状态。
/// <para>
/// 生命周期：组合根创建实例并设为 <see cref="Current"/>，注入 <see cref="Lifetime"/> 与应用样式表 →
/// <see cref="Initialize"/> → <see cref="OnFrameworkInitializationCompleted"/>（在这里创建并指派主窗口）→
/// 生命周期运行主循环 → <see cref="OnExit"/>。
/// </para>
/// <para>
/// 窗口不归 <see cref="Application"/> 管理：窗口集合、主窗口与退出策略在 <see cref="DesktopApplicationLifetime"/>；
/// 应用只在初始化钩子里创建窗口并赋给 <see cref="DesktopApplicationLifetime.MainWindow"/>。这样同一套应用代码
/// 以后可以挂到单视图生命周期（移动端 / Web）上。
/// </para>
/// </summary>
public class Application
{
    /// <summary>当前应用；组合根启动期间非 null，退出后清空。测试可以据此确认没有遗留实例。</summary>
    public static Application? Current { get; private set; }

    /// <summary>由组合根注入的生命周期；在 <see cref="Initialize"/> 之前就绪。</summary>
    public ApplicationLifetime? Lifetime { get; internal set; }

    /// <summary>应用级样式表：每个窗口显示时先加入应用样式，再加入窗口自己的样式，最后是组件样式。</summary>
    public IList<StyleSheet> Styles { get; } = new List<StyleSheet>();

    /// <summary>平台与生命周期就绪之后、主窗口显示之前调用（注册服务、准备主题等）。</summary>
    public virtual void Initialize()
    {
    }

    /// <summary>
    /// 框架初始化完成：在这里创建主窗口并赋给 <see cref="DesktopApplicationLifetime.MainWindow"/>，
    /// 或直接显示自己的窗口。桌面生命周期随后显示主窗口并进入消息循环。
    /// </summary>
    public virtual void OnFrameworkInitializationCompleted()
    {
    }

    /// <summary>主循环结束、进程退出之前调用。</summary>
    /// <param name="exitCode">生命周期最终确定的退出码。</param>
    public virtual void OnExit(int exitCode)
    {
    }

    internal static void SetCurrent(Application application)
    {
        if (Current is not null)
            throw new InvalidOperationException("Another Application is already running; Application.Current must be cleared before starting a new one.");
        Current = application;
    }

    internal static void ClearCurrent(Application application)
    {
        if (ReferenceEquals(Current, application)) Current = null;
    }
}
