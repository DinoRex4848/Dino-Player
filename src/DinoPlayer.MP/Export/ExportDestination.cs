namespace DinoPlayer.MP.Export;

internal static class ExportDestination
{
    public static string NormalizeName(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            value = value[..^4].TrimEnd();
        return value;
    }

    public static string Build(string? name, string? folder, string container)
    {
        var value = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("Enter a file name without a folder path.");
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose an export folder.");
        return Path.Combine(folder.Trim(), value + (container == "MKV" ? ".mkv" : ".mp4"));
    }
}
