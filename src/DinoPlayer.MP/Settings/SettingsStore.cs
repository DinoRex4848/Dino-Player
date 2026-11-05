using System.Globalization;
using System.Text.Json;
using DinoPlayer.MP.Export;

namespace DinoPlayer.MP.Settings;

public static class SettingsStore
{
    private static SettingsRepository? _repository;
    private static SettingsRepository Repository => _repository ??= new(SettingsPath);
    public static AppSettings Current => Repository.Snapshot;
    public static string SettingsPath => RuntimePaths.SettingsPath;

    public static void Initialize()
    {
        _repository = new SettingsRepository(SettingsPath);
        AppLog.Info($"Settings path: {SettingsPath}");
    }

    public static string GetDefaultExportPresetName() => Current.General.DefaultExportPresetName;
    public static string GetDefaultOutputFolder() => Current.General.DefaultOutputFolder;
    public static PlaybackShortcutSettings GetPlaybackShortcuts() => Repository.GetPlaybackShortcuts();
    public static ExportPreset? FindExportPreset(string name) => Current.ExportPresets.FirstOrDefault(
        p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    public static AudioTrackPreset? FindAudioTrackPreset(int count) => Current.Audio.TrackPresets.FirstOrDefault(p => p.TrackCount == count);

    public static void AddOrUpdateExportPreset(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Name)) throw new ArgumentException("A preset needs a name.");
        if (!preset.VideoAdvanced && preset.VideoCodecName != ExportNames.Copy)
            VideoRateControl.Validate(preset.VideoRateControl, preset.VideoBitrate, preset.VideoQuality,
                preset.VideoTargetSize, preset.VideoCodecName, preset.UseGpu);
        Repository.Update(settings =>
        {
            var index = settings.ExportPresets.FindIndex(p => string.Equals(p.Name, preset.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0) settings.ExportPresets.Add(preset);
            else settings.ExportPresets[index] = preset;
        });
    }

    public static bool DeleteExportPreset(string name)
    {
        var removed = false;
        Repository.Update(settings =>
        {
            removed = settings.ExportPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (string.Equals(settings.General.DefaultExportPresetName, name, StringComparison.OrdinalIgnoreCase))
                settings.General.DefaultExportPresetName = "";
        });
        return removed;
    }

    public static void AddOrUpdateAudioTrackPreset(AudioTrackPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.TrackCount <= 0 || preset.Tracks is null || preset.Tracks.Count != preset.TrackCount ||
            preset.Tracks.Any(t => t is null || !double.IsFinite(t.VolumePercent)))
            throw new ArgumentException("An audio preset must contain one valid setting per track.");
        Repository.Update(settings =>
        {
            var index = settings.Audio.TrackPresets.FindIndex(p => p.TrackCount == preset.TrackCount);
            if (index < 0) settings.Audio.TrackPresets.Add(preset);
            else settings.Audio.TrackPresets[index] = preset;
        });
    }

    public static void SavePreferences(string? presetName, string? folder, PlaybackShortcutSettings shortcuts,
        bool? loopByDefault = null, bool? showDiagnosticLog = null, bool? showPlayerQueueButton = null,
        bool? editableCropTimes = null) =>
        Repository.SavePreferences(presetName, folder, shortcuts, loopByDefault, showDiagnosticLog, showPlayerQueueButton, editableCropTimes);
}

internal sealed class SettingsRepository
{
    internal const int CurrentVersion = 6;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
    };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Action<string, string> _write;
    private AppSettings _current;

    public SettingsRepository(string path, Action<string, string>? write = null)
    {
        _path = path;
        _write = write ?? WriteAtomic;
        _current = Load(path);
    }

    public AppSettings Snapshot { get { lock (_sync) return Clone(_current); } }

    public PlaybackShortcutSettings GetPlaybackShortcuts()
    {
        lock (_sync)
        {
            var shortcuts = _current.PlaybackShortcuts;
            return new PlaybackShortcutSettings
            {
                SpeedUpKey = shortcuts.SpeedUpKey, SpeedUpModifiers = shortcuts.SpeedUpModifiers,
                SpeedDownKey = shortcuts.SpeedDownKey, SpeedDownModifiers = shortcuts.SpeedDownModifiers
            };
        }
    }

    public void Update(Action<AppSettings> update)
    {
        lock (_sync)
        {
            if (_current.Version > CurrentVersion)
                throw new InvalidOperationException($"Settings version {_current.Version} is newer than this application supports; the file is read-only.");
            var candidate = Clone(_current);
            update(candidate);
            // Clone again to detach any objects supplied by callers.
            candidate = Clone(candidate);
            Normalize(candidate);
            _write(_path, JsonSerializer.Serialize(candidate, JsonOptions));
            _current = candidate;
        }
    }

    public void SavePreferences(string? presetName, string? folder, PlaybackShortcutSettings shortcuts,
        bool? loopByDefault = null, bool? showDiagnosticLog = null, bool? showPlayerQueueButton = null,
        bool? editableCropTimes = null)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        if (!ValidShortcuts(shortcuts)) throw new ArgumentException("Choose distinct, valid playback shortcuts that are not reserved.");
        var name = presetName?.Trim() ?? "";
        var path = string.IsNullOrWhiteSpace(folder) ? "" : Path.GetFullPath(folder.Trim());
        Update(settings =>
        {
            var preset = settings.ExportPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (name.Length > 0 && preset is null) throw new ArgumentException($"Export preset '{name}' does not exist.");
            settings.General.DefaultExportPresetName = preset?.Name ?? "";
            settings.General.DefaultOutputFolder = path;
            if (loopByDefault is { } loop) settings.General.LoopByDefault = loop;
            if (showDiagnosticLog is { } diagnosticLog) settings.General.ShowDiagnosticLog = diagnosticLog;
            if (showPlayerQueueButton is { } queueButton) settings.General.ShowPlayerQueueButton = queueButton;
            if (editableCropTimes is { } cropTimes) settings.General.EditableCropTimes = cropTimes;
            settings.PlaybackShortcuts = shortcuts;
        });
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    private static AppSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new();
            Normalize(settings);
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            if (exception is not FileNotFoundException and not DirectoryNotFoundException)
                AppLog.Warn($"Could not read settings; using defaults. {exception.Message}");
            return new();
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.Version <= CurrentVersion) settings.Version = CurrentVersion;
        settings.General ??= new();
        settings.Audio ??= new();
        settings.PlaybackShortcuts ??= new();
        settings.General.DefaultOutputFolder ??= "";
        settings.General.DefaultExportPresetName ??= "";
        settings.ExportPresets = (settings.ExportPresets ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        foreach (var preset in settings.ExportPresets)
        {
            preset.Name = preset.Name.Trim();
            preset.Container = preset.Container == ExportNames.Mkv ? ExportNames.Mkv : ExportNames.Mp4;
            preset.Resolution = ExportNames.IsFixedResolution(preset.Resolution) ? preset.Resolution : ExportNames.SourceResolution;
            preset.VideoCodecName = preset.VideoCodecName is ExportNames.H265 or ExportNames.Av1 or ExportNames.Copy ? preset.VideoCodecName : ExportNames.H264;
            preset.AudioCodecName = preset.AudioCodecName is ExportNames.Opus or ExportNames.Flac or ExportNames.Copy ? preset.AudioCodecName : ExportNames.Aac;
            preset.VideoPreset ??= "medium";
            if (!double.TryParse(preset.VideoBitrate, NumberStyles.Float, CultureInfo.InvariantCulture, out var bitrate) ||
                !double.IsFinite(bitrate) || bitrate <= 0) preset.VideoBitrate = "8";
            preset.VideoRateControl = preset.VideoRateControl is ExportNames.Quality or ExportNames.Size
                ? preset.VideoRateControl : ExportNames.Bitrate;
            if (!TargetSize.TryParseMegabytes(preset.VideoTargetSize, out _)) preset.VideoTargetSize = TargetSize.DefaultMegabytes;
            if (!VideoRateControl.IsValidQuality(preset.VideoQuality, preset.VideoCodecName, preset.UseGpu))
                preset.VideoQuality = "23";
            preset.AudioBitrate ??= "192k";
            preset.VideoAdvancedText ??= "";
            preset.AudioAdvancedText ??= "";
        }
        var defaultPreset = settings.ExportPresets.FirstOrDefault(p => string.Equals(p.Name,
            settings.General.DefaultExportPresetName, StringComparison.OrdinalIgnoreCase));
        settings.General.DefaultExportPresetName = defaultPreset?.Name ?? "";
        settings.Audio.TrackPresets = (settings.Audio.TrackPresets ?? []).Where(p => p is not null && p.TrackCount > 0 &&
            p.Tracks is not null && p.Tracks.Count == p.TrackCount && p.Tracks.All(t => t is not null && double.IsFinite(t.VolumePercent)))
            .GroupBy(p => p.TrackCount).Select(g => g.First()).ToList();
        foreach (var track in settings.Audio.TrackPresets.SelectMany(p => p.Tracks))
            track.VolumePercent = Math.Clamp(track.VolumePercent, 0, 150);
        if (!ValidShortcuts(settings.PlaybackShortcuts)) settings.PlaybackShortcuts = new();
    }

    private static bool ValidShortcuts(PlaybackShortcutSettings shortcuts)
    {
        static bool Allowed(string key, string modifiers) =>
            PlaybackShortcut.TryParse(key, modifiers, out var parsed, out _) && !PlaybackShortcut.IsReserved(parsed);
        return Allowed(shortcuts.SpeedUpKey, shortcuts.SpeedUpModifiers) && Allowed(shortcuts.SpeedDownKey, shortcuts.SpeedDownModifiers) &&
               !PlaybackShortcut.Conflicts(shortcuts.SpeedUpKey, shortcuts.SpeedUpModifiers, shortcuts.SpeedDownKey, shortcuts.SpeedDownModifiers);
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
