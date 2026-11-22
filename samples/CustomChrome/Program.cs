using AwAUI;
using AwAUI.Desktop;
using AwAUI.Platform;
using AwAUI.UI;
using CustomChrome;

// 无边框窗口 + 自绘标题栏（design/04）。应用仍然不引用任何平台程序集：
// WindowDecorations.None 由目标平台后端落实，标题栏经 IWindowChrome 调用系统窗口动作。
return AwAppBuilder.Create()
    .UsePlatformDetect()
    .UseSoftwareRenderer()
    .UseDefaultTextEngine()
    .Run<ChromeApplication>();

sealed class ChromeApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (DesktopApplicationLifetime)Lifetime!;
        lifetime.MainWindow = new Window(new WindowOptions
        {
            Title = "AwAUI Custom Chrome",
            Size = new Size(620, 380),
            Decorations = WindowDecorations.None,
        })
        {
            Content = new App(),
        };
    }
}
