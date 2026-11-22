using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace AwAUI.Text;

/// <summary>
/// 字体文件的只读数据。优先用内存映射：字体文件往往几十 MB（CJK 字体），而排版只访问其中的少数表与字形，
/// 映射后只有访问过的页驻留，且是可被系统回收的文件页，不计入进程的私有内存；
/// 读入托管数组则整个文件都成为 GC 大对象堆上的私有内存。
/// <para>
/// 不支持映射的环境（浏览器 / 受限文件系统）退回到整文件读取。
/// 没有终结器：映射只在显式释放时解除（全量扫描中未命中的文件）。被字型引用的映射与字型同寿命，
/// 即进程寿命——终结器可能在 <see cref="Span{T}"/> 仍在使用时解除映射（CA2015）。
/// 映射期间文件以 <c>FileShare.ReadWrite | FileShare.Delete</c> 打开，不阻止系统更新或卸载字体。
/// </para>
/// </summary>
internal sealed unsafe class FontFileData : MemoryManager<byte>
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly int _length;
    byte* _pointer;

    FontFileData(MemoryMappedFile file, MemoryMappedViewAccessor view, int length)
    {
        _file = file;
        _view = view;
        _length = length;
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _pointer = p + view.PointerOffset;
    }

    /// <summary>
    /// 打开字体文件。<paramref name="owner"/> 非 null 时数据来自内存映射，释放它即解除映射
    /// （之后不得再访问返回的内存）；为 null 时数据是托管数组。
    /// </summary>
    /// <exception cref="IOException">文件不存在或无法读取。</exception>
    /// <exception cref="UnauthorizedAccessException">没有读取权限。</exception>
    public static ReadOnlyMemory<byte> Open(string path, out IDisposable? owner)
    {
        owner = null;
        FileStream? stream = null;
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            // 小文件直接读取：映射至少占一页地址空间与两个句柄，不划算
            if (length is >= MinMappedBytes and <= int.MaxValue)
            {
                file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                stream = null;   // 所有权转给映射
                view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
                var data = new FontFileData(file, view, (int)length);
                owner = data;
                return data.Memory;
            }
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NotSupportedException or ArgumentException
            or IOException or UnauthorizedAccessException)
        {
            // 映射失败：退回整文件读取（由它报告真正的 IO 错误）
            view?.Dispose();
            file?.Dispose();
        }
        finally
        {
            stream?.Dispose();
        }
        return File.ReadAllBytes(path);
    }

    const long MinMappedBytes = 64 * 1024;

    public override Span<byte> GetSpan()
    {
        if (_pointer == null) throw new ObjectDisposedException(nameof(FontFileData));
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (_pointer == null) throw new ObjectDisposedException(nameof(FontFileData));
        if ((uint)elementIndex > (uint)_length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_pointer + elementIndex);   // 映射的地址不会移动
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer == null) return;
        _pointer = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        if (!disposing) return;
        _view.Dispose();
        _file.Dispose();
    }
}
