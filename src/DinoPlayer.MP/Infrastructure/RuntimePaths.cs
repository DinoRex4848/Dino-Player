namespace DinoPlayer.MP;

internal static class RuntimePaths
{
    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "settings.json"));

    public static string SettingsPath => ResolveSettingsPath(AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string ResolveSettingsPath(string appDirectory, string? configHome, string userHome)
    {
        var portable = Path.Combine(appDirectory, "settings.json");
        if (File.Exists(portable)) return portable;
        var root = !string.IsNullOrWhiteSpace(configHome) ? configHome : Path.Combine(userHome, ".config");
        return Path.Combine(root, "DinoPlayer", "settings.json");
    }

    public static string DataDirectory
    {
        get
        {
            if (IsPortable) return Path.Combine(AppContext.BaseDirectory, "data");
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(dataHome) ? dataHome :
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "DinoPlayer", "MP");
        }
    }
}
