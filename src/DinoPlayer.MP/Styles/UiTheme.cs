namespace DinoPlayer.MP;

internal static class UiTheme
{
    public static T Resource<T>(string key)
    {
        if (Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is T resource)
            return resource;
        throw new InvalidOperationException($"Theme resource '{key}' is missing or has the wrong type.");
    }
}
