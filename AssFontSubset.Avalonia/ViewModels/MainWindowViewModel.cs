using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AssFontSubset.Avalonia.Models;
using AssFontSubset.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AssFontSubset.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public static string WindowTitle => $"AssFontSubset v{Assembly.GetEntryAssembly()!.GetName().Version}";

    private readonly FontDatabase _database;
    private readonly StringBuilder _log = new();

    public MainWindowViewModel()
    {
        var settings = AppSettings.Load();

        OutputFolder = settings.OutputFolder;
        FontFolder = settings.FontFolder;
        UseDatabase = settings.UseDatabase;
        SourceHanEllipsis = settings.SourceHanEllipsis;
        DebugMode = settings.DebugMode;
        UseHarfBuzz = settings.UseHarfBuzz;
        ConvertOtfToTtf = settings.ConvertOtfToTtf;
        PythonPath = settings.PythonPath ?? string.Empty;
        DatabasePath = string.IsNullOrWhiteSpace(settings.DatabasePath) ? AppSettings.DefaultDatabasePath : settings.DatabasePath;

        AssFiles = [];
        LibraryFolders = new ObservableCollection<string>(settings.LibraryFolders);
        DatabaseFonts = [];

        _database = FontDatabase.Load(DatabasePath);
        RefreshDatabaseView();
    }

    // ---- subset tab ----
    public ObservableCollection<string> AssFiles { get; }

    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _fontFolder = string.Empty;
    [ObservableProperty] private bool _useDatabase;
    [ObservableProperty] private bool _sourceHanEllipsis = true;
    [ObservableProperty] private bool _debugMode;
    [ObservableProperty] private bool _useHarfBuzz;
    [ObservableProperty] private bool _convertOtfToTtf;
    [ObservableProperty] private string _pythonPath = string.Empty;

    // ---- font library tab ----
    public ObservableCollection<string> LibraryFolders { get; }
    public ObservableCollection<FontEntryDisplay> DatabaseFonts { get; }

    [ObservableProperty] private string _databasePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSubsetCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildDatabaseCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _logText = string.Empty;

    public int DatabaseCount => _database.Count;
    public string DatabaseSummary => $"{_database.Count} font faces indexed";

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartSubsetAsync()
    {
        if (AssFiles.Count == 0)
        {
            AppendLog(LogLevel.Error, I18n.Resources.ErrorNoAssFile);
            return;
        }

        SaveSettings();
        IsBusy = true;
        try
        {
            var path = AssFiles.Select(f => new FileInfo(f)).ToArray();
            DirectoryInfo? output = string.IsNullOrWhiteSpace(OutputFolder) ? null : new DirectoryInfo(OutputFolder);

            var config = new SubsetConfig
            {
                SourceHanEllipsis = SourceHanEllipsis,
                DebugMode = DebugMode,
                Backend = UseHarfBuzz ? SubsetBackend.HarfBuzzSubset : SubsetBackend.PyFontTools,
                ConvertOtfToTtf = ConvertOtfToTtf,
                PythonPath = string.IsNullOrWhiteSpace(PythonPath) ? null : PythonPath,
            };

            var logger = CreateLogger();
            var core = new SubsetCore(logger);

            if (UseDatabase)
            {
                if (_database.Count == 0)
                {
                    AppendLog(LogLevel.Error, I18n.Resources.ErrorEmptyDatabase);
                    return;
                }
                await core.SubsetWithDatabaseAsync(path, _database, output, null, config);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(FontFolder))
                {
                    AppendLog(LogLevel.Error, I18n.Resources.ErrorNoFontFolder);
                    return;
                }
                await core.SubsetAsync(path, new DirectoryInfo(FontFolder), output, null, config);
            }

            AppendLog(LogLevel.Information, I18n.Resources.SuccessSubset);
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BuildDatabaseAsync()
    {
        if (LibraryFolders.Count == 0)
        {
            AppendLog(LogLevel.Error, I18n.Resources.ErrorNoLibrary);
            return;
        }

        SaveSettings();
        IsBusy = true;
        try
        {
            var logger = CreateLogger();
            var folders = LibraryFolders.ToList();
            var dbPath = DatabasePath;

            await Task.Run(() =>
            {
                _database.Build(folders, logger);
                _database.Save(dbPath);
            });

            RefreshDatabaseView();
            AppendLog(LogLevel.Information, string.Format(I18n.Resources.DatabaseBuilt, _database.Count));
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearAssFiles() => AssFiles.Clear();

    [RelayCommand]
    private void RemoveLibraryFolder(string? folder)
    {
        if (folder is not null && LibraryFolders.Remove(folder))
        {
            SaveSettings();
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        _log.Clear();
        LogText = string.Empty;
    }

    public void AddAssFiles(IEnumerable<string> files)
    {
        foreach (var file in files.Where(f => string.Equals(Path.GetExtension(f), ".ass", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AssFiles.Contains(file))
            {
                AssFiles.Add(file);
            }
        }

        // Offer sensible defaults from the first dropped file.
        if (AssFiles.Count > 0 && string.IsNullOrWhiteSpace(OutputFolder))
        {
            var dir = Path.GetDirectoryName(AssFiles[0]);
            if (!string.IsNullOrEmpty(dir))
            {
                OutputFolder = Path.Combine(dir, "output");
                if (!UseDatabase && string.IsNullOrWhiteSpace(FontFolder))
                {
                    FontFolder = Path.Combine(dir, "fonts");
                }
            }
        }
    }

    public void AddLibraryFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && !LibraryFolders.Contains(folder))
        {
            LibraryFolders.Add(folder);
            SaveSettings();
        }
    }

    public void SaveSettings() => ToSettings().Save();

    private AppSettings ToSettings() => new()
    {
        LibraryFolders = LibraryFolders.ToList(),
        DatabasePath = DatabasePath,
        OutputFolder = OutputFolder,
        FontFolder = FontFolder,
        UseDatabase = UseDatabase,
        SourceHanEllipsis = SourceHanEllipsis,
        DebugMode = DebugMode,
        UseHarfBuzz = UseHarfBuzz,
        ConvertOtfToTtf = ConvertOtfToTtf,
        PythonPath = string.IsNullOrWhiteSpace(PythonPath) ? null : PythonPath,
    };

    private void RefreshDatabaseView()
    {
        DatabaseFonts.Clear();
        foreach (var entry in _database.Entries.OrderBy(e => e.Families.FirstOrDefault()))
        {
            DatabaseFonts.Add(new FontEntryDisplay(entry));
        }
        OnPropertyChanged(nameof(DatabaseCount));
        OnPropertyChanged(nameof(DatabaseSummary));
    }

    private ILogger CreateLogger() => new RelayLogger(AppendLog);

    private void AppendLog(LogLevel level, string message)
    {
        void Append()
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {level}: {message}");
            LogText = _log.ToString();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Append();
        }
        else
        {
            Dispatcher.UIThread.Post(Append);
        }
    }
}
