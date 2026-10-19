namespace DinoPlayer.MP.Playback;

internal static class EmbeddedInputBridge
{
    public static string GetPath()
    {
        using var resource = typeof(EmbeddedInputBridge).Assembly.GetManifestResourceStream("DinoPlayer.MP.MpvInputBridge.lua")
            ?? throw new InvalidOperationException("The embedded mpv input bridge is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var contents = buffer.ToArray();
        var buildId = typeof(EmbeddedInputBridge).Module.ModuleVersionId.ToString("N");
        var directory = Path.Combine(RuntimePaths.DataDirectory, "cache");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"mpv-input-{buildId}.lua");
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(contents))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, contents);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return path;
    }
}
