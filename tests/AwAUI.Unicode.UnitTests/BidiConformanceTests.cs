using System.Globalization;
using System.Text;

namespace AwAUI.Unicode.UnitTests;

/// <summary>运行 BidiCharacterTest.txt：段落层级、逐字符层级（含 L1）与视觉顺序（L2）。</summary>
public class BidiConformanceTests
{
    static bool IsRemoved(int cp)
    {
        var c = UnicodeData.GetBidiClass(cp);
        return c is BidiClass.BN or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF;
    }

    [Fact]
    public void BidiCharacterTest()
    {
        int cases = 0;
        var failures = new List<string>();
        int lineNo = 0;
        foreach (var raw in File.ReadLines(TestData.PathOf("BidiCharacterTest.txt"), Encoding.UTF8))
        {
            lineNo++;
            if (raw.Length == 0 || raw[0] == '#') continue;
            var f = raw.Split(';');
            var cps = f[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            var direction = f[1] switch { "0" => BidiDirection.LeftToRight, "1" => BidiDirection.RightToLeft, _ => BidiDirection.Auto };
            int expectedParagraph = int.Parse(f[2], CultureInfo.InvariantCulture);
            var expectedLevels = f[3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var expectedOrder = f[4].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            cases++;

            var sb = new StringBuilder();
            var offsets = new int[cps.Length];
            for (int i = 0; i < cps.Length; i++)
            {
                offsets[i] = sb.Length;
                sb.Append(char.ConvertFromUtf32(cps[i]));
            }
            var text = sb.ToString();
            var levels = new byte[text.Length];
            int paragraph = BidiAlgorithm.ResolveLevels(text.AsSpan(), direction, levels);
            BidiAlgorithm.ResetWhitespaceLevels(text.AsSpan(), paragraph, levels);

            var actualLevels = new string[cps.Length];
            var keptLevels = new List<byte>();
            var keptIndex = new List<int>();
            for (int i = 0; i < cps.Length; i++)
            {
                if (IsRemoved(cps[i])) { actualLevels[i] = "x"; continue; }
                actualLevels[i] = levels[offsets[i]].ToString(CultureInfo.InvariantCulture);
                keptLevels.Add(levels[offsets[i]]);
                keptIndex.Add(i);
            }
            var order = new int[keptLevels.Count];
            BidiAlgorithm.ReorderVisual(keptLevels.ToArray(), order);
            var actualOrder = order.Select(o => keptIndex[o]).ToArray();

            if (paragraph != expectedParagraph || !actualLevels.SequenceEqual(expectedLevels) || !actualOrder.SequenceEqual(expectedOrder))
            {
                failures.Add($"line {lineNo}: {raw}\n    got para {paragraph}; levels {string.Join(" ", actualLevels)}; order {string.Join(" ", actualOrder)}");
            }
        }
        Assert.Equal(91707, cases);
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} failed:\n" + string.Join("\n", failures.Take(20)));
    }
}
