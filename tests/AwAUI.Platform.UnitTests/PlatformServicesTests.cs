using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwAUI.Platform;

namespace AwAUI.Platform.UnitTests;

/// <summary>
/// 平台服务基类的行为（design/04）：显示器查找、文件选择便捷方法、启动器 URI 校验、
/// 以及新增富剪贴板成员在不支持平台上的默认结果。具体平台实现由各自后端的测试覆盖。
/// </summary>
public class PlatformServicesTests
{
    sealed class FakeScreens(params Screen[] screens) : Screens
    {
        public override IReadOnlyList<Screen> All { get; } = screens;
        public override Screen Primary => screens[0];
        public void RaiseChanged() => OnChanged();
    }

    [Fact]
    public void Screens_finds_the_screen_containing_a_point_and_not_the_gap_between_screens()
    {
        var left = new Screen("left", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), 1, isPrimary: true);
        var right = new Screen("right", new Rect(2000, 0, 1280, 1024), new Rect(2000, 0, 1280, 984), 2);
        var screens = new FakeScreens(left, right);

        Assert.Same(left, screens.FromPoint(new Point(100, 100)));
        Assert.Same(right, screens.FromPoint(new Point(2100, 50)));
        Assert.Null(screens.FromPoint(new Point(1950, 50)));
        Assert.Same(left, screens.Primary);
        Assert.Equal(2, screens.All.Count);
    }

    [Fact]
    public void Screens_raises_changed_when_the_platform_layout_changes()
    {
        var screens = new FakeScreens(new Screen("only", new Rect(0, 0, 800, 600), new Rect(0, 0, 800, 600), 1, true));
        int changed = 0;
        screens.Changed += () => changed++;
        screens.RaiseChanged();
        Assert.Equal(1, changed);
    }

    sealed class FakeClipboard : Clipboard
    {
        public override Task<string?> GetTextAsync() => Task.FromResult<string?>("text");
        public override Task<bool> SetTextAsync(string text) => Task.FromResult(true);
    }

    [Fact]
    public async Task Clipboard_defaults_are_safe_when_a_platform_only_implements_text()
    {
        var clipboard = new FakeClipboard();
        Assert.Equal("text", await clipboard.GetTextAsync());
        Assert.Null(await clipboard.GetHtmlAsync());
        Assert.False(await clipboard.SetHtmlAsync("<b>x</b>"));
        Assert.Null(await clipboard.GetImageAsync());
        Assert.Empty(await clipboard.GetFilesAsync());
        Assert.False(await clipboard.SetFilesAsync(Array.Empty<StorageFile>()));
    }

    sealed class FakeStorageProvider : StorageProvider
    {
        public FilePickerOptions? LastOptions;
        public StorageFile? File = new FakeFile("a.txt");

        public override Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options)
        {
            LastOptions = options;
            return Task.FromResult<IReadOnlyList<StorageFile>>(new[] { File! });
        }

        public override Task<StorageFile?> SaveFileAsync(FilePickerOptions options) => Task.FromResult<StorageFile?>(null);
        public override Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options) => Task.FromResult<StorageFolder?>(null);
    }

    sealed class FakeFile(string name) : StorageFile
    {
        public override string Name { get; } = name;
        public override string Path => "/tmp/" + Name;
        public override Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new MemoryStream());
        public override Task<Stream> OpenWriteAsync() => Task.FromResult<Stream>(new MemoryStream());
    }

    [Fact]
    public async Task StorageProvider_OpenFileAsync_returns_the_first_file_and_empty_result_returns_null()
    {
        var provider = new FakeStorageProvider();
        var options = new FilePickerOptions { AllowMultiple = true };
        var file = await provider.OpenFileAsync(options);
        Assert.Same(provider.File, file);
        Assert.Same(options, provider.LastOptions);

        var empty = new EmptyProvider();
        Assert.Null(await empty.OpenFileAsync(new FilePickerOptions()));
    }

    sealed class EmptyProvider : StorageProvider
    {
        public override Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options) =>
            Task.FromResult<IReadOnlyList<StorageFile>>(Array.Empty<StorageFile>());
        public override Task<StorageFile?> SaveFileAsync(FilePickerOptions options) => Task.FromResult<StorageFile?>(null);
        public override Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options) => Task.FromResult<StorageFolder?>(null);
    }

    sealed class FakeLauncher : Launcher
    {
        public Uri? Last;
        public override Task<bool> LaunchUriAsync(Uri uri)
        {
            Last = uri;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void Launcher_default_validation_accepts_only_web_and_mail_schemes()
    {
        var launcher = new FakeLauncher();
        Assert.True(launcher.CanLaunchUri(new Uri("https://example.com/a")));
        Assert.True(launcher.CanLaunchUri(new Uri("mailto:a@example.com")));
        Assert.False(launcher.CanLaunchUri(new Uri("javascript:alert(1)")));
        Assert.False(launcher.CanLaunchUri(new Uri("file:///etc/passwd")));
        Assert.False(launcher.CanLaunchUri(null));
        Assert.False(launcher.CanLaunchUri(new Uri("relative/path", UriKind.Relative)));
    }

    [Fact]
    public void Thickness_reports_zero_and_value_equality()
    {
        Assert.True(Thickness.Zero.IsZero);
        Assert.Equal(new Thickness(1, 2, 3, 4), new Thickness(left: 1, top: 2, right: 3, bottom: 4));
        Assert.NotEqual(new Thickness(1), new Thickness(2));
        Assert.Equal(new Thickness(5), new Thickness(5, 5, 5, 5));
    }

    [Fact]
    public void RawPointerEvent_marks_predicted_samples_without_changing_existing_construction()
    {
        var real = new RawPointerEvent(RawPointerEventType.Move, PointerType.Touch, 7, new Point(1, 2),
            PointerButtons.None, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero);
        var predicted = new RawPointerEvent(RawPointerEventType.Move, PointerType.Touch, 7, new Point(3, 4),
            PointerButtons.None, PointerButtons.Left, KeyModifiers.None, TimeSpan.FromMilliseconds(8),
            pressure: 0.5f, wheelDeltaX: 0, wheelDeltaY: 0, preciseWheel: false, isPredicted: true);

        Assert.False(real.IsPredicted);
        Assert.True(predicted.IsPredicted);
        Assert.Equal(7, predicted.PointerId);
    }

    [Fact]
    public void WindowOptions_default_app_id_is_null()
    {
        // Wayland 的 xdg_toplevel.set_app_id / 移动端包标识提示；未设置时由后端使用默认值。
        Assert.Null(new WindowOptions().AppId);
    }
}
