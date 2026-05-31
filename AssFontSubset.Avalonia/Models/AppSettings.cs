using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssFontSubset.Avalonia.Models;

/// <summary>
/// Persistent application settings: the font library folders, the database location
/// and the last used subset options. Stored under the user's application data folder.
/// </summary>
public sealed class AppSettings
{
    [JsonIgnore]
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AssFontSubset");

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");

    [JsonIgnore]
    public static string DefaultDatabasePath { get; } = Path.Combine(AppDataDir, "fontdb.json");

    public List<string> LibraryFolders { get; set; } = [];
    public string DatabasePath { get; set; } = DefaultDatabasePath;

    public string OutputFolder { get; set; } = string.Empty;
    public string FontFolder { get; set; } = string.Empty;

    public bool UseDatabase { get; set; }
    public bool SourceHanEllipsis { get; set; } = true;
    public bool DebugMode { get; set; }
    public bool UseHarfBuzz { get; set; }
    public bool ConvertOtfToTtf { get; set; }
    public string? PythonPath { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
            }
        }
        catch
        {
            // Ignore malformed settings and fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Saving settings is best-effort; never crash the app for it.
        }
    }
}
