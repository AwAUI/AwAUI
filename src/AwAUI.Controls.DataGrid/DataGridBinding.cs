using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace AwAUI.Controls.DataGrid;

/// <summary>
/// Reflection-based access to the property path of a dictionary column (<see cref="DataGridColumn.Binding"/>).
/// Rows are ordinary objects, so this is the writable counterpart of <c>ItemsControl.ItemTemplate</c>.
/// </summary>
internal static class DataGridBinding
{
    static readonly Dictionary<Type, Dictionary<string, PropertyInfo[]?>> s_paths = new();
    static readonly object s_gate = new();

    static PropertyInfo[]? Resolve(Type type, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (s_gate)
        {
            if (!s_paths.TryGetValue(type, out var byPath))
            {
                byPath = new Dictionary<string, PropertyInfo[]?>(StringComparer.Ordinal);
                s_paths[type] = byPath;
            }
            if (byPath.TryGetValue(path!, out var cached)) return cached;

            string[] parts = path!.Split('.');
            var properties = new PropertyInfo[parts.Length];
            Type current = type;
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i].Trim();
                var property = string.IsNullOrEmpty(name)
                    ? null
                    : current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is null || property.GetIndexParameters().Length != 0 || property.GetGetMethod() is null)
                    return byPath[path] = null;
                properties[i] = property;
                current = property.PropertyType;
            }
            return byPath[path] = properties;
        }
    }

    /// <summary>The property type at the end of <paramref name="path"/>, or null when the path cannot be resolved.</summary>
    public static Type? GetValueType(object? item, string? path)
    {
        if (item is null) return null;
        var properties = Resolve(item.GetType(), path);
        return properties is { Length: > 0 } ? properties[properties.Length - 1].PropertyType : null;
    }

    /// <summary>Reads <paramref name="path"/> from <paramref name="item"/>; a missing row or path yields null.</summary>
    public static object? Get(object? item, string? path)
    {
        if (item is null || string.IsNullOrEmpty(path)) return null;
        var properties = Resolve(item.GetType(), path);
        if (properties is null) return null;
        try
        {
            object? current = item;
            foreach (var property in properties)
            {
                if (current is null) return null;
                current = property.GetValue(current);
            }
            return current;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryGet(object? item, string? path, out object? value)
    {
        value = Get(item, path);
        return value is not null;
    }

    /// <summary>
    /// Writes <paramref name="value"/> through the last property setter of <paramref name="path"/>.
    /// A missing path, a read-only property, or a value-type row fails with a message instead of throwing.
    /// </summary>
    public static bool TrySet(object? item, string? path, object? value, out string? error)
    {
        error = null;
        if (item is null)
        {
            error = "The row is null.";
            return false;
        }
        if (string.IsNullOrEmpty(path))
        {
            error = "The column has no binding.";
            return false;
        }
        if (item.GetType().IsValueType)
        {
            // 装箱的结构体副本无法写回源集合；与 Avalonia 一样要求引用类型的行对象。
            error = "Rows of value type cannot be edited in place.";
            return false;
        }
        var properties = Resolve(item.GetType(), path);
        if (properties is null || properties.Length == 0)
        {
            error = "The binding path '" + path + "' was not found.";
            return false;
        }
        var last = properties[properties.Length - 1];
        if (!last.CanWrite || last.GetSetMethod() is null)
        {
            error = "The property '" + last.Name + "' has no setter.";
            return false;
        }

        object? target = item;
        try
        {
            for (int i = 0; i < properties.Length - 1; i++)
            {
                if (target is null)
                {
                    error = "The binding path '" + path + "' has a null segment.";
                    return false;
                }
                target = properties[i].GetValue(target);
            }
            if (target is null)
            {
                error = "The binding path '" + path + "' has a null segment.";
                return false;
            }
            if (value is null && last.PropertyType.IsValueType && Nullable.GetUnderlyingType(last.PropertyType) is null)
            {
                error = "The property '" + last.Name + "' cannot be empty.";
                return false;
            }
            if (value is not null && !last.PropertyType.IsInstanceOfType(value))
            {
                if (!TryConvert(Convert.ToString(value, CultureInfo.CurrentCulture), last.PropertyType, out value, out error))
                    return false;
            }
            last.SetValue(target, value);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Converts editor text to <paramref name="type"/>. Empty text becomes null for reference and nullable types,
    /// and fails for non-nullable value types so the grid can keep the editor open and mark the cell invalid.
    /// </summary>
    public static bool TryConvert(string? text, Type? type, out object? value, out string? error)
    {
        error = null;
        if (type is null || type == typeof(object))
        {
            value = text;
            return true;
        }
        var underlying = Nullable.GetUnderlyingType(type);
        bool nullable = underlying is not null || !type.IsValueType;
        bool empty = string.IsNullOrEmpty(text);
        if (empty)
        {
            if (type == typeof(string))
            {
                value = "";
                return true;
            }
            if (nullable)
            {
                value = null;
                return true;
            }
            value = null;
            error = "A value is required.";
            return false;
        }

        try
        {
            if (type == typeof(string) || underlying == typeof(string))
            {
                value = text;
                return true;
            }
            var target = underlying ?? type;
            if (target.IsEnum)
            {
                value = Enum.Parse(target, text!, ignoreCase: true);
                return true;
            }
            if (target == typeof(bool))
            {
                var trimmed = text!.Trim();
                if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "1", StringComparison.Ordinal)
                    || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }
                if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "0", StringComparison.Ordinal)
                    || string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }
                value = null;
                error = "Enter true or false.";
                return false;
            }
            if (target == typeof(char))
            {
                if (text!.Length == 1)
                {
                    value = text[0];
                    return true;
                }
                value = null;
                error = "Enter a single character.";
                return false;
            }
            if (target == typeof(Guid))
            {
                if (Guid.TryParse(text, out var guid))
                {
                    value = guid;
                    return true;
                }
                value = null;
                error = "Enter a valid GUID.";
                return false;
            }
            if (target == typeof(DateTime))
            {
                if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
                    || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    value = date;
                    return true;
                }
                value = null;
                error = "Enter a valid date.";
                return false;
            }
            if (target == typeof(DateTimeOffset))
            {
                if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
                    || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    value = date;
                    return true;
                }
                value = null;
                error = "Enter a valid date.";
                return false;
            }
            if (target == typeof(TimeSpan))
            {
                if (TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out var span)
                    || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out span))
                {
                    value = span;
                    return true;
                }
                value = null;
                error = "Enter a valid duration.";
                return false;
            }
            if (target == typeof(byte) || target == typeof(sbyte) || target == typeof(short) || target == typeof(ushort)
                || target == typeof(int) || target == typeof(uint) || target == typeof(long) || target == typeof(ulong)
                || target == typeof(float) || target == typeof(double) || target == typeof(decimal))
            {
                value = Convert.ChangeType(text, target, CultureInfo.CurrentCulture);
                return true;
            }
            if (type.IsAssignableFrom(typeof(string)))
            {
                value = text;
                return true;
            }
        }
        catch (Exception)
        {
            // 统一为一条可供 aria-invalid 展示的消息，不把格式异常泄漏给调用方。
        }
        value = null;
        error = "The value is not a valid " + (Nullable.GetUnderlyingType(type) ?? type).Name + ".";
        return false;
    }

    /// <summary>Public readable instance properties suitable for <see cref="DataGrid.AutoGenerateColumns"/>.</summary>
    public static List<PropertyInfo> GetBindableProperties(Type type)
    {
        var result = new List<PropertyInfo>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            var getter = property.GetGetMethod();
            if (getter is null || !getter.IsPublic) continue;
            var propertyType = property.PropertyType;
            if (propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType)) continue;
            result.Add(property);
        }
        return result;
    }
}
