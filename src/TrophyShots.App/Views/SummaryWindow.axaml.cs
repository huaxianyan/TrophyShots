using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TrophyShots.App.Views;

public partial class SummaryWindow : Window
{
    public SummaryWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
