using System.IO;
using AwAUI.Win32;

namespace AwAUI.Platform.UnitTests;

/// <summary>启动器 / 文件选择器的输入校验（纯逻辑）。</summary>
public class Win32PathTests
{
    [Fact]
    public void Accepts_rooted_paths_and_rejects_relative_or_bad_ones()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "awaui"));
        Assert.True(Win32Path.IsSafeAbsolutePath(root));
        Assert.True(Win32Path.IsSafeAbsolutePath(Path.Combine(root, "file.txt")));

        Assert.False(Win32Path.IsSafeAbsolutePath("relative/file.txt"));
        Assert.False(Win32Path.IsSafeAbsolutePath(null));
        Assert.False(Win32Path.IsSafeAbsolutePath(""));
        Assert.False(Win32Path.IsSafeAbsolutePath(root + "\0evil"));
        Assert.False(Win32Path.IsSafeAbsolutePath(root + "\nevil"));
        Assert.False(Win32Path.IsSafeAbsolutePath(new string('a', Win32Path.MaxPath + 1)));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:user@example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com", false)]
    public void Launch_uri_policy_allows_only_web_and_mail(string text, bool expected)
    {
        Assert.Equal(expected, Win32Path.IsSafeLaunchUri(new Uri(text)));
    }

    [Fact]
    public void Launch_uri_policy_rejects_relative_and_null()
    {
        Assert.False(Win32Path.IsSafeLaunchUri(null));
        Assert.False(Win32Path.IsSafeLaunchUri(new Uri("relative/path", UriKind.Relative)));
    }

    [Fact]
    public void Combine_requires_both_parts_and_returns_absolute_path()
    {
        string dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "awaui"));
        string? combined = Win32Path.Combine(dir, "a.txt");
        Assert.NotNull(combined);
        Assert.Equal(Path.Combine(dir, "a.txt"), combined);

        Assert.Null(Win32Path.Combine(null, "a.txt"));
        Assert.Null(Win32Path.Combine(dir, null));
        Assert.Null(Win32Path.Combine("", "a.txt"));
        Assert.Null(Win32Path.Combine(dir, "bad\0name"));
    }
}
