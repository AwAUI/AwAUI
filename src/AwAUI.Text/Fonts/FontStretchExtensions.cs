using AwAUI.Graphics.Text;

namespace AwAUI.Text;

internal static class FontStretchExtensions
{
    /// <summary>CSS 规定的字宽百分比。</summary>
    public static float ToPercentage(this FontStretch stretch) => stretch switch
    {
        FontStretch.UltraCondensed => 50f,
        FontStretch.ExtraCondensed => 62.5f,
        FontStretch.Condensed => 75f,
        FontStretch.SemiCondensed => 87.5f,
        FontStretch.SemiExpanded => 112.5f,
        FontStretch.Expanded => 125f,
        FontStretch.ExtraExpanded => 150f,
        FontStretch.UltraExpanded => 200f,
        _ => 100f,
    };
}
