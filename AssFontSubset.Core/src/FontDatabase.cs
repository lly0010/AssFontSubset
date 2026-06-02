using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mobsub.Font;
using ZLogger;

namespace AssFontSubset.Core;

/// <summary>
/// A single indexed font face, serialized to the font database JSON.
/// </summary>
public sealed class FontDatabaseEntry
{
    [JsonPropertyName("families")]
    public List<string> Families { get; set; } = [];

    [JsonPropertyName("fullnames")]
    public List<string> Fullnames { get; set; } = [];

    [JsonPropertyName("psnames")]
    public List<string> Psnames { get; set; } = [];

    [JsonPropertyName("weight")]
    public int Weight { get; set; }

    [JsonPropertyName("slant")]
    public int Slant { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public uint Index { get; set; }

    [JsonPropertyName("last_write_time")]
    public string LastWriteTime { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<FontDatabaseEntry>))]
internal partial class FontDatabaseJsonContext : JsonSerializerContext;

/// <summary>
/// Builds and reads a font database: an index of the fonts found under a directory,
/// recording each face's names, weight/slant and file location for fast lookup.
/// </summary>
public static class FontDatabase
{
    private static readonly string[] SupportedExtensions = [".ttf", ".otf", ".ttc", ".otc"];

    // Write non-ASCII (e.g. CJK family names) literally instead of \uXXXX escapes.
    private static readonly FontDatabaseJsonContext WriteContext = new(
        new JsonSerializerOptions(FontDatabaseJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>Scan <paramref name="dir"/> recursively and produce one entry per font face.</summary>
    public static List<FontDatabaseEntry> Build(DirectoryInfo dir, ILogger? logger = null)
    {
        if (!dir.Exists) { throw new DirectoryNotFoundException($"Font directory not found: {dir.FullName}"); }

        var files = dir.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(f.Extension.ToLowerInvariant()))
            .ToArray();

        logger?.ZLogInformation($"Scanning {files.Length} font files under {dir.FullName}");

        List<FontDatabaseEntry> entries = [];
        foreach (var file in files)
        {
            // Parse each file in isolation so a single bad font does not abort the whole build.
            try
            {
                foreach (var faceInfo in OpenType.GetLocalFontsInfo([file]))
                {
                    if (faceInfo is FontFaceInfoOpenType ot)
                    {
                        entries.Add(ToEntry(ot, file));
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.ZLogWarning($"Skip font file {file.FullName}: {ex.Message}");
            }
        }

        logger?.ZLogInformation($"Indexed {entries.Count} font faces");
        return entries;
    }

    public static void Write(List<FontDatabaseEntry> entries, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
        var json = JsonSerializer.Serialize(entries, WriteContext.ListFontDatabaseEntry);
        File.WriteAllText(path, json);
    }

    public static List<FontDatabaseEntry> Read(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FontDatabaseJsonContext.Default.ListFontDatabaseEntry) ?? [];
    }

    private static FontDatabaseEntry ToEntry(FontFaceInfoOpenType info, FileInfo file)
    {
        var families = new List<string>();
        AddLowered(families, info.FamilyNamesGdi?.Values);
        AddLowered(families, info.FamilyNames?.Values);

        var fullnames = new List<string>();
        AddLowered(fullnames, info.FullNames?.Values);

        var psnames = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.PostScriptName))
        {
            AddLowered(psnames, [info.PostScriptName]);
        }

        return new FontDatabaseEntry
        {
            Families = families,
            Fullnames = fullnames,
            Psnames = psnames,
            Weight = info.Weight,
            Slant = (info.fsSelection & 0b_1) == 1 ? 110 : 0, // italic bit -> fontconfig-style slant
            Path = file.FullName,
            Index = info.FaceIndex,
            LastWriteTime = file.LastWriteTimeUtc.ToString("'UTC' yyyy-MM-dd HH:mm:ss"),
        };
    }

    private static void AddLowered(List<string> target, IEnumerable<string>? names)
    {
        if (names is null) { return; }
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) { continue; }
            var lowered = name.ToLowerInvariant();
            if (!target.Contains(lowered)) { target.Add(lowered); }
        }
    }
}
