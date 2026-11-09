using Avalonia.Controls;
using Avalonia.Input;
using TrophyShots.App.ViewModels;

namespace TrophyShots.App.Views;

/// <summary>自带目录选择器，返回值是选中的目录（取消为 null）。</summary>
public partial class DirectoryPickerWindow : Window
{
    public DirectoryPickerWindow()
        : this(null)
    {
    }

    public DirectoryPickerWindow(string? startPath)
    {
        InitializeComponent();
        var viewModel = new DirectoryPickerViewModel(startPath);
        DataContext = viewModel;

        FolderList.DoubleTapped += (_, _) => viewModel.OpenSelectedCommand.Execute(null);
        FolderList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                viewModel.OpenSelectedCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void OnConfirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is DirectoryPickerViewModel viewModel)
        {
            Close(viewModel.ResolveTarget());
        }
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}
