using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrophyShots.Core;

namespace TrophyShots.App.ViewModels;

/// <summary>
/// 自带的目录选择器。不用 Windows 的 shell 文件夹对话框：那个对话框用 GDI 画字，
/// 本机 MacType 的 GDI hook 会让进程在 gdi32full.dll 里 fast-fail 崩溃（2026-11-10 实测）。
/// </summary>
public partial class DirectoryPickerViewModel : ObservableObject
{
    public DirectoryPickerViewModel(string? startPath)
    {
        Drives = FolderBrowsing.Drives();
        Navigate(Directory.Exists(startPath) ? startPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public IReadOnlyList<string> Drives { get; }

    public ObservableCollection<string> Folders { get; } = [];

    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private string? _selectedDrive;

    public string SelectionHint => SelectedFolder ?? "（不选子文件夹时表示选中当前目录）";

    partial void OnSelectedDriveChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Navigate(value);
        }
    }

    partial void OnSelectedFolderChanged(string? value) => OnPropertyChanged(nameof(SelectionHint));

    [RelayCommand]
    private void Go() => Navigate(CurrentPath);

    [RelayCommand]
    private void Up() => Navigate(FolderBrowsing.ParentOf(CurrentPath));

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedFolder is { } folder)
        {
            Navigate(folder);
        }
    }

    /// <summary>进入某个目录：路径写回输入框，列出子目录。</summary>
    public void Navigate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().TrimEnd('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Error = $"路径无效：{path}";
            return;
        }

        if (!Directory.Exists(full))
        {
            Error = $"目录不存在：{full}";
            return;
        }

        CurrentPath = full;
        Error = string.Empty;
        SelectedFolder = null;

        Folders.Clear();
        try
        {
            foreach (string child in FolderBrowsing.Subfolders(full))
            {
                Folders.Add(child);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Error = $"这个目录读不全：{ex.Message}";
        }

        SelectedDrive = Drives.FirstOrDefault(drive => full.StartsWith(drive, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>要返回给主窗口的目录：选了子目录就用子目录，否则用当前目录。</summary>
    public string ResolveTarget() => SelectedFolder ?? CurrentPath;
}
