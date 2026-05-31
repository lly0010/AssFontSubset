using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace AssFontSubset.Core;

/// <summary>
/// A single indexed font face. The serialized shape intentionally contains the
/// fields requested for the font library (families / fullnames / psnames / weight /
/// slant / path / index / last_write_time) plus a few extra fields that are needed
/// to reproduce a <see cref="FontInfo"/> for the matching pipeline.
/// </summary>
public sealed class FontDatabaseEntry
{
    [JsonPropertyName("families")]
    public List<string> Families { get; set; } = [];

    [JsonPropertyName("fullnames")]
    public List<string> FullNames { get; set; } = [];

    [JsonPropertyName("psnames")]
    public List<string> PsNames { get; set; } = [];

    [JsonPropertyName("weight")]
    public int Weight { get; set; }

    /// <summary>0 = upright, 1 = italic / oblique.</summary>
    [JsonPropertyName("slant")]
    public int Slant { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("last_write_time")]
    public string LastWriteTime { get; set; } = string.Empty;

    // --- extra fields used by the matching pipeline ---

    /// <summary>Bold flag from the OS/2 fsSelection table (needed for style matching).</summary>
    [JsonPropertyName("bold")]
    public bool Bold { get; set; }

    /// <summary>Number of glyphs from the maxp table (used to guess CJK fonts).</summary>
    [JsonPropertyName("maxp_num_glyphs")]
    public int MaxpNumGlyphs { get; set; }

    /// <summary>Family names keyed by their OpenType language id, used for grouping.</summary>
    [JsonPropertyName("family_names")]
    public Dictionary<int, string> FamilyNamesByLang { get; set; } = [];

    public FontInfo ToFontInfo()
    {
        var familyNames = FamilyNamesByLang.Count > 0
            ? new Dictionary<int, string>(FamilyNamesByLang)
            : Families.Select((name, i) => (name, i)).ToDictionary(x => x.i == 0 ? FontConstant.LanguageIdEnUs : 100000 + x.i, x => x.name);

        if (!familyNames.ContainsKey(FontConstant.LanguageIdEnUs) && familyNames.Count > 0)
        {
            familyNames[FontConstant.LanguageIdEnUs] = familyNames.First().Value;
        }

        var matchNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Families.Concat(FullNames).Concat(PsNames))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                matchNames.Add(name);
            }
        }
        foreach (var name in familyNames.Values)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                matchNames.Add(name);
            }
        }

        return new FontInfo
        {
            FamilyNames = familyNames,
            MatchNames = matchNames,
            Bold = Bold,
            Italic = Slant != 0,
            Weight = Weight,
            FileName = Path,
            Index = (uint)Index,
            MaxpNumGlyphs = (ushort)MaxpNumGlyphs,
        };
    }
}

/// <summary>
/// A persistent index of fonts found in one or more library folders. Once built it
/// removes the need to gather the correct font files for every subset job.
/// </summary>
public sealed class FontDatabase
{
    public static readonly string[] SupportedExtensions = [".ttf", ".otf", ".ttc", ".otc"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<FontDatabaseEntry> Entries { get; private set; } = [];

    public int Count => Entries.Count;

    public static FontDatabase Load(string path)
    {
        var db = new FontDatabase();
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
            {
                db.Entries = JsonSerializer.Deserialize<List<FontDatabaseEntry>>(json, JsonOptions) ?? [];
            }
        }
        return db;
    }

    public void Save(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(Entries, JsonOptions));
    }

    /// <summary>
    /// Scan the given library folders (recursively) and rebuild the index.
    /// </summary>
    public void Build(IEnumerable<string> libraryFolders, ILogger? logger = null)
    {
        var files = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in libraryFolders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                logger?.ZLogWarning($"Skip missing library folder: {folder}");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (SupportedExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    && seen.Add(file))
                {
                    files.Add(new FileInfo(file));
                }
            }
        }

        logger?.ZLogInformation($"Indexing {files.Count} font files from {libraryFolders.Count()} folder(s)");
        Entries = FontParse.BuildDatabaseEntries(files, logger);
        logger?.ZLogInformation($"Indexed {Entries.Count} font faces");
    }

    /// <summary>
    /// Select the font faces required by the given ass font names. The full family of
    /// any matched face is included so that weight/italic selection still works.
    /// </summary>
    public List<FontInfo> SelectForNames(IEnumerable<string> requiredNames)
    {
        var required = new HashSet<string>(
            requiredNames.Select(n => n.StartsWith('@') ? n[1..] : n),
            StringComparer.Ordinal);

        var infos = Entries.Select(e => e.ToFontInfo()).ToList();
        var groups = infos.GroupBy(fi => fi.FamilyNames.TryGetValue(FontConstant.LanguageIdEnUs, out var n) ? n : fi.FamilyNames.Values.FirstOrDefault() ?? string.Empty);

        var selected = new List<FontInfo>();
        foreach (var group in groups)
        {
            var matched = group.Any(fi => (fi.MatchNames?.Overlaps(required) ?? false)
                                          || fi.FamilyNames.Values.Any(required.Contains));
            if (matched)
            {
                selected.AddRange(group);
            }
        }
        return selected;
    }
}
