using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DinoPlayer.MP.Export;

public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string message, string affirmative = "Yes", string negative = "No", bool defaultToNo = false) : this()
    {
        MessageText.Text = message;
        YesButton.Content = affirmative;
        NoButton.Content = negative;
        YesButton.IsDefault = !defaultToNo;
        NoButton.IsDefault = defaultToNo;
    }

    private void YesOnClick(object? sender, RoutedEventArgs eventArgs) => Close(true);

    private void NoOnClick(object? sender, RoutedEventArgs eventArgs) => Close(false);
}
