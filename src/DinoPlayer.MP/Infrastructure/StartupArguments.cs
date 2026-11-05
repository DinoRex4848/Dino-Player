namespace DinoPlayer.MP;

internal static class StartupArguments
{
    /// <summary>
    /// The video to open from the command line: the first argument that is a
    /// path or a file:// URI (file managers can pass either). Options are skipped.
    /// </summary>
    public static string? FindMediaPath(IEnumerable<string>? args)
    {
        foreach (var argument in args ?? [])
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith('-')) continue;
            if (argument.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.IsFile) return uri.LocalPath;
                continue;
            }
            return Path.GetFullPath(argument);
        }
        return null;
    }
}
