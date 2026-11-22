using System.IO;
using AwAUI.Platform;
using AwAUI.Win32;

namespace AwAUI.Platform.UnitTests;

/// <summary>comdlg32 过滤串与多选缓冲的解析（纯逻辑）。</summary>
public class Win32DialogsTests
{
    [Fact]
    public void BuildFilter_pairs_names_and_patterns_and_ends_with_double_nul()
    {
        var images = new FilePickerFileType("Images");
        images.Patterns.Add("*.png");
        images.Patterns.Add("*.jpg");
        var all = new FilePickerFileType("All");
        all.Patterns.Add("*.*");

        string filter = Win32Dialogs.BuildFilter(new[] { images, all });
        Assert.Equal("Images\0*.png;*.jpg\0All\0*.*\0\0", filter);
    }

    [Fact]
    public void BuildFilter_defaults_when_empty_or_patternless()
    {
        Assert.Equal("All files\0*.*\0\0", Win32Dialogs.BuildFilter(Array.Empty<FilePickerFileType>()));

        var empty = new FilePickerFileType("");
        Assert.Equal("Files\0*.*\0\0", Win32Dialogs.BuildFilter(new[] { empty }));
    }

    [Fact]
    public void BuildFilter_strips_control_characters()
    {
        var type = new FilePickerFileType("Pi\0ct\nures");
        type.Patterns.Add("*.p\0ng");
        string filter = Win32Dialogs.BuildFilter(new[] { type });
        Assert.Equal("Pictures\0*.png\0\0", filter);
    }

    [Fact]
    public void ParseOpenPaths_reads_a_single_file_even_in_multi_select_mode()
    {
        string file = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "only.txt"));
        char[] buffer = SinglePathBuffer(file);
        var paths = Win32Dialogs.ParseOpenPaths(buffer, multiple: true);
        Assert.Single(paths);
        Assert.Equal(file, paths[0]);
    }

    [Fact]
    public void ParseOpenPaths_expands_a_multi_select_directory_buffer()
    {
        string dir = Path.GetFullPath(Path.GetTempPath());
        char[] buffer = MultiBuffer(dir, "a.txt", "b.txt");
        var paths = Win32Dialogs.ParseOpenPaths(buffer, multiple: true);
        Assert.Equal(2, paths.Count);
        Assert.Equal(Path.Combine(dir, "a.txt"), paths[0]);
        Assert.Equal(Path.Combine(dir, "b.txt"), paths[1]);
    }

    [Fact]
    public void ParseOpenPaths_stops_at_double_nul_and_ignores_trailing_garbage()
    {
        string dir = Path.GetFullPath(Path.GetTempPath());
        char[] buffer = MultiBuffer(dir, "a.txt");
        // 双 NUL 之后的内容不属于文件列表
        buffer[buffer.Length - 1] = 'x';
        var paths = Win32Dialogs.ParseOpenPaths(buffer, multiple: true);
        Assert.Single(paths);
    }

    [Fact]
    public void ParseOpenPaths_rejects_empty_and_relative_inputs()
    {
        Assert.Empty(Win32Dialogs.ParseOpenPaths(new char[16], multiple: false));
        Assert.Empty(Win32Dialogs.ParseOpenPaths(new char[] { '\0', 'a', '\0', '\0' }, multiple: false));
        var relative = "relative.txt\0\0".ToCharArray();
        Assert.Empty(Win32Dialogs.ParseOpenPaths(relative, multiple: false));
        Assert.Empty(Win32Dialogs.ParseOpenPaths(null!, multiple: true));
    }

    static char[] SinglePathBuffer(string path)
    {
        var buffer = new char[path.Length + 2];
        path.CopyTo(0, buffer, 0, path.Length);
        return buffer;
    }

    static char[] MultiBuffer(string directory, params string[] names)
    {
        int length = directory.Length + 1;
        foreach (string name in names) length += name.Length + 1;
        length += 1; // 双 NUL
        var buffer = new char[length];
        int at = 0;
        directory.CopyTo(0, buffer, at, directory.Length);
        at += directory.Length + 1;
        foreach (string name in names)
        {
            name.CopyTo(0, buffer, at, name.Length);
            at += name.Length + 1;
        }
        return buffer;
    }
}
