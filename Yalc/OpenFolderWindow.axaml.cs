using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace YetAnotherLosslessCutter;

/// <summary>
/// View-model for <see cref="OpenFolderWindow"/>. Seeded from <see cref="Settings.Instance"/>
/// each time the dialog opens; on OK its values are written back to Settings (so an
/// override becomes the new default) and the same instance is handed to the caller as
/// the dialog result. Cancel returns null and leaves Settings untouched.
/// </summary>
public sealed class OpenFromFolderViewModel : ViewModelBase
{
    private string _folderPath = string.Empty;
    public string FolderPath { get => _folderPath; set => Set(ref _folderPath, value); }

    private bool _includeSubFolders;
    public bool IncludeSubFolders { get => _includeSubFolders; set => Set(ref _includeSubFolders, value); }

    private bool _includeTSFiles;
    public bool IncludeTSFiles { get => _includeTSFiles; set => Set(ref _includeTSFiles, value); }

    private bool _useFileSize;
    public bool UseFileSize { get => _useFileSize; set => Set(ref _useFileSize, value); }

    private double _fileSizeMin;
    public double FileSizeMin { get => _fileSizeMin; set => Set(ref _fileSizeMin, value); }

    private double _fileSizeMax;
    public double FileSizeMax { get => _fileSizeMax; set => Set(ref _fileSizeMax, value); }

    private FilePickerSortMode _sortMode;
    public FilePickerSortMode SortMode { get => _sortMode; set => Set(ref _sortMode, value); }

    public static OpenFromFolderViewModel FromSettings()
    {
        var s = Settings.Instance;
        return new OpenFromFolderViewModel
        {
            FolderPath = s.FilePickerFolderPath,
            IncludeSubFolders = s.FilePickerIncludeSubFolders,
            IncludeTSFiles = s.IncludeTSFiles,
            UseFileSize = s.FilePickerUseSize,
            FileSizeMin = s.FilePickerSizeMin,
            FileSizeMax = s.FilePickerSizeMax,
            SortMode = s.FilePickerSortMode,
        };
    }

    public void ApplyToSettings()
    {
        var s = Settings.Instance;
        s.FilePickerFolderPath = FolderPath;
        s.FilePickerIncludeSubFolders = IncludeSubFolders;
        s.IncludeTSFiles = IncludeTSFiles;
        s.FilePickerUseSize = UseFileSize;
        s.FilePickerSizeMin = FileSizeMin;
        s.FilePickerSizeMax = FileSizeMax;
        s.FilePickerSortMode = SortMode;
    }
}

public partial class OpenFolderWindow : Window
{
    private readonly OpenFromFolderViewModel _vm = OpenFromFolderViewModel.FromSettings();

    public OpenFolderWindow()
    {
        DataContext = _vm;
        InitializeComponent();
        SortModeCombo.ItemsSource = Enum.GetValues<FilePickerSortMode>();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder",
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            _vm.FolderPath = path;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        _vm.ApplyToSettings();
        Close(_vm);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
