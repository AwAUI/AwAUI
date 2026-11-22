using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.X11;

namespace AwAUI.Platform.UnitTests;

/// <summary>Wave-12 X11 后端的纯逻辑：首次位置、工作区、启动器校验、对话框参数、剪贴板富格式与 PNG 编解码。</summary>
public class X11WindowGeometryTests
{
    [Fact]
    public void Manual_position_is_used_as_is()
    {
        var options = new WindowOptions { Size = new Size(200, 100), Position = new Point(10, 20) };
        Assert.Equal(new Point(10, 20), X11WindowGeometry.ResolveStartupPosition(options, null));
    }

    [Fact]
    public void Center_screen_uses_the_primary_work_area()
    {
        var screen = new Screen("s", new Rect(0, 0, 1000, 800), new Rect(100, 50, 800, 600), 1, isPrimary: true);
        var options = new WindowOptions { Size = new Size(200, 100), StartupLocation = WindowStartupLocation.CenterScreen };
        Assert.Equal(new Point(400, 300), X11WindowGeometry.ResolveStartupPosition(options, screen));

        // 窗口比工作区大：贴住工作区左上角
        options = new WindowOptions { Size = new Size(2000, 2000), StartupLocation = WindowStartupLocation.CenterOwner };
        Assert.Equal(new Point(100, 50), X11WindowGeometry.ResolveStartupPosition(options, screen));
    }

    [Fact]
    public void Work_area_property_parsing_is_bounds_checked()
    {
        var values = new long[] { 0, 0, 1920, 1080, 10, 20, 800, 600 };
        Assert.Equal(new Rect(10, 20, 800, 600), X11WindowGeometry.ParseWorkArea(values, 1));
        Assert.Equal(Rect.Empty, X11WindowGeometry.ParseWorkArea(values, 2));
        Assert.Equal(Rect.Empty, X11WindowGeometry.ParseWorkArea(values, -1));
        Assert.Equal(Rect.Empty, X11WindowGeometry.ParseWorkArea(new long[] { 0, 0, 0, 100 }, 0));
        Assert.Equal(Rect.Empty, X11WindowGeometry.ParseWorkArea(Array.Empty<long>(), 0));
    }

    [Fact]
    public void Work_area_is_clamped_to_the_screen_bounds()
    {
        var bounds = new Rect(0, 0, 1000, 800);
        Assert.Equal(new Rect(100, 50, 800, 600), X11WindowGeometry.ClampWorkArea(bounds, new Rect(100, 50, 800, 600)));
        Assert.Equal(bounds, X11WindowGeometry.ClampWorkArea(bounds, Rect.Empty));
        // 完全在屏幕外的工作区退回完整屏幕
        Assert.Equal(bounds, X11WindowGeometry.ClampWorkArea(bounds, new Rect(2000, 2000, 100, 100)));
    }

    [Fact]
    public void Logical_pixels_convert_to_device_pixels()
    {
        Assert.Equal(21, X11WindowGeometry.ToPixelCoordinate(10.5f, 2));
        Assert.Equal(1, X11WindowGeometry.ToPixelExtent(0.1, 1));
        Assert.Equal(5, X11WindowGeometry.ToPixelExtent(2.5, 2));
        Assert.Equal(1, X11WindowGeometry.ToPixelExtent(-10, 1));
    }
}

public class X11LaunchRulesTests
{
    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:user@example.com", true)]
    [InlineData("tel:+123456", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/plain,hi", false)]
    [InlineData("file://remotehost/tmp/x", false)]
    [InlineData("file:///tmp/x", true)]
    public void Uri_schemes_are_restricted(string text, bool expected) =>
        Assert.Equal(expected, X11LaunchRules.CanLaunch(new Uri(text)));

    [Fact]
    public void Unsafe_paths_are_rejected()
    {
        Assert.True(X11LaunchRules.IsSafePath("/tmp/a b.txt"));
        Assert.False(X11LaunchRules.IsSafePath("relative/path"));
        Assert.False(X11LaunchRules.IsSafePath("/tmp/../etc/passwd"));
        Assert.False(X11LaunchRules.IsSafePath("/tmp/a\0b"));
        Assert.False(X11LaunchRules.IsSafePath(""));
        Assert.False(X11LaunchRules.IsSafePath(null));
    }

    [Fact]
    public void Posix_quoting_handles_spaces_and_quotes()
    {
        Assert.Equal("''", X11LaunchRules.QuotePosix(""));
        Assert.Equal("'a b'", X11LaunchRules.QuotePosix("a b"));
        Assert.Equal("'a'\\''b'", X11LaunchRules.QuotePosix("a'b"));
    }
}

public class X11FileDialogArgumentsTests
{
    [Fact]
    public void Zenity_open_arguments_include_multiple_title_and_filters()
    {
        var options = new FilePickerOptions { Title = "Open file", AllowMultiple = true };
        var filter = new FilePickerFileType("Images");
        filter.Patterns.Add("*.png");
        filter.Patterns.Add("*.jpg");
        options.FileTypes.Add(filter);

        var command = X11FileDialogArguments.Build("zenity", X11FileDialogKind.Open, options);
        Assert.Equal("zenity", command.FileName);
        Assert.Equal("--file-selection", command.Arguments[0]);
        Assert.Contains("--multiple", command.Arguments);
        Assert.Contains("--title=Open file", command.Arguments);
        Assert.Contains("--file-filter=Images | *.png *.jpg", command.Arguments);
    }

    [Fact]
    public void Kdialog_save_arguments_use_the_suggested_name()
    {
        var options = new FilePickerOptions { SuggestedFileName = "out.txt" };
        var filter = new FilePickerFileType("Text");
        filter.Patterns.Add("*.txt");
        options.FileTypes.Add(filter);

        var command = X11FileDialogArguments.Build("kdialog", X11FileDialogKind.Save, options);
        Assert.Equal("--getsavefilename", command.Arguments[0]);
        Assert.Equal("./out.txt", command.Arguments[1]);
        Assert.Contains("Text (*.txt)", command.Arguments);
    }

    [Fact]
    public void Hostile_filters_are_sanitized()
    {
        Assert.Equal("", X11FileDialogArguments.SanitizePattern("*.png|rm -rf /"));
        Assert.Equal("", X11FileDialogArguments.SanitizePattern("*.p ng"));
        Assert.Equal("Im ages x", X11FileDialogArguments.SanitizeName("Im|ages (x)"));
        // 模式不含分隔符时原样保留
        Assert.Equal("*.png", X11FileDialogArguments.SanitizePattern(" *.png "));
    }

    [Fact]
    public void Output_parsing_accepts_newlines_and_pipes()
    {
        Assert.Equal(new[] { "/a", "/b", "/c" },
            X11FileDialogArguments.ParseOutput("/a\n/b|/c\r\n", allowMultiple: true));
        Assert.Equal(new[] { "/a" }, X11FileDialogArguments.ParseOutput("/a\n/b", allowMultiple: false));
        Assert.Empty(X11FileDialogArguments.ParseOutput(null, true));
    }

    [Fact]
    public void Capped_reader_drains_the_rest_of_the_stream()
    {
        var reader = new StringReader(new string('x', 10000));
        string text = X11FileDialogArguments.ReadCapped(reader, 100);
        Assert.Equal(100, text.Length);
        Assert.Equal(-1, reader.Peek()); // 已经读到末尾，不会让子进程因管道写满而阻塞
    }
}

public class X11ClipboardFormatsTests
{
    [Fact]
    public void Uri_list_round_trips_local_paths()
    {
        var paths = new List<string> { "/tmp/a b.txt", "/tmp/plain" };
        string uriList = X11ClipboardFormats.BuildUriList(paths)!;
        Assert.Contains("file:///tmp/a%20b.txt", uriList);
        Assert.Equal(paths, X11ClipboardFormats.ParseUriList(uriList));
    }

    [Fact]
    public void Uri_list_ignores_remote_comments_and_unsafe_entries()
    {
        const string text = "# comment\r\nfile:///tmp/ok\r\nfile://remotehost/tmp/no\r\nhttp://example.com/x\r\n/tmp/relative\r\nfile:///tmp/../etc/passwd\r\n";
        Assert.Equal(new[] { "/tmp/ok" }, X11ClipboardFormats.ParseUriList(text));
        Assert.Equal(new[] { "/tmp/ok" }, X11ClipboardFormats.ParseUriList("file:///tmp/ok\r\nfile:///tmp/ok\r\n"));
    }

    [Fact]
    public void Html_fallback_strips_tags_and_decodes_entities()
    {
        Assert.Equal("Hello & world", X11ClipboardFormats.HtmlToPlainText("<p>Hello &amp; <b>world</b></p>"));
        Assert.Equal("", X11ClipboardFormats.HtmlToPlainText(null));
    }

    [Fact]
    public void Text_file_list_skips_invalid_paths()
    {
        string text = X11ClipboardFormats.BuildTextFileList(new[] { "/tmp/a", "relative" })!;
        Assert.Equal("/tmp/a\n", text);
        Assert.Null(X11ClipboardFormats.BuildUriList(new[] { "relative" }));
    }
}

public class X11PngTests
{
    [Fact]
    public void Encode_and_decode_round_trip_opaque_pixels()
    {
        using var bitmap = new Bitmap(new PixelSize(2, 2));
        var row0 = bitmap.GetRow(0);
        row0[0] = 255; row0[1] = 0; row0[2] = 0; row0[3] = 255;
        row0[4] = 0; row0[5] = 255; row0[6] = 0; row0[7] = 255;
        var row1 = bitmap.GetRow(1);
        row1[0] = 0; row1[1] = 0; row1[2] = 255; row1[3] = 255;
        row1[4] = 255; row1[5] = 255; row1[6] = 255; row1[7] = 255;
        bitmap.Invalidate();

        byte[] png = X11Png.Encode(bitmap);
        Assert.True(X11Png.CanDecode(png));
        using var decoded = X11Png.Decode(png);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, decoded.GetRow(0).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }, decoded.GetRow(1).ToArray());
    }

    [Fact]
    public void Corrupt_or_foreign_data_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => X11Png.Decode(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Throws<InvalidDataException>(() => X11Png.Decode(Encoding.ASCII.GetBytes("not a png")));
        Assert.False(X11Png.CanDecode(new byte[] { 1, 2, 3 }));
    }
}

/// <summary>富格式的选择协议：TARGETS 列出可用格式，各目标返回自己的字节，读取方支持任意目标与 INCR。</summary>
public class X11RichSelectionTests
{
    [Fact]
    public void Server_advertises_and_serves_rich_targets()
    {
        var x = new FakeXServer();
        var a = x.Connect(1);
        var png = new byte[] { 1, 2, 3, 4, 5 };
        a.Server.Own(new X11ClipboardData
        {
            Text = "plain",
            Html = "<b>hi</b>",
            Png = png,
            UriList = "file:///tmp/a\r\n",
        }, 1);

        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Targets, 50, 1, 0);
        var targets = ParsedTargets(x.Properties[(7, 50)].Data);
        Assert.Contains(FakeXServer.Atoms.UriList, targets);
        Assert.Contains(FakeXServer.Atoms.ImagePng, targets);
        Assert.Contains(FakeXServer.Atoms.Html, targets);

        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Html, 51, 1, 0);
        Assert.Equal("<b>hi</b>", Encoding.UTF8.GetString(x.Properties[(7, 51)].Data));
        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.ImagePng, 52, 1, 0);
        Assert.Equal(png, x.Properties[(7, 52)].Data);
        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.UriList, 53, 1, 0);
        Assert.Equal("file:///tmp/a\r\n", Encoding.UTF8.GetString(x.Properties[(7, 53)].Data));
    }

    [Fact]
    public void Reader_fetches_bytes_for_any_target()
    {
        var x = new FakeXServer();
        var owner = x.Connect(1);
        var reader = x.Connect(2);
        owner.Server.Own(new X11ClipboardData { UriList = "file:///tmp/a\r\nfile:///tmp/b\r\n" }, 1);

        byte[]? result = null;
        reader.Reader.RequestBytes(FakeXServer.Atoms.UriList, 1 << 20, x.Now, r => result = r);
        x.Pump();
        Assert.Equal("file:///tmp/a\r\nfile:///tmp/b\r\n", Encoding.UTF8.GetString(result!));
    }

    [Fact]
    public void Binary_targets_use_incr_when_large()
    {
        var x = new FakeXServer { MaxChunk = 4 };
        var owner = x.Connect(1);
        var reader = x.Connect(2);
        var png = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        owner.Server.Own(new X11ClipboardData { Png = png }, 1);

        byte[]? result = null;
        reader.Reader.RequestBytes(FakeXServer.Atoms.ImagePng, 1 << 20, x.Now, r => result = r);
        x.Pump();
        Assert.Equal(png, result);
        Assert.Equal(0, owner.Server.ActiveTransfers);
    }

    static ulong[] ParsedTargets(byte[] data)
    {
        var values = new ulong[data.Length / 8];
        for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToUInt64(data, i * 8);
        return values;
    }
}
