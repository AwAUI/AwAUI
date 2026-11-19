namespace AwAUI.Diagnostics.Json;

/// <summary><see cref="JsonReader"/> 的记号类型。</summary>
public enum JsonTokenType : byte
{
    None,
    StartObject,
    EndObject,
    StartArray,
    EndArray,
    PropertyName,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>读取器选项。</summary>
public struct JsonReaderOptions
{
    /// <summary>默认最大嵌套深度。</summary>
    public const int DefaultMaxDepth = 64;

    int _maxDepth;

    /// <summary>最大嵌套深度（对象与数组的层数），默认 64。超过时报错，防止恶意输入耗尽栈。</summary>
    public int MaxDepth
    {
        readonly get => _maxDepth == 0 ? DefaultMaxDepth : _maxDepth;
        set => _maxDepth = value > 0 ? value : throw new System.ArgumentOutOfRangeException(nameof(value));
    }
}
