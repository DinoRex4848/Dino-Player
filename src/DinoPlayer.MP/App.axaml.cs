using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DinoPlayer.MP;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var iconPath = OperatingSystem.IsWindows() ? "AppIcon.ico" : "AppIcon.png";
        using var iconStream = AssetLoader.Open(new Uri($"avares://DinoPlayer.MP/Assets/{iconPath}"));
        if (OperatingSystem.IsWindows())
        {
            Resources["AppIcon"] = new WindowIcon(iconStream);
        }
        else
        {
            // X11's icon loader limits both dimensions to 128. Pad the portrait
            // logo to a square first so it keeps its proportions when loaded.
            const int size = 128;
            using var logo = new Bitmap(iconStream);
            using var icon = new RenderTargetBitmap(new PixelSize(size, size));
            var scale = size / (double)Math.Max(logo.PixelSize.Width, logo.PixelSize.Height);
            var width = logo.PixelSize.Width * scale;
            var height = logo.PixelSize.Height * scale;
            using (var drawing = icon.CreateDrawingContext())
                drawing.DrawImage(logo, new Rect((size - width) / 2, (size - height) / 2, width, height));
            Resources["AppIcon"] = new WindowIcon(icon);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            if (StartupArguments.FindMediaPath(desktop.Args) is { } path) window.OpenOnStartup(path);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
