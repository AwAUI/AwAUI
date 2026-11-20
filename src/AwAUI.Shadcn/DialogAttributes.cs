using System;
using System.Globalization;
using AwAUI.UI;
using static AwAUI.Tailwind.TailwindMerge;

namespace AwAUI.Shadcn;

internal static class DialogAttributes
{
    internal static AttributeDictionary ForPart(string slot, AttributeDictionary? rest, string? classes, string? defaults = null)
    {
        var attributes = new AttributeDictionary(rest);
        var merged = Cn(defaults, Convert.ToString(attributes["class"], CultureInfo.InvariantCulture), classes);
        attributes["class"] = merged.Length == 0 ? null : merged;
        attributes["data-slot"] = slot;
        return attributes;
    }
}
