using System.Globalization;

namespace AwAUI.Text.UnitTests;

/// <summary>由 Expected/dump_fonttools.py 用 fontTools 生成的期望值。</summary>
public sealed class ExpectedData
{
    public readonly Dictionary<string, int[]> Records = new();
    public readonly Dictionary<int, string> Names = new();
    public readonly List<(int Cp, int Gid)> Cmap = new();
    public readonly List<(int Gid, int Advance, int Lsb)> Hmtx = new();
    public readonly List<int[]> Glyphs = new();
    public readonly List<(int Left, int Right, int Value)> Kern = new();
    public float ItalicAngle;

    public static ExpectedData Load(string fontFile)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Expected", System.IO.Path.ChangeExtension(fontFile, ".txt"));
        var d = new ExpectedData();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOf(' ');
            string kind = line.Substring(0, sp), rest = line.Substring(sp + 1);
            if (kind == "name")
            {
                int sp2 = rest.IndexOf(' ');
                d.Names[int.Parse(rest.Substring(0, sp2), CultureInfo.InvariantCulture)] = rest.Substring(sp2 + 1);
                continue;
            }
            if (kind == "post")
            {
                var parts = rest.Split(' ');
                d.ItalicAngle = float.Parse(parts[0], CultureInfo.InvariantCulture);
                d.Records[kind] = parts.Skip(1).Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                continue;
            }
            var v = rest.Split(' ').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            switch (kind)
            {
                case "cmap": d.Cmap.Add((v[0], v[1])); break;
                case "hmtx": d.Hmtx.Add((v[0], v[1], v[2])); break;
                case "glyf": d.Glyphs.Add(v); break;
                case "kern": d.Kern.Add((v[0], v[1], v[2])); break;
                default: d.Records[kind] = v; break;
            }
        }
        return d;
    }
}
