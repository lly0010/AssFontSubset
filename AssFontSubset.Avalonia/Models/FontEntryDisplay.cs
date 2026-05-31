using System.Linq;
using AssFontSubset.Core;

namespace AssFontSubset.Avalonia.Models;

/// <summary>Read-only projection of a <see cref="FontDatabaseEntry"/> for list display.</summary>
public sealed class FontEntryDisplay(FontDatabaseEntry entry)
{
    public string FamilyDisplay { get; } = entry.Families.Count > 0 ? string.Join(" / ", entry.Families) : "(unknown)";
    public string Detail { get; } =
        $"weight {entry.Weight}, {(entry.Slant != 0 ? "italic" : "upright")}{(entry.Bold ? ", bold" : string.Empty)}"
        + (entry.Index > 0 ? $", index {entry.Index}" : string.Empty);
    public string Path { get; } = entry.Path;
}
