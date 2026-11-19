using System;
using System.IO;
using AwAUI.Graphics.Text;

namespace AwAUI.Text;

/// <summary>字体数据的来源：内存、文件，或应用自定义的来源（例如按需下载）。</summary>
public abstract class FontSource
{
    /// <summary>要加载的字型索引；-1 表示文件中的全部字型（.ttc 集合）。</summary>
    public int FaceIndex { get; protected set; } = -1;

    /// <summary>诊断用的名称（文件路径等）。</summary>
    public virtual string Name => GetType().Name;

    /// <summary>读取完整的字体文件数据（sfnt 或 TrueType 集合）。可能被调用多次，实现应自行缓存。</summary>
    public abstract ReadOnlyMemory<byte> GetData();

    public static FontSource FromMemory(ReadOnlyMemory<byte> data, int faceIndex = -1) => new MemorySource(data, faceIndex);

    public static FontSource FromFile(string path, int faceIndex = -1) => new FileSource(path, faceIndex);

    public override string ToString() => Name;

    sealed class MemorySource : FontSource
    {
        readonly ReadOnlyMemory<byte> _data;

        public MemorySource(ReadOnlyMemory<byte> data, int faceIndex)
        {
            _data = data;
            FaceIndex = faceIndex;
        }

        public override string Name => $"<memory {_data.Length} bytes>";
        public override ReadOnlyMemory<byte> GetData() => _data;
    }

    sealed class FileSource : FontSource
    {
        readonly string _path;
        byte[]? _data;

        public FileSource(string path, int faceIndex)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            FaceIndex = faceIndex;
        }

        public override string Name => _path;
        public override ReadOnlyMemory<byte> GetData() => _data ??= File.ReadAllBytes(_path);
    }
}

/// <summary>一次注册的结果：来源与从中加载的字型。</summary>
public sealed class FontRegistration
{
    internal FontRegistration(FontSource? source, FontFace[] faces, FontDescriptor[] descriptors)
    {
        Source = source;
        Faces = faces;
        Descriptors = descriptors;
    }

    /// <summary>直接注册 <see cref="FontFace"/> 时为 null。</summary>
    public FontSource? Source { get; }

    public System.Collections.Generic.IReadOnlyList<FontFace> Faces { get; }

    /// <summary>参与匹配的描述符（与 <see cref="Faces"/> 一一对应；注册时提供了覆盖描述符则为覆盖值）。</summary>
    public System.Collections.Generic.IReadOnlyList<FontDescriptor> Descriptors { get; }
}
