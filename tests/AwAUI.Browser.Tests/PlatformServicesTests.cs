using System;
using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

/// <summary>浏览器服务（Launcher / 文件选择器元数据）的纯 C# 部分。JS 互操作本身由 node/ 下的端到端测试覆盖。</summary>
public class BrowserUriPolicyTests
{
    [Theory]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://localhost:5000/")]
    [InlineData("mailto:user@example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void AllowsHttpHttpsAndMailto(string value) =>
        Assert.True(BrowserUriPolicy.IsAllowed(new Uri(value)));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("blob:https://example.com/id")]
    public void RejectsOtherSchemes(string value) =>
        Assert.False(BrowserUriPolicy.IsAllowed(new Uri(value)));

    [Fact]
    public void RejectsRelativeAndNull()
    {
        Assert.False(BrowserUriPolicy.IsAllowed(new Uri("foo/bar", UriKind.Relative)));
        Assert.False(BrowserUriPolicy.IsAllowed(null));
    }
}

/// <summary><see cref="BrowserStorageCodec"/>：JS 端的文件元数据编码与 Pick 类型过滤。</summary>
public class BrowserStorageCodecTests
{
    [Fact]
    public void ParsesEntriesAndEscapesSeparatorsInNames()
    {
        // 名称里带空格、斜杠、换行与字段分隔符；encodeURIComponent / UnescapeDataString 应还原
        string name = "a b/c\r\n\u001f.txt";
        string type = "image/png";
        string payload =
            "7\u001f" + Uri.EscapeDataString(name) + "\u001f1234\u001f" + Uri.EscapeDataString(type) + "\u001f0\n" +
            "8\u001fplain.txt\u001f-1\u001f\u001f1\n";

        var entries = BrowserStorageCodec.Parse(payload);

        Assert.Equal(2, entries.Count);
        Assert.Equal(7, entries[0].Handle);
        Assert.Equal(name, entries[0].Name);
        Assert.Equal(1234, entries[0].Size);
        Assert.Equal(type, entries[0].Type);
        Assert.False(entries[0].Writable);

        Assert.Equal(8, entries[1].Handle);
        Assert.Equal("plain.txt", entries[1].Name);
        Assert.Equal(-1, entries[1].Size);
        Assert.Equal(string.Empty, entries[1].Type);
        Assert.True(entries[1].Writable);
    }

    [Fact]
    public void SkipsMalformedLinesAndInvalidHandles()
    {
        string payload =
            "not-a-number\u001fa\u001f1\u001ftext/plain\u001f0\n" +
            "0\u001fzero\u001f1\u001ftext/plain\u001f0\n" +
            "3\u001fmissing-fields\n" +
            "\n" +
            "5\u001fok\u001f2\u001ftext/plain\u001f0";

        var entries = BrowserStorageCodec.Parse(payload);

        var entry = Assert.Single(entries);
        Assert.Equal(5, entry.Handle);
        Assert.Equal("ok", entry.Name);
    }

    [Fact]
    public void EmptyPayloadMeansCancelled()
    {
        Assert.Empty(BrowserStorageCodec.Parse(null));
        Assert.Empty(BrowserStorageCodec.Parse(string.Empty));
    }

    [Fact]
    public void BuildAcceptCombinesMimeTypesAndPatternsWithoutDuplicates()
    {
        var options = new FilePickerOptions();
        var images = new FilePickerFileType("Images");
        images.MimeTypes.Add("image/png");
        images.Patterns.Add("*.png");
        var text = new FilePickerFileType("Text");
        text.MimeTypes.Add("text/plain");
        text.MimeTypes.Add("image/png"); // 重复
        options.FileTypes.Add(images);
        options.FileTypes.Add(text);

        Assert.Equal("image/png,*.png,text/plain", BrowserStorageCodec.BuildAccept(options));
    }

    [Fact]
    public void BuildAcceptNullWhenNoTypes()
    {
        Assert.Null(BrowserStorageCodec.BuildAccept(null));
        Assert.Null(BrowserStorageCodec.BuildAccept(new FilePickerOptions()));
    }
}
