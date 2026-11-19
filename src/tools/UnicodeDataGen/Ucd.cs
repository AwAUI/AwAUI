using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;

namespace AwAUI.Tools.UnicodeDataGen;

/// <summary>UCD 文件的读取：本地缓存目录优先，缺失时从 unicode.org 下载并写入缓存。</summary>
internal sealed class Ucd
{
    public const int CodePointCount = 0x110000;

    readonly string _cacheDir;
    readonly string _baseUrl;
    readonly bool _allowDownload;
    HttpClient? _http;

    /// <summary>属性名（宽松匹配）→ (值别名（宽松匹配）→ 短名)。</summary>
    readonly Dictionary<string, Dictionary<string, string>> _valueAliases = new();
    /// <summary>属性名（宽松匹配）→ 按文件顺序的 (短名, 长名)。</summary>
    readonly Dictionary<string, List<(string Short, string Long)>> _valueOrder = new();

    public Ucd(string cacheDir, string baseUrl, bool allowDownload)
    {
        _cacheDir = cacheDir;
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        _allowDownload = allowDownload;
    }

    public string GetPath(string relative) => Fetch(relative, _baseUrl + relative);

    /// <summary>Unicode 许可文本（不在 ucd 目录下）。</summary>
    public string GetPathOfLicense() => Fetch("license.txt", "https://www.unicode.org/license.txt");

    string Fetch(string relative, string url)
    {
        var path = Path.Combine(_cacheDir, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) return path;
        if (!_allowDownload) throw new FileNotFoundException($"UCD file not found in cache and downloads are disabled: {path}");
        if (_http == null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AwAUI-UnicodeDataGen/1.0");
        }
        Console.Error.WriteLine($"downloading {url}");
        var bytes = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string[] ReadLines(string relative) => File.ReadAllLines(GetPath(relative), Encoding.UTF8);

    /// <summary>读取文件第一行里的版本号，如 "# LineBreak-18.0.0.txt"。</summary>
    public string ReadVersionTag(string relative)
    {
        var first = ReadLines(relative)[0];
        int dash = first.LastIndexOf('-');
        int dot = first.LastIndexOf(".txt", StringComparison.Ordinal);
        return dash >= 0 && dot > dash ? first.Substring(dash + 1, dot - dash - 1) : "?";
    }

    public static string Loose(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c != '_' && c != ' ' && c != '-') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public void LoadAliases()
    {
        foreach (var raw in ReadLines("PropertyValueAliases.txt"))
        {
            var line = StripComment(raw);
            if (line.Length == 0) continue;
            var f = Split(line);
            if (f.Length < 3) continue;
            var prop = Loose(f[0]);
            if (prop == "ccc") continue; // 规范组合类多一个数值列，这里不需要
            var shortName = f[1];
            if (!_valueAliases.TryGetValue(prop, out var map))
            {
                _valueAliases[prop] = map = new Dictionary<string, string>();
                _valueOrder[prop] = new List<(string, string)>();
            }
            // gc 的组合值（如 L、LC）在注释里带 "|"，原文不再区分：在调用方过滤
            for (int i = 1; i < f.Length; i++) map[Loose(f[i])] = shortName;
            _valueOrder[prop].Add((shortName, f[2]));
        }
    }

    public string ToShortName(string prop, string value)
    {
        if (_valueAliases.TryGetValue(Loose(prop), out var map) && map.TryGetValue(Loose(value), out var s)) return s;
        throw new InvalidDataException($"Unknown value '{value}' for property '{prop}'.");
    }

    public IReadOnlyList<(string Short, string Long)> ValuesOf(string prop) => _valueOrder[Loose(prop)];

    public static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        return (hash >= 0 ? line.Substring(0, hash) : line).Trim();
    }

    public static string[] Split(string line)
    {
        var parts = line.Split(';');
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        return parts;
    }

    public static (int Lo, int Hi) ParseRange(string s)
    {
        int dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
        {
            int v = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (v, v);
        }
        return (int.Parse(s.AsSpan(0, dots), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(s.AsSpan(dots + 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static int ParseHex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// 读取枚举型属性文件（"范围 ; 值" 或 "范围 ; 属性 ; 值"），先按顺序应用 @missing 默认值，再应用显式条目。
    /// <paramref name="propertyField"/> 非空时只取第二列等于它的行（如 DerivedCoreProperties 中的 InCB）。
    /// </summary>
    public void ReadEnumerated(string file, string aliasProperty, string? propertyField, Func<string, int> map, int[] target)
    {
        var lines = ReadLines(file);
        foreach (var raw in lines)
        {
            const string Missing = "# @missing:";
            if (!raw.StartsWith(Missing, StringComparison.Ordinal)) continue;
            var f = Split(raw.Substring(Missing.Length).Trim());
            string value;
            if (propertyField != null)
            {
                if (f.Length < 3 || f[1] != propertyField) continue;
                value = f[2];
            }
            else value = f[1];
            var (lo, hi) = ParseRange(f[0]);
            int v = map(ToShortName(aliasProperty, value));
            for (int cp = lo; cp <= hi; cp++) target[cp] = v;
        }
        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (line.Length == 0) continue;
            var f = Split(line);
            string value;
            if (propertyField != null)
            {
                if (f.Length < 3 || f[1] != propertyField) continue;
                value = f[2];
            }
            else value = f[1];
            var (lo, hi) = ParseRange(f[0]);
            int v = map(ToShortName(aliasProperty, value));
            for (int cp = lo; cp <= hi; cp++) target[cp] = v;
        }
    }

    /// <summary>读取二元属性（"范围 ; 属性名"）。</summary>
    public bool[] ReadBinary(string file, string property)
    {
        var result = new bool[CodePointCount];
        int count = 0;
        foreach (var raw in ReadLines(file))
        {
            var line = StripComment(raw);
            if (line.Length == 0) continue;
            var f = Split(line);
            if (f.Length < 2 || f[1] != property) continue;
            var (lo, hi) = ParseRange(f[0]);
            for (int cp = lo; cp <= hi; cp++) result[cp] = true;
            count++;
        }
        if (count == 0) throw new InvalidDataException($"Property {property} not found in {file}.");
        return result;
    }
}
