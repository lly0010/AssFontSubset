using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssFontSubset.Avalonia.Models;

/// <summary>
/// Persisted GUI settings, remembered between launches.
/// </summary>
public sealed class AppSettings
{
    public string? ConsoleExePath { get; set; }
    public string? FontFolder { get; set; }
    public string? FontDatabasePath { get; set; }
    public int BackendIndex { get; set; }
    public bool SourceHanEllipsis { get; set; } = true;
    public bool Debug { get; set; }
    public bool EmbedFontToAss { get; set; }
    public bool SeparateFontFolder { get; set; }
    public bool ReembedFonts { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AssFontSubset",
        "gui-settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) { return new AppSettings(); }
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Persisting settings is best-effort; ignore failures.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
