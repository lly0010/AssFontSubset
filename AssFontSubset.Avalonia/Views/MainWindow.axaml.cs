using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssFontSubset.Avalonia.ViewModels;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using I18nResources = AssFontSubset.Avalonia.I18n.Resources;

namespace AssFontSubset.Avalonia.Views
{
    public partial class MainWindow : Window
    {
        private bool _running;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();
            AddHandler(DragDrop.DragOverEvent, DragOver_Files);
            AddHandler(DragDrop.DropEvent, Drop_Files);

            ConsoleExe.Text = DetectConsoleExe() ?? string.Empty;
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

        private void Clear_Click(object? sender, RoutedEventArgs e)
        {
            AssFileList.ItemsSource = null;
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

            var paths = files.Select(f => f.Path.LocalPath);
            AddAssFiles(paths);
        }

        private void AddAssFiles(IEnumerable<string> paths)
        {
            var current = (AssFileList.ItemsSource as IEnumerable<string>)?.ToList() ?? [];
            foreach (var p in paths)
            {
                if (string.Equals(Path.GetExtension(p), ".ass", StringComparison.OrdinalIgnoreCase) && !current.Contains(p))
                {
                    current.Add(p);
                }
            }
            if (current.Count == 0) return;

            AssFileList.ItemsSource = current.OrderBy(x => x).ToList();

            // Default the font/output folders based on the first ass file's directory.
            var dir = Path.GetDirectoryName(current[0]);
            if (dir is null) return;
            if (string.IsNullOrEmpty(FontFolder.Text)) FontFolder.Text = Path.Combine(dir, "fonts");
            if (string.IsNullOrEmpty(OutputFolder.Text)) OutputFolder.Text = Path.Combine(dir, "output");
        }

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

        private async void Start_Click(object? sender, RoutedEventArgs e)
        {
            if (_running) return;

            var assFiles = (AssFileList.ItemsSource as IEnumerable<string>)?.ToList() ?? [];
            if (assFiles.Count == 0)
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

            var args = BuildArguments(assFiles, consoleExe);
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
            // Leave it to PATH resolution if nothing concrete was found.
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private List<string> BuildArguments(List<string> assFiles, string consoleExe)
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

                if (process.ExitCode == 0)
                {
                    AppendLog(Environment.NewLine + I18nResources.SuccessSubset + Environment.NewLine);
                    await ShowMessageBox("Success", I18nResources.SuccessSubset);
                }
                else
                {
                    var msg = string.Format(I18nResources.ErrorExitCode, process.ExitCode);
                    AppendLog(Environment.NewLine + msg + Environment.NewLine);
                    await ShowMessageBox("Error", msg);
                }
            }
            catch (Exception ex)
            {
                AppendLog(Environment.NewLine + ex.Message + Environment.NewLine);
                await ShowMessageBox("Error", ex.Message);
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
        }

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

        private void DragOver_Files(object? sender, DragEventArgs e)
        {
            var files = e.DataTransfer.TryGetFiles();
            e.DragEffects = files is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Drop_Files(object? sender, DragEventArgs e)
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is not { Length: > 0 }) return;

            var paths = files.Select(f => f.Path.LocalPath).ToArray();

            // A dropped folder sets the font directory; dropped ass files are queued.
            var folder = paths.FirstOrDefault(Directory.Exists);
            if (folder is not null)
            {
                FontFolder.Text = folder;
            }

            var assPaths = paths.Where(p => string.Equals(Path.GetExtension(p), ".ass", StringComparison.OrdinalIgnoreCase));
            AddAssFiles(assPaths);
            e.Handled = true;
        }
    }
}
