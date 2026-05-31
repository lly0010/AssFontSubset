using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssFontSubset.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AssFontSubset.Avalonia.Views
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

        public MainWindow()
        {
            InitializeComponent();

            AddHandler(DragDrop.DragOverEvent, DragOver_Files);
            AddHandler(DragDrop.DropEvent, Drop_Files);
            Closing += (_, _) => ViewModel.SaveSettings();
        }

        private async void AddAss_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select ASS subtitle files",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("ASS Subtitles") { Patterns = ["*.ass"] }],
            });

            if (files.Count > 0)
            {
                ViewModel.AddAssFiles(files.Select(f => f.Path.LocalPath));
            }
        }

        private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
        {
            var folder = await PickSingleFolderAsync("Select output folder");
            if (folder is not null)
            {
                ViewModel.OutputFolder = folder;
            }
        }

        private async void BrowseFontFolder_Click(object? sender, RoutedEventArgs e)
        {
            var folder = await PickSingleFolderAsync("Select fonts folder");
            if (folder is not null)
            {
                ViewModel.FontFolder = folder;
            }
        }

        private async void AddLibraryFolder_Click(object? sender, RoutedEventArgs e)
        {
            var folder = await PickSingleFolderAsync("Add a font library folder");
            if (folder is not null)
            {
                ViewModel.AddLibraryFolder(folder);
            }
        }

        private async void BrowseDatabase_Click(object? sender, RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Select database file",
                SuggestedFileName = "fontdb.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });

            if (file is not null)
            {
                ViewModel.DatabasePath = file.Path.LocalPath;
            }
        }

        private async Task<string?> PickSingleFolderAsync(string title)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
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

            var paths = files
                .Select(f => f.Path.LocalPath)
                .Where(p => string.Equals(Path.GetExtension(p), ".ass", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (paths.Count > 0)
            {
                ViewModel.AddAssFiles(paths);
            }
            e.Handled = true;
        }
    }
}
