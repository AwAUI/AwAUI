using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

// 绑定按属性名做反射查找（不发出 IL）。类型被裁剪时无法保证可用，但 AOT 下只要类型保留即可工作。
#pragma warning disable IL2072

namespace AwAUI.Mvvm;

/// <summary>
/// An immutable, thread-safe property path such as <c>Name</c>, <c>Address.City</c> or <c>Items[0].Name</c>.
/// Property getters and setters are resolved through reflection and cached per segment; no dynamic code is emitted.
/// </summary>
public sealed class PropertyPath : IEquatable<PropertyPath>
{
    static readonly PropertyPathSegment[] s_empty = Array.Empty<PropertyPathSegment>();

    readonly PropertyPathSegment[] _segments;

    PropertyPath(string path, PropertyPathSegment[] segments)
    {
        Path = path;
        _segments = segments;
    }

    /// <summary>Parses a property path.</summary>
    /// <param name="path">The path text, for example <c>Address.City</c> or <c>Items[0].Name</c>.</param>
    /// <returns>The parsed path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
    /// <exception cref="FormatException">The path syntax is invalid.</exception>
    public static PropertyPath Parse(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        var segments = ParseCore(path, out string? error);
        if (segments is null) throw new FormatException($"Invalid property path \"{path}\": {error}");
        return new PropertyPath(path, segments);
    }

    /// <summary>Attempts to parse a property path.</summary>
    /// <param name="path">The path text.</param>
    /// <param name="result">The parsed path when parsing succeeds; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when the path was parsed.</returns>
    public static bool TryParse(string? path, out PropertyPath? result)
    {
        result = null;
        if (path is null) return false;
        var segments = ParseCore(path, out _);
        if (segments is null) return false;
        result = new PropertyPath(path, segments);
        return true;
    }

    /// <summary>The original path text.</summary>
    public string Path { get; }

    /// <summary>The parsed segments, in source order.</summary>
    public IReadOnlyList<PropertyPathSegment> Segments => _segments;

    /// <summary>Whether this path has no segments (an empty path).</summary>
    public bool IsEmpty => _segments.Length == 0;

    /// <summary>Whether every segment has a setter. Indexer segments are writable when they support assignment.</summary>
    public bool CanWrite
    {
        get
        {
            if (IsEmpty) return false;
            foreach (var segment in _segments)
                if (!segment.CanWrite) return false;
            return true;
        }
    }

    /// <summary>Reads the value at the end of the path.</summary>
    /// <param name="root">The root object.</param>
    /// <returns>The resolved value, which may be <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">A segment could not be resolved.</exception>
    public object? GetValue(object root)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (IsEmpty) return root;
        if (!TryGetValue(root, out object? value))
            throw new InvalidOperationException($"Cannot resolve property path \"{Path}\".");
        return value;
    }

    /// <summary>Attempts to read the value at the end of the path without throwing.</summary>
    /// <param name="root">The root object.</param>
    /// <param name="value">The resolved value, or <c>null</c> when resolution fails.</param>
    /// <returns><c>true</c> when the value was resolved.</returns>
    public bool TryGetValue(object? root, out object? value)
    {
        value = null;
        if (root is null) return false;
        if (IsEmpty)
        {
            value = root;
            return true;
        }

        object? current = root;
        foreach (var segment in _segments)
        {
            if (current is null) return false;
            if (!segment.TryGetValue(current, out current)) return false;
        }

        value = current;
        return true;
    }

    /// <summary>Writes a value to the end of the path.</summary>
    /// <param name="root">The root object.</param>
    /// <param name="value">The value to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">A segment could not be resolved or written.</exception>
    public void SetValue(object root, object? value)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (IsEmpty) throw new InvalidOperationException("Cannot assign to an empty property path.");

        object? current = root;
        for (int i = 0; i < _segments.Length - 1; i++)
        {
            if (current is null)
                throw new InvalidOperationException($"Cannot resolve property path \"{Path}\": an intermediate value is null.");
            if (!_segments[i].TryGetValue(current, out current))
                throw new InvalidOperationException($"Cannot resolve property path \"{Path}\": segment \"{_segments[i]}\" is not readable.");
        }

        if (current is null)
            throw new InvalidOperationException($"Cannot resolve property path \"{Path}\": an intermediate value is null.");
        _segments[_segments.Length - 1].SetValue(current, value);
    }

    /// <summary>Attempts to write a value to the end of the path without throwing.</summary>
    /// <param name="root">The root object.</param>
    /// <param name="value">The value to write.</param>
    /// <returns><c>true</c> when the value was written.</returns>
    public bool TrySetValue(object? root, object? value)
    {
        if (root is null) return false;
        try
        {
            SetValue(root, value);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is TargetInvocationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool Equals(PropertyPath? other)
        => other is not null && string.Equals(Path, other.Path, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PropertyPath);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Path);

    /// <inheritdoc />
    public override string ToString() => Path;

    static PropertyPathSegment[]? ParseCore(string path, out string? error)
    {
        error = null;
        var text = path.Trim();
        if (text.Length == 0) return s_empty;

        var segments = new List<PropertyPathSegment>();
        int i = 0;
        while (i < text.Length)
        {
            int nameStart = i;
            while (i < text.Length && text[i] != '.' && text[i] != '[' && text[i] != ']')
            {
                char c = text[i];
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                {
                    error = $"Unexpected character '{c}' at position {i}.";
                    return null;
                }
                i++;
            }

            string name = text.Substring(nameStart, i - nameStart);
            var indexes = new List<object?>();

            while (i < text.Length && text[i] == '[')
            {
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length)
                {
                    error = "Unterminated indexer.";
                    return null;
                }

                object? index;
                char c = text[i];
                if (c == '\'' || c == '"')
                {
                    char quote = c;
                    i++;
                    int valueStart = i;
                    while (i < text.Length && text[i] != quote) i++;
                    if (i >= text.Length)
                    {
                        error = "Unterminated string indexer.";
                        return null;
                    }
                    index = text.Substring(valueStart, i - valueStart);
                    i++;
                }
                else
                {
                    int valueStart = i;
                    while (i < text.Length && text[i] != ']') i++;
                    if (i >= text.Length)
                    {
                        error = "Unterminated indexer.";
                        return null;
                    }
                    string raw = text.Substring(valueStart, i - valueStart).Trim();
                    if (raw.Length == 0)
                    {
                        error = "Empty indexer.";
                        return null;
                    }
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) index = number;
                    else index = raw;
                }

                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] != ']')
                {
                    error = "Expected ']' after indexer.";
                    return null;
                }
                i++;
                indexes.Add(index);
            }

            if (name.Length == 0 && indexes.Count == 0)
            {
                error = "Empty path segment.";
                return null;
            }

            segments.Add(new PropertyPathSegment(name, indexes.ToArray()));

            if (i < text.Length)
            {
                if (text[i] != '.')
                {
                    error = $"Unexpected character '{text[i]}' at position {i}.";
                    return null;
                }
                i++;
                if (i >= text.Length)
                {
                    error = "Path cannot end with '.'.";
                    return null;
                }
            }
        }

        return segments.ToArray();
    }
}

/// <summary>
/// One segment of a <see cref="PropertyPath"/>: a property name plus zero or more indexers.
/// The reflected <see cref="PropertyInfo"/> is cached per runtime type; access is thread-safe.
/// </summary>
public sealed class PropertyPathSegment
{
    readonly object _cacheLock = new();
    readonly Dictionary<Type, PropertyInfo?> _cache = new();
    readonly object?[] _indexes;

    internal PropertyPathSegment(string name, object?[] indexes)
    {
        Name = name;
        _indexes = indexes;
    }

    /// <summary>The property name; empty for a segment consisting only of indexers.</summary>
    public string Name { get; }

    /// <summary>The indexer values (integers or strings), in source order.</summary>
    public IReadOnlyList<object?> Indexes => _indexes;

    /// <summary>Whether this segment can be assigned. Read-only properties report <c>false</c>.</summary>
    public bool CanWrite
    {
        get
        {
            // Without a target instance the property type is unknown. Indexer-only segments are writable
            // when they have an index; named segments are optimistically writable and SetValue reports failures.
            return _indexes.Length > 0 || Name.Length > 0;
        }
    }

    internal bool TryGetValue(object instance, out object? value)
    {
        value = null;
        object? current = instance;
        if (Name.Length > 0)
        {
            var property = GetProperty(current!.GetType());
            if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0) return false;
            try
            {
                current = property.GetValue(current, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        foreach (var index in _indexes)
        {
            if (current is null) return false;
            if (!TryGetIndex(current, index, out current)) return false;
        }

        value = current;
        return true;
    }

    internal void SetValue(object instance, object? value)
    {
        object? current = instance;
        if (Name.Length > 0)
        {
            var property = GetProperty(current!.GetType());
            // A read-only property is still a valid navigation step when an indexer follows it.
            if (property is null || property.GetIndexParameters().Length != 0 || (_indexes.Length == 0 && !property.CanWrite))
                throw new InvalidOperationException($"Property \"{Name}\" on {current.GetType().FullName} is not writable.");
            if (_indexes.Length == 0)
            {
                property.SetValue(current, Coerce(value, property.PropertyType), null);
                return;
            }
            current = property.GetValue(current, null);
        }

        for (int i = 0; i < _indexes.Length - 1; i++)
        {
            if (current is null) throw new InvalidOperationException("An intermediate indexer value is null.");
            if (!TryGetIndex(current, _indexes[i], out current))
                throw new InvalidOperationException($"Cannot apply indexer [{_indexes[i]}].");
        }

        if (current is null) throw new InvalidOperationException("An intermediate indexer value is null.");
        SetIndex(current, _indexes[_indexes.Length - 1], value);
    }

    PropertyInfo? GetProperty([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(type, out var cached)) return cached;
        }

        var property = type.GetProperty(Name, BindingFlags.Public | BindingFlags.Instance);
        lock (_cacheLock)
        {
            _cache[type] = property;
        }
        return property;
    }

    static bool TryGetIndex(object instance, object? index, out object? value)
    {
        value = null;
        try
        {
            switch (instance)
            {
                case Array array when index is int arrayIndex:
                    if ((uint)arrayIndex >= (uint)array.Length) return false;
                    value = array.GetValue(arrayIndex);
                    return true;
                case IList list when index is int listIndex:
                    if (listIndex < 0 || listIndex >= list.Count) return false;
                    value = list[listIndex];
                    return true;
                case IDictionary dictionary:
                    if (index is null || !dictionary.Contains(index)) return false;
                    value = dictionary[index];
                    return true;
                default:
                {
                    var indexer = GetDefaultIndexer(instance.GetType(), index);
                    if (indexer is null) return false;
                    value = indexer.GetValue(instance, new[] { Coerce(index, indexer.GetIndexParameters()[0].ParameterType) });
                    return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    static void SetIndex(object instance, object? index, object? value)
    {
        switch (instance)
        {
            case Array array when index is int arrayIndex:
                if ((uint)arrayIndex >= (uint)array.Length) throw new InvalidOperationException($"Index {arrayIndex} is outside the array.");
                array.SetValue(Coerce(value, array.GetType().GetElementType() ?? typeof(object)), arrayIndex);
                return;
            case IList list when index is int listIndex:
                if (listIndex < 0 || listIndex >= list.Count) throw new InvalidOperationException($"Index {listIndex} is outside the list.");
                list[listIndex] = value;
                return;
            case IDictionary dictionary when index is not null:
                dictionary[index] = value;
                return;
            default:
            {
                var indexer = GetDefaultIndexer(instance.GetType(), index);
                if (indexer is null || !indexer.CanWrite) throw new InvalidOperationException($"Type {instance.GetType().FullName} has no writable indexer.");
                indexer.SetValue(instance, Coerce(value, indexer.PropertyType), new[] { Coerce(index, indexer.GetIndexParameters()[0].ParameterType) });
                return;
            }
        }
    }

    static PropertyInfo? GetDefaultIndexer(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type, object? index)
    {
        var indexType = index?.GetType() ?? typeof(object);
        return type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, null, new[] { indexType }, null)
            ?? type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
    }

    static object? Coerce(object? value, Type targetType)
    {
        if (value is null) return null;
        if (targetType.IsInstanceOfType(value)) return value;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsInstanceOfType(value)) return value;
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(underlying))
        {
            try
            {
                return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return value;
            }
        }
        return value;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = Name;
        foreach (var index in _indexes)
            text += "[" + (index is string s ? "\"" + s + "\"" : Convert.ToString(index, CultureInfo.InvariantCulture)) + "]";
        return text;
    }
}
