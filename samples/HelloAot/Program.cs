using AwAUI;
using AwAUI.Desktop;
using AwAUI.Platform;
using AwAUI.UI;
using HelloAot;

// 组合根（design/01 规则 5）：应用只配置平台无关的部分。
// UsePlatformDetect() 使用构建期选定的后端；应用不引用 Win32 / AppKit / X11 中的任何一个。
return AwAppBuilder.Create()
    .UsePlatformDetect()
    .UseSoftwareRenderer()
    .UseDefaultTextEngine()
    .Run<HelloApplication>();

/// <summary>应用对象：只负责创建主窗口；平台窗口、生命周期与消息循环由组合根装配。</summary>
sealed class HelloApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (DesktopApplicationLifetime)Lifetime!;
        lifetime.MainWindow = new Window(new WindowOptions
        {
            Title = "AwAUI Hello",
            Size = new Size(420, 260),
        })
        {
            Content = new App(),
        };
    }
}
