using System;
using System.Globalization;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

internal static class VisualPart
{
    internal static AttributeDictionary Attributes(string slot, string? defaults, string? classes, AttributeDictionary? rest)
    {
        var attributes = new AttributeDictionary(rest);
        var merged = TailwindMerge.Cn(defaults, Convert.ToString(attributes["class"], CultureInfo.InvariantCulture), classes);
        attributes["class"] = merged.Length == 0 ? null : merged;
        attributes["data-slot"] = slot;
        return attributes;
    }
}
