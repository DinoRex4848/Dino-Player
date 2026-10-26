using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DinoPlayer.MP.Export;

public sealed partial class ExportPresetNameDialog : Window
{
    public ExportPresetNameDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private void SaveOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var name = NameBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            Close(name);
    }

    private void CancelOnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
