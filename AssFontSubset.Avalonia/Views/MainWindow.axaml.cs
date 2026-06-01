using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssFontSubset.Avalonia.Models;
using AssFontSubset.Avalonia.ViewModels;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using I18nResources = AssFontSubset.Avalonia.I18n.Resources;

namespace AssFontSubset.Avalonia.Views
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<string> _assFiles = [];
        private bool _running;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();

            AssFileList.ItemsSource = _assFiles;

            // Drag & drop: ass files / folders onto the list; folders onto the path boxes.
            AssFileList.AddHandler(DragDrop.DragOverEvent, DragOver_AcceptFiles);
            AssFileList.AddHandler(DragDrop.DropEvent, Drop_AssFiles);
            FontFolder.AddHandler(DragDrop.DragOverEvent, DragOver_AcceptFiles);
            FontFolder.AddHandler(DragDrop.DropEvent, Drop_FontFolder);
            OutputFolder.AddHandler(DragDrop.DragOverEvent, DragOver_AcceptFiles);
            OutputFolder.AddHandler(DragDrop.DropEvent, Drop_OutputFolder);
            AssFileList.KeyDown += AssFileList_KeyDown;

            LoadSettings();
            SetStatus(I18nResources.StatusReady);
        }

        // ---------- Settings persistence ----------

        private void LoadSettings()
        {
            var settings = AppSettings.Load();
            ConsoleExe.Text = !string.IsNullOrWhiteSpace(settings.ConsoleExePath)
                ? settings.ConsoleExePath
                : DetectConsoleExe() ?? string.Empty;
            Backend.SelectedIndex = Math.Clamp(settings.BackendIndex, 0, 1);
            SourceHanEllipsis.IsChecked = settings.SourceHanEllipsis;
            Debug.IsChecked = settings.Debug;
            EmbedFontToAss.IsChecked = settings.EmbedFontToAss;
            SeparateFontFolder.IsChecked = settings.SeparateFontFolder;
        }

        private void SaveSettings()
        {
            new AppSettings
            {
                ConsoleExePath = ConsoleExe.Text,
                BackendIndex = Backend.SelectedIndex,
                SourceHanEllipsis = SourceHanEllipsis.IsChecked == true,
                Debug = Debug.IsChecked == true,
                EmbedFontToAss = EmbedFontToAss.IsChecked == true,
                SeparateFontFolder = SeparateFontFolder.IsChecked == true,
            }.Save();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }

        /// <summary>
        /// Try to locate the AssFontSubset.Console executable shipped next to this GUI.
        /// </summary>
        private static string? DetectConsoleExe()
        {
            var exeName = OperatingSystem.IsWindows() ? "AssFontSubset.Console.exe" : "AssFontSubset.Console";
            var candidate = Path.Combine(AppContext.BaseDirectory, exeName);
            return File.Exists(candidate) ? candidate : null;
        }

        // ---------- ASS file list management ----------

        private void Clear_Click(object? sender, RoutedEventArgs e)
        {
            _assFiles.Clear();
            FontFolder.Text = string.Empty;
            OutputFolder.Text = string.Empty;
        }

        private async void AddAss_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = I18nResources.InputAssFiles,
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("ASS Subtitles") { Patterns = ["*.ass"] }],
            });
            if (files.Count == 0) return;
            AddAssFiles(files.Select(f => f.Path.LocalPath));
        }

        private async void AddFolder_Click(object? sender, RoutedEventArgs e)
        {
            var dir = await PickFolderAsync(I18nResources.AddFolder);
            if (dir is not null) AddAssFromFolder(dir);
        }

        private void RemoveSelected_Click(object? sender, RoutedEventArgs e) => RemoveSelectedAss();

        private void AssFileList_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                RemoveSelectedAss();
                e.Handled = true;
            }
        }

        private void RemoveSelectedAss()
        {
            var selected = AssFileList.SelectedItems?.Cast<string>().ToList();
            if (selected is null) return;
            foreach (var item in selected)
            {
                _assFiles.Remove(item);
            }
        }

        private void AddAssFromFolder(string folder)
        {
            if (!Directory.Exists(folder)) return;
            AddAssFiles(Directory.EnumerateFiles(folder, "*.ass", SearchOption.TopDirectoryOnly));
        }

        private void AddAssFiles(IEnumerable<string> paths)
        {
            // Merge, de-duplicate and keep sorted.
            var merged = new SortedSet<string>(_assFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (string.Equals(Path.GetExtension(p), ".ass", StringComparison.OrdinalIgnoreCase))
                {
                    merged.Add(p);
                }
            }
            if (merged.Count == _assFiles.Count) return;

            _assFiles.Clear();
            foreach (var p in merged) _assFiles.Add(p);

            // Default the font/output folders based on the first ass file's directory.
            var dir = Path.GetDirectoryName(_assFiles[0]);
            if (dir is null) return;
            if (string.IsNullOrEmpty(FontFolder.Text)) FontFolder.Text = Path.Combine(dir, "fonts");
            if (string.IsNullOrEmpty(OutputFolder.Text)) OutputFolder.Text = Path.Combine(dir, "output");
        }

        // ---------- Browse buttons ----------

        private async void BrowseFontFolder_Click(object? sender, RoutedEventArgs e)
        {
            var dir = await PickFolderAsync(I18nResources.InputFontFolder);
            if (dir is not null) FontFolder.Text = dir;
        }

        private async void BrowseOutputFolder_Click(object? sender, RoutedEventArgs e)
        {
            var dir = await PickFolderAsync(I18nResources.OutputFolder);
            if (dir is not null) OutputFolder.Text = dir;
        }

        private async void BrowseConsoleExe_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = I18nResources.ConsoleExe,
                AllowMultiple = false,
            });
            if (files.Count > 0) ConsoleExe.Text = files[0].Path.LocalPath;
        }

        private async Task<string?> PickFolderAsync(string title)
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return dirs.Count > 0 ? dirs[0].Path.LocalPath : null;
        }

        // ---------- Drag & drop ----------

        private void DragOver_AcceptFiles(object? sender, DragEventArgs e)
        {
            var files = e.DataTransfer.TryGetFiles();
            e.DragEffects = files is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Drop_AssFiles(object? sender, DragEventArgs e)
        {
            var paths = GetDroppedPaths(e);
            if (paths.Length == 0) return;

            foreach (var folder in paths.Where(Directory.Exists))
            {
                AddAssFromFolder(folder);
            }
            AddAssFiles(paths.Where(p => !Directory.Exists(p)));
            e.Handled = true;
        }

        private void Drop_FontFolder(object? sender, DragEventArgs e)
        {
            var dir = GetDroppedFolder(e);
            if (dir is not null) FontFolder.Text = dir;
            e.Handled = true;
        }

        private void Drop_OutputFolder(object? sender, DragEventArgs e)
        {
            var dir = GetDroppedFolder(e);
            if (dir is not null) OutputFolder.Text = dir;
            e.Handled = true;
        }

        private static string[] GetDroppedPaths(DragEventArgs e) =>
            e.DataTransfer.TryGetFiles()?.Select(f => f.Path.LocalPath).ToArray() ?? [];

        /// <summary>Resolve a dropped folder path: the folder itself, or the parent of a dropped file.</summary>
        private static string? GetDroppedFolder(DragEventArgs e)
        {
            var paths = GetDroppedPaths(e);
            if (paths.Length == 0) return null;
            var first = paths[0];
            return Directory.Exists(first) ? first : Path.GetDirectoryName(first);
        }

        // ---------- Subset run ----------

        private async void Start_Click(object? sender, RoutedEventArgs e)
        {
            if (_running) return;

            if (_assFiles.Count == 0)
            {
                await ShowMessageBox("Error", I18nResources.ErrorNoAssFile);
                return;
            }

            var consoleExe = ResolveConsoleExe(ConsoleExe.Text);
            if (consoleExe is null)
            {
                await ShowMessageBox("Error", I18nResources.ErrorNoConsole);
                return;
            }

            SaveSettings();
            var args = BuildArguments([.. _assFiles]);
            await RunSubsetAsync(consoleExe, args);
        }

        /// <summary>
        /// Resolve the console executable path. Falls back to the auto-detected one,
        /// then to relying on PATH.
        /// </summary>
        private static string? ResolveConsoleExe(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text) && File.Exists(text)) return text;
            var detected = DetectConsoleExe();
            if (detected is not null) return detected;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private List<string> BuildArguments(List<string> assFiles)
        {
            var args = new List<string>();
            args.AddRange(assFiles);

            if (!string.IsNullOrWhiteSpace(FontFolder.Text))
            {
                args.Add("--fonts");
                args.Add(FontFolder.Text!);
            }
            if (!string.IsNullOrWhiteSpace(OutputFolder.Text))
            {
                args.Add("--output");
                args.Add(OutputFolder.Text!);
            }

            args.Add("--subset-backend");
            args.Add(Backend.SelectedIndex == 1 ? "HarfBuzzSubset" : "PyFontTools");

            args.Add("--source-han-ellipsis");
            args.Add(SourceHanEllipsis.IsChecked == true ? "true" : "false");

            args.Add("--debug");
            args.Add(Debug.IsChecked == true ? "true" : "false");

            args.Add("--embed-font-to-ass");
            args.Add(EmbedFontToAss.IsChecked == true ? "true" : "false");

            args.Add("--separate-font-folder");
            args.Add(SeparateFontFolder.IsChecked == true ? "true" : "false");

            return args;
        }

        private async Task RunSubsetAsync(string consoleExe, List<string> args)
        {
            SetRunning(true);
            LogBox.Text = string.Empty;
            AppendLog($"> {consoleExe} {string.Join(' ', args.Select(QuoteIfNeeded))}{Environment.NewLine}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = consoleExe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, ev) => { if (ev.Data is not null) AppendLogOnUi(StripAnsi(ev.Data) + Environment.NewLine); };
                process.ErrorDataReceived += (_, ev) => { if (ev.Data is not null) AppendLogOnUi(StripAnsi(ev.Data) + Environment.NewLine); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                // Completion is shown inline; the window stays open so results/log remain visible.
                if (process.ExitCode == 0)
                {
                    AppendLog(Environment.NewLine + I18nResources.SuccessSubset + Environment.NewLine);
                    SetStatus(I18nResources.StatusDone);
                }
                else
                {
                    var msg = string.Format(I18nResources.ErrorExitCode, process.ExitCode);
                    AppendLog(Environment.NewLine + msg + Environment.NewLine);
                    SetStatus(I18nResources.StatusFailed);
                }
            }
            catch (Exception ex)
            {
                AppendLog(Environment.NewLine + ex.Message + Environment.NewLine);
                SetStatus(I18nResources.StatusFailed);
            }
            finally
            {
                SetRunning(false);
            }
        }

        private void SetRunning(bool running)
        {
            _running = running;
            Start.IsEnabled = !running;
            Progressing.IsIndeterminate = running;
            if (running) SetStatus(I18nResources.StatusRunning);
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private static string QuoteIfNeeded(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

        private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);
        private static string StripAnsi(string s) => AnsiRegex.Replace(s, string.Empty);

        private void AppendLogOnUi(string text) => Dispatcher.UIThread.Post(() => AppendLog(text));

        private void AppendLog(string text)
        {
            LogBox.Text += text;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        }

        private async Task ShowMessageBox(string title, string message)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(title, message, ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.None, WindowStartupLocation.CenterOwner);
            await box.ShowWindowDialogAsync(this);
        }
    }
}
