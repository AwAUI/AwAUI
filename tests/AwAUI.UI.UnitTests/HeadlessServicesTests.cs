using System.Text;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 无头平台服务：可脚本化显示器、文件选择器与临时路径、默认程序启动器，以及支持 HTML / 图片 / 文件列表的内存剪贴板。
/// </summary>
public class HeadlessServicesTests
{
    static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "AwAUI.Headless.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Screens_default_services_and_changed_event()
    {
        var platform = new HeadlessPlatform();
        var screens = platform.HeadlessScreens;

        Assert.Same(screens, platform.Screens);
        Assert.Same(screens, platform.GetService<Screens>());
        var primary = Assert.Single(screens.All);
        Assert.True(primary.IsPrimary);
        Assert.Equal(new Rect(0, 0, 1920, 1080), primary.Bounds);
        Assert.Equal(new Rect(0, 0, 1920, 1080), primary.WorkingArea);
        Assert.Equal(1, primary.Scaling);
        Assert.Same(primary, screens.Primary);
        Assert.Same(primary, screens.FromPoint(new Point(100, 100)));
        Assert.Null(screens.FromPoint(new Point(5000, 5000)));

        int changed = 0;
        screens.Changed += () => changed++;

        var second = new Screen("Second", new Rect(1920, 0, 1280, 720), new Rect(1920, 0, 1280, 700), 1.5);
        screens.Add(second);
        Assert.Equal(2, screens.All.Count);
        Assert.Equal(1, changed);
        Assert.Same(second, screens.FromPoint(new Point(2000, 100)));
        Assert.Equal(700, screens.FromPoint(new Point(2000, 100))!.WorkingArea.Height);

        screens.SetScreens(
            new Screen("A", new Rect(0, 0, 800, 600), new Rect(0, 0, 800, 600), 1, isPrimary: true),
            new Screen("B", new Rect(800, 0, 800, 600), new Rect(800, 0, 800, 600), 1));
        Assert.Equal(2, changed);
        Assert.Equal("A", screens.Primary.Name);
        Assert.Equal("B", screens.All[1].Name);

        screens.NotifyOnChange = false;
        screens.Remove(second); // 已不在集合中
        Assert.Equal(2, changed);
        screens.Reset();
        Assert.Equal(2, changed);
        Assert.Single(screens.All);
        Assert.Equal(1920, screens.Primary.Bounds.Width);

        screens.RaiseChanged();
        Assert.Equal(3, changed);

        // 窗口通过 WindowImpl.Screens 拿到同一份集合。
        var window = (HeadlessWindow)platform.CreateWindow(new WindowOptions());
        Assert.Same(screens, window.Screens);
    }

    [Fact]
    public async Task Storage_open_save_and_folder_queues_are_recorded()
    {
        var provider = new HeadlessStorageProvider(NewTempRoot());
        try
        {
            var a = provider.CreateFile("a.txt", "alpha");
            var b = provider.CreateFile("b.txt", "beta");

            provider.EnqueueOpenFiles(a, b);
            var options = new FilePickerOptions { Title = "Pick", AllowMultiple = true };
            var files = await provider.OpenFilesAsync(options);
            Assert.Equal(2, files.Count);
            Assert.Same(a, files[0]);
            Assert.Same(b, files[1]);
            Assert.Single(provider.OpenFilesRequests);
            Assert.Equal("Pick", provider.OpenFilesRequests[0].Title);
            Assert.True(provider.OpenFilesRequests[0].AllowMultiple);

            // 空队列表示用户取消；请求仍被记录。
            Assert.Empty(await provider.OpenFilesAsync(new FilePickerOptions { Title = "Again" }));
            Assert.Equal(2, provider.OpenFilesRequests.Count);
            Assert.Equal("Again", provider.OpenFilesRequests[1].Title);

            provider.EnqueueOpenFile(null);
            Assert.Empty(await provider.OpenFilesAsync(new FilePickerOptions()));
            provider.EnqueueOpenFile(a);
            Assert.Same(a, await provider.OpenFileAsync(new FilePickerOptions()));

            var saved = provider.CreateFile("saved.txt", "saved");
            provider.EnqueueSaveFile(saved);
            Assert.Same(saved, await provider.SaveFileAsync(new FilePickerOptions { SuggestedFileName = "saved.txt" }));
            Assert.Null(await provider.SaveFileAsync(new FilePickerOptions()));
            Assert.Equal(2, provider.SaveFileRequests.Count);
            Assert.Equal("saved.txt", provider.SaveFileRequests[0].SuggestedFileName);

            var folder = provider.CreateFolder("docs");
            provider.EnqueueOpenFolder(folder);
            Assert.Same(folder, await provider.OpenFolderAsync(new FilePickerOptions { Title = "Folder" }));
            Assert.Null(await provider.OpenFolderAsync(new FilePickerOptions()));
            Assert.Equal(2, provider.OpenFolderRequests.Count);
            Assert.Equal("Folder", provider.OpenFolderRequests[0].Title);

            provider.ClearQueue();
            Assert.Empty(await provider.OpenFilesAsync(new FilePickerOptions()));
        }
        finally
        {
            provider.CleanupTempDirectory();
        }
    }

    [Fact]
    public async Task Memory_files_round_trip_through_streams()
    {
        var provider = new HeadlessStorageProvider(NewTempRoot());
        try
        {
            var file = provider.CreateFile("note.txt", "hello");
            Assert.True(file.IsMemoryBacked);
            Assert.Equal("note.txt", file.Name);
            Assert.Equal("", file.Path);
            Assert.Null(file.TempPath);
            Assert.Equal(5, await file.GetSizeAsync());
            Assert.Equal("hello", Encoding.UTF8.GetString(file.ReadAllBytes()));

            using (var read = await file.OpenReadAsync())
            using (var reader = new StreamReader(read))
                Assert.Equal("hello", reader.ReadToEnd());

            using (var write = await file.OpenWriteAsync())
            {
                var bytes = Encoding.UTF8.GetBytes("bye");
                write.Write(bytes, 0, bytes.Length);
            }

            Assert.Equal("bye", Encoding.UTF8.GetString(file.ReadAllBytes()));
            Assert.Equal(3, await file.GetSizeAsync());

            file.WriteAllBytes(new byte[] { 1, 2, 3, 4 });
            Assert.Equal(4, await file.GetSizeAsync());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, file.ReadAllBytes());
        }
        finally
        {
            provider.CleanupTempDirectory();
        }
    }

    [Fact]
    public async Task Temp_files_and_folders_enumerate_deterministically()
    {
        var provider = new HeadlessStorageProvider(NewTempRoot());
        try
        {
            var file = provider.CreateTempFile("nested/data.bin", new byte[] { 1, 2, 3 });
            Assert.False(file.IsMemoryBacked);
            Assert.NotNull(file.TempPath);
            Assert.True(File.Exists(file.TempPath!));
            Assert.Equal("data.bin", file.Name);
            Assert.Equal(3, await file.GetSizeAsync());

            using (var write = await file.OpenWriteAsync()) write.WriteByte(9);
            Assert.Equal(new byte[] { 9 }, file.ReadAllBytes());
            Assert.Equal(1, await file.GetSizeAsync());

            var folder = provider.CreateTempFolder("nested");
            var files = await folder.GetFilesAsync();
            Assert.Single(files);
            Assert.Equal("data.bin", files[0].Name);
            Assert.Empty(await folder.GetFoldersAsync());

            provider.CreateTempFolder("nested/child");
            var folders = await folder.GetFoldersAsync();
            Assert.Single(folders);
            Assert.Equal("child", folders[0].Name);

            var fromPath = await provider.TryGetFileFromPathAsync(file.TempPath!);
            Assert.NotNull(fromPath);
            Assert.Equal(file.TempPath, fromPath!.Path);
            Assert.Equal(1, await fromPath.GetSizeAsync());

            var folderFromPath = await provider.TryGetFolderFromPathAsync(folder.TempPath!);
            Assert.NotNull(folderFromPath);
            Assert.Equal(folder.TempPath, folderFromPath!.Path);

            Assert.Null(await provider.TryGetFileFromPathAsync(Path.Combine(provider.TempRoot, "missing.txt")));
            Assert.Null(await provider.TryGetFolderFromPathAsync(Path.Combine(provider.TempRoot, "missing")));

            Assert.Throws<ArgumentException>(() => provider.CreateTempFile("../escape.txt"));
        }
        finally
        {
            provider.CleanupTempDirectory();
        }
    }

    [Fact]
    public async Task Launcher_records_calls_and_validates_uris()
    {
        var platform = new HeadlessPlatform();
        var launcher = platform.HeadlessLauncher;
        Assert.Same(launcher, platform.Launcher);
        Assert.Same(launcher, platform.GetService<Launcher>());

        var file = new HeadlessStorageFile("a.txt");
        var folder = new HeadlessStorageFolder("docs");

        Assert.True(await launcher.LaunchUriAsync(new Uri("https://example.com/page")));
        Assert.False(await launcher.LaunchUriAsync(new Uri("ftp://example.com/file")));
        Assert.True(await launcher.LaunchUriAsync(new Uri("mailto:someone@example.com")));

        launcher.Result = false;
        Assert.False(await launcher.LaunchFileAsync(file));
        Assert.False(await launcher.LaunchFolderAsync(folder));

        Assert.Equal(3, launcher.LaunchUriCalls.Count);
        Assert.Equal("ftp", launcher.LaunchUriCalls[1].Scheme);
        Assert.Single(launcher.LaunchFileCalls);
        Assert.Single(launcher.LaunchFolderCalls);
        Assert.Equal(5, launcher.LaunchCount);

        Assert.True(launcher.CanLaunchUri(new Uri("mailto:a@b.c")));
        Assert.False(launcher.CanLaunchUri(null));
        Assert.False(launcher.CanLaunchUri(new Uri("javascript:alert(1)", UriKind.Absolute)));

        launcher.ClearCalls();
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task Clipboard_round_trips_html_image_and_files()
    {
        var clipboard = new HeadlessClipboard();

        Assert.True(await clipboard.SetTextAsync("plain"));
        Assert.Equal("plain", await clipboard.GetTextAsync());

        Assert.True(await clipboard.SetHtmlAsync("<b>hi</b>"));
        Assert.Equal("<b>hi</b>", await clipboard.GetHtmlAsync());

        var source = new Bitmap(new PixelSize(2, 2));
        source.Clear(Color.FromRgb(10, 20, 30));
        Assert.True(await clipboard.SetImageAsync(source));
        Assert.Equal(new PixelSize(2, 2), clipboard.ImageSize);
        // 剪贴板保存像素副本：释放写入时传入的位图不影响之后读取。
        source.Dispose();

        using var image = await clipboard.GetImageAsync();
        Assert.NotNull(image);
        Assert.Equal(new PixelSize(2, 2), image!.Size);
        Assert.NotSame(clipboard.Image, image);
        var rgba = image.ToRgbaUnpremultiplied();
        Assert.Equal(10, rgba[0]);
        Assert.Equal(20, rgba[1]);
        Assert.Equal(30, rgba[2]);
        Assert.Equal(255, rgba[3]);

        var a = new HeadlessStorageFile("a.txt", "a");
        var b = new HeadlessStorageFile("b.txt", "b");
        Assert.True(await clipboard.SetFilesAsync(new StorageFile[] { a, b }));
        var files = await clipboard.GetFilesAsync();
        Assert.Equal(2, files.Count);
        Assert.Same(a, files[0]);
        Assert.Same(b, files[1]);

        Assert.True(await clipboard.ClearAsync());
        Assert.Null(await clipboard.GetTextAsync());
        Assert.Null(await clipboard.GetHtmlAsync());
        Assert.Null(await clipboard.GetImageAsync());
        Assert.Empty(await clipboard.GetFilesAsync());
    }

    [Fact]
    public async Task Clipboard_deny_access_applies_to_every_format()
    {
        var clipboard = new HeadlessClipboard { DenyAccess = true };
        var bitmap = new Bitmap(new PixelSize(1, 1));
        try
        {
            Assert.False(await clipboard.SetTextAsync("x"));
            Assert.False(await clipboard.SetHtmlAsync("<b>x</b>"));
            Assert.False(await clipboard.SetImageAsync(bitmap));
            Assert.False(await clipboard.SetFilesAsync(new StorageFile[] { new HeadlessStorageFile("a.txt") }));
            Assert.False(await clipboard.ClearAsync());

            Assert.Null(await clipboard.GetTextAsync());
            Assert.Null(await clipboard.GetHtmlAsync());
            Assert.Null(await clipboard.GetImageAsync());
            Assert.Empty(await clipboard.GetFilesAsync());
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    [Fact]
    public async Task Clipboard_keeps_the_16m_text_limit_for_text_and_html()
    {
        var clipboard = new HeadlessClipboard();
        var over = new string('x', Clipboard.MaxTextLength + 3);

        Assert.True(await clipboard.SetTextAsync(over));
        Assert.Equal(Clipboard.MaxTextLength, clipboard.Text!.Length);

        Assert.True(await clipboard.SetHtmlAsync(over));
        Assert.Equal(Clipboard.MaxTextLength, clipboard.Html!.Length);
    }

    [Fact]
    public void Platform_services_are_available_on_windows_and_getservice()
    {
        var platform = new HeadlessPlatform();
        var window = (HeadlessWindow)platform.CreateWindow(new WindowOptions());

        Assert.Same(platform.Clipboard, window.Clipboard);
        Assert.Same(platform.Screens, window.Screens);
        Assert.Same(platform.StorageProvider, window.StorageProvider);
        Assert.Same(platform.Launcher, window.Launcher);

        Assert.Same(platform.Clipboard, platform.GetService<Clipboard>());
        Assert.Same(platform.Screens, platform.GetService<Screens>());
        Assert.Same(platform.StorageProvider, platform.GetService<StorageProvider>());
        Assert.Same(platform.Launcher, platform.GetService<Launcher>());
        Assert.Null(platform.GetService<IDisposable>());
    }
}
