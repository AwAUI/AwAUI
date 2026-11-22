using System.Text;
using AwAUI.Win32;

namespace AwAUI.Platform.UnitTests;

/// <summary>CF_HTML 头部的编解码（纯逻辑；可在任何系统上验证）。</summary>
public class Win32ClipboardHtmlTests
{
    [Theory]
    [InlineData("<b>hello</b>")]
    [InlineData("中文 <i>italic</i>")]
    [InlineData("emoji 😀 and é")]
    [InlineData("plain")]
    public void Encode_decode_round_trips_fragment(string fragment)
    {
        byte[] data = Win32ClipboardHtml.Encode(fragment);
        Assert.Equal(fragment, Win32ClipboardHtml.Decode(data));
    }

    [Fact]
    public void Header_offsets_are_byte_offsets_and_end_html_is_total_length()
    {
        byte[] data = Win32ClipboardHtml.Encode("é😀");
        string header = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 256));
        int startHtml = Parse(header, "StartHTML:");
        int endHtml = Parse(header, "EndHTML:");
        int startFragment = Parse(header, "StartFragment:");
        int endFragment = Parse(header, "EndFragment:");

        Assert.True(startHtml > 0); // StartHTML 是文档正文的起始字节（头部之后）
        Assert.Equal(data.Length, endHtml);
        Assert.True(startFragment > startHtml && startFragment < endFragment);
        Assert.True(endFragment <= endHtml);
        Assert.Equal("é😀", Encoding.UTF8.GetString(data, startFragment, endFragment - startFragment));
    }

    [Fact]
    public void Decode_falls_back_to_fragment_markers_when_offsets_are_invalid()
    {
        // 没有偏移头，只有片段标记（某些程序写入的简化格式）
        byte[] data = Encoding.UTF8.GetBytes("garbage<!--StartFragment--><p>ok</p><!--EndFragment-->tail");
        Assert.Equal("<p>ok</p>", Win32ClipboardHtml.Decode(data));
    }

    [Fact]
    public void Decode_returns_null_without_markers_or_offsets()
    {
        byte[] data = Encoding.UTF8.GetBytes("just some text with no html");
        Assert.Null(Win32ClipboardHtml.Decode(data));
        Assert.Null(Win32ClipboardHtml.Decode(Array.Empty<byte>()));
    }

    [Fact]
    public void Decode_ignores_offsets_beyond_the_buffer()
    {
        string html = "<span>x</span>";
        byte[] data = Win32ClipboardHtml.Encode(html);
        // 伪造一个超出缓冲区的 StartFragment，解码应回退到标记
        string ascii = Encoding.ASCII.GetString(data);
        int at = ascii.IndexOf("StartFragment:", StringComparison.Ordinal) + "StartFragment:".Length;
        for (int i = 0; i < 10; i++) data[at + i] = (byte)'9';
        Assert.Equal(html, Win32ClipboardHtml.Decode(data));
    }

    [Fact]
    public void Plain_text_strips_tags_scripts_and_decodes_entities()
    {
        const string html = "<div>Hello <b>world</b></div><script>alert(1)</script><p>a &amp; b&nbsp;&lt;c&gt;</p>";
        string text = Win32ClipboardHtml.ToPlainText(html);
        Assert.DoesNotContain("<b>", text);
        Assert.DoesNotContain("<div>", text);
        Assert.DoesNotContain("alert", text);
        Assert.Contains("Hello world", text);
        Assert.Contains("a & b <c>", text);
    }

    static int Parse(string header, string field)
    {
        int at = header.IndexOf(field, StringComparison.Ordinal);
        Assert.True(at >= 0, field);
        int i = at + field.Length;
        int value = 0;
        while (i < header.Length && header[i] >= '0' && header[i] <= '9')
        {
            value = value * 10 + (header[i] - '0');
            i++;
        }
        return value;
    }
}
