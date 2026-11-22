using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// 富剪贴板（design/04）：在 CF_UNICODETEXT 之外支持 CF_HTML（"HTML Format"）、CF_DIB 图片往返与 CF_HDROP 文件列表。
/// 读取侧把剪贴板内容视为不可信——所有尺寸以 GlobalSize 与实际字节数为界，文本/HTML 不超过
/// <see cref="Clipboard.MaxTextLength"/>，图片读取不超过 64MB 且解析时再次校验行距与偏移。
/// 写入侧在同一个打开会话中先 EmptyClipboard，再写入各格式；失败时返回 false，不抛异常。
/// </summary>
public sealed unsafe partial class Win32Clipboard
{
    /// <summary>图片数据读取上限（字节）；更大的图片返回 null，避免无界分配。</summary>
    const long MaxImageBytes = 64L * 1024 * 1024;

    // ---------------------------------------------------------------- HTML

    public override Task<string?> GetHtmlAsync() => OnUiThread(ReadHtml);

    public override Task<bool> SetHtmlAsync(string html)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));
        return OnUiThread(() => WriteHtml(html));
    }

    static uint s_htmlFormat;

    static uint HtmlFormat()
    {
        if (s_htmlFormat != 0) return s_htmlFormat;
        try
        {
            fixed (char* name = Win32ClipboardHtml.FormatName)
                s_htmlFormat = User32.RegisterClipboardFormatW(name);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_htmlFormat = 0;
        }
        return s_htmlFormat;
    }

    string? ReadHtml()
    {
        uint format = HtmlFormat();
        if (format == 0 || User32.IsClipboardFormatAvailable(format) == 0 || !Open()) return null;
        try
        {
            var handle = User32.GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            var p = Kernel32.GlobalLock(handle);
            if (p == null) return null;
            try
            {
                long size = (long)(ulong)Kernel32.GlobalSize(handle);
                if (size <= 0) return null;
                int length = (int)Math.Min(size, Win32ClipboardHtml.MaxBytes);
                var bytes = new byte[length];
                Marshal.Copy((IntPtr)p, bytes, 0, length);
                return Win32ClipboardHtml.Decode(bytes);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    bool WriteHtml(string html)
    {
        uint format = HtmlFormat();
        if (format == 0) return false;
        var htmlData = Win32ClipboardHtml.Encode(html);
        string plain = Win32ClipboardText.ToCrLf(Clipboard.LimitLength(Win32ClipboardHtml.ToPlainText(html)));
        var textData = EncodeUtf16Z(plain);

        if (!Open()) return false;
        try
        {
            if (User32.EmptyClipboard() == 0) return false;
            if (!SetBytes(format, htmlData)) return false;
            return SetBytes(CF_UNICODETEXT, textData);
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    static byte[] EncodeUtf16Z(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    // ---------------------------------------------------------------- 图片（CF_DIB）

    public override Task<Bitmap?> GetImageAsync() => OnUiThread(ReadImage);

    public override Task<bool> SetImageAsync(Bitmap image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        return OnUiThread(() => WriteImage(image));
    }

    Bitmap? ReadImage()
    {
        uint format = User32.IsClipboardFormatAvailable(CF_DIB) != 0 ? CF_DIB
            : User32.IsClipboardFormatAvailable(CF_DIBV5) != 0 ? CF_DIBV5
            : 0;
        if (format == 0 || !Open()) return null;
        try
        {
            var handle = User32.GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            var p = Kernel32.GlobalLock(handle);
            if (p == null) return null;
            try
            {
                long size = (long)(ulong)Kernel32.GlobalSize(handle);
                if (size <= 0 || size < Win32Dib.HeaderSize) return null;
                int length = (int)Math.Min(size, MaxImageBytes);
                var bytes = new byte[length];
                Marshal.Copy((IntPtr)p, bytes, 0, length);
                if (!Win32Dib.TryParse(bytes, out int width, out int height, out byte[] rgba)) return null;
                return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(width, height));
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    bool WriteImage(Bitmap image)
    {
        if (image.IsDisposed) return false;
        var dib = Win32Dib.Build(image.Width, image.Height, image.ToRgbaUnpremultiplied());
        if (!Open()) return false;
        try
        {
            if (User32.EmptyClipboard() == 0) return false;
            return SetBytes(CF_DIB, dib);
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    // ---------------------------------------------------------------- 文件（CF_HDROP）

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync() => OnUiThread(ReadFiles);

    public override Task<bool> SetFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));
        return OnUiThread(() => WriteFiles(files));
    }

    IReadOnlyList<StorageFile> ReadFiles()
    {
        if (User32.IsClipboardFormatAvailable(CF_HDROP) == 0 || !Open()) return Array.Empty<StorageFile>();
        try
        {
            var handle = User32.GetClipboardData(CF_HDROP);
            if (handle == IntPtr.Zero) return Array.Empty<StorageFile>();
            var p = Kernel32.GlobalLock(handle);
            if (p == null) return Array.Empty<StorageFile>();
            try
            {
                long total = (long)(ulong)Kernel32.GlobalSize(handle);
                if (total < sizeof(DROPFILES)) return Array.Empty<StorageFile>();
                var drop = (DROPFILES*)p;
                uint offset = drop->pFiles;
                if (offset < sizeof(DROPFILES) || offset >= total) return Array.Empty<StorageFile>();

                var basePtr = (byte*)p + offset;
                long remaining = total - offset;
                return drop->fWide != 0 ? ReadWidePaths(basePtr, remaining) : ReadAnsiPaths(basePtr, remaining);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    static IReadOnlyList<StorageFile> ReadWidePaths(byte* data, long remainingBytes)
    {
        var files = new List<StorageFile>();
        long chars = Math.Min(remainingBytes / 2, Clipboard.MaxTextLength);
        char* s = (char*)data;
        int at = 0;
        while (at < chars && files.Count < 4096)
        {
            int end = at;
            while (end < chars && s[end] != '\0') end++;
            if (end == at) break; // 双 NUL 结束
            int length = end - at;
            if (length <= Win32Path.MaxPath)
            {
                string path = new string(s, at, length);
                if (Win32Path.IsSafeAbsolutePath(path)) files.Add(new Win32StorageFile(path));
            }
            at = end + 1;
        }
        return files;
    }

    static IReadOnlyList<StorageFile> ReadAnsiPaths(byte* data, long remainingBytes)
    {
        // 旧式 ANSI drop 列表很少见；没有代码页提供程序时按 UTF-8 宽容解码（无效字节替换为 U+FFFD）。
        var files = new List<StorageFile>();
        int bytes = (int)Math.Min(remainingBytes, Clipboard.MaxTextLength);
        int at = 0;
        while (at < bytes && files.Count < 4096)
        {
            int end = at;
            while (end < bytes && data[end] != 0) end++;
            if (end == at) break;
            int length = end - at;
            if (length <= Win32Path.MaxPath)
            {
                var buffer = new byte[length];
                Marshal.Copy((IntPtr)(data + at), buffer, 0, length);
                string path = Encoding.UTF8.GetString(buffer);
                if (Win32Path.IsSafeAbsolutePath(path)) files.Add(new Win32StorageFile(path));
            }
            at = end + 1;
        }
        return files;
    }

    bool WriteFiles(IReadOnlyList<StorageFile> files)
    {
        var paths = new List<string>(files.Count);
        long chars = 1; // 末尾的第二个 NUL
        foreach (var file in files)
        {
            if (file is null) continue;
            string path = file.Path;
            if (!Win32Path.IsSafeAbsolutePath(path)) continue;
            if (paths.Count >= 4096) break;
            chars += path.Length + 1;
            if (chars > Clipboard.MaxTextLength) return false;
            paths.Add(path);
        }
        if (paths.Count == 0) return false;

        string list = string.Join("\0", paths) + "\0\0";
        int header = sizeof(DROPFILES);
        int total = header + list.Length * 2;
        var data = new byte[total];
        WriteInt(data, 0, (uint)header); // pFiles
        WriteInt(data, 16, 1);           // fWide
        Encoding.Unicode.GetBytes(list, 0, list.Length, data, header);

        if (!Open()) return false;
        try
        {
            if (User32.EmptyClipboard() == 0) return false;
            return SetBytes(CF_HDROP, data);
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    // ---------------------------------------------------------------- 共用写入

    /// <summary>在已打开的剪贴板会话里写入一块全局内存；失败时释放句柄。</summary>
    static bool SetBytes(uint format, byte[] data)
    {
        if (data is null || data.Length == 0) return false;
        var handle = Kernel32.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)data.Length);
        if (handle == IntPtr.Zero) return false;
        var p = Kernel32.GlobalLock(handle);
        if (p == null)
        {
            Kernel32.GlobalFree(handle);
            return false;
        }
        try
        {
            Marshal.Copy(data, 0, (IntPtr)p, data.Length);
        }
        finally
        {
            Kernel32.GlobalUnlock(handle);
        }
        // 成功后内存归系统所有；失败时由我们释放
        if (User32.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            Kernel32.GlobalFree(handle);
            return false;
        }
        return true;
    }

    static void WriteInt(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
