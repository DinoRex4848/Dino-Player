using System.Collections.Immutable;
using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DinoPlayer.MP.Playback;
using System.ComponentModel;
using DinoPlayer.MP.Settings;

namespace DinoPlayer.MP.Export;

public sealed partial class ExportView : UserControl, IDisposable
{
    private const string FourKResolution = "3840x2160 (4K)";
    private static readonly string[] Resolutions = { "Source", "1280x720", "1920x1080", "2560x1440", FourKResolution };
    private static readonly string[] VideoCodecs = { "H.264", "H.265", "AV1", "Copy" };
    private static readonly string[] AudioCodecs = { "AAC", "Opus", "FLAC", "Copy" };
    private static readonly string[] LossyAudioBitrates = { "128k", "192k", "256k", "320k" };
    private ExportContext _context = new();
    private readonly ObservableCollection<string> _presetNames = new();
    private readonly ObservableCollection<QueueItem> _queueItems = new();
    private readonly DispatcherTimer _refreshTimer;
    private ExportManager? _manager;
    private bool _suppressPresetSelection;
    private bool _loadingPreset;
    private bool _defaultsInitialized;
    private bool _refreshing;
    private string? _selectedLog;
    private Guid? _selectedLogJob;
    private string _lastSourcePath = "";
    private string _videoBitrate = "8";
    private string _videoQuality = "23";
    private bool _showingQuality;
    private bool _updatingRateControl;

    private string SelectedResolution => ResolutionCombo.SelectedItem as string == FourKResolution
        ? "3840x2160" : ResolutionCombo.SelectedItem as string ?? "Source";

    public ExportView()
    {
        InitializeComponent();
        VideoRateRow.SizeChanged += (_, _) => UpdatePairedRow(VideoRateRow, VideoRateField, "2*,3*");
        OutputFolderRow.SizeChanged += (_, _) => UpdatePairedRow(OutputFolderRow, OverwriteCheck, "*,Auto");
        QueueLayout.LayoutUpdated += (_, _) => UpdateQueueMinimumHeight();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => RefreshQueue()) { IsEnabled = false };
        QueueList.ItemsSource = _queueItems;
        QueueList.SelectionChanged += (_, _) => { if (!_refreshing) RefreshQueue(); };
        SourceText.Text = "Open a video in the Video player tab to prepare an export.";
        ContainerCombo.ItemsSource = new[] { "MP4", "MKV" };
        ContainerCombo.SelectedIndex = 0;
        ContainerCombo.SelectionChanged += (_, _) => MarkPresetDirty();
        OutputNameBox.LostFocus += (_, _) => OutputNameBox.Text = ExportDestination.NormalizeName(OutputNameBox.Text);
        ResolutionCombo.ItemsSource = Resolutions;
        ResolutionCombo.SelectedIndex = 0;
        VideoCodecCombo.ItemsSource = VideoCodecs;
        VideoCodecCombo.SelectedIndex = 0;
        AudioCodecCombo.ItemsSource = AudioCodecs;
        AudioCodecCombo.SelectedIndex = 0;
        VideoPresetCombo.ItemsSource = GetCpuPresets("H.264");
        VideoPresetCombo.SelectedItem = "medium";
        VideoRateControlCombo.ItemsSource = new[] { "Bitrate", "Target quality" };
        VideoRateControlCombo.SelectedIndex = 0;
        AudioBitrateCombo.ItemsSource = LossyAudioBitrates;
        AudioBitrateCombo.SelectedItem = "192k";
        PresetCombo.ItemsSource = _presetNames;
        PresetCombo.SelectionChanged += PresetComboOnSelectionChanged;
        SavePresetButton.Click += SavePresetOnClick;
        DeletePresetButton.Click += DeletePresetOnClick;
        VideoCodecCombo.SelectionChanged += (_, _) => { RefreshVideoPresets(); MarkPresetDirty(); };
        VideoGpuCheck.IsCheckedChanged += (_, _) => { RefreshVideoPresets(); MarkPresetDirty(); };
        ResolutionCombo.SelectionChanged += (_, _) => MarkPresetDirty();
        VideoBitrateBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _updatingRateControl) return;
            if (_showingQuality) _videoQuality = VideoBitrateBox.Text ?? "";
            else _videoBitrate = VideoBitrateBox.Text ?? "";
            MarkPresetDirty();
        };
        VideoRateControlCombo.SelectionChanged += (_, _) => { RefreshRateControl(); MarkPresetDirty(); };
        VideoPresetCombo.SelectionChanged += (_, _) => MarkPresetDirty();
        AudioCodecCombo.SelectionChanged += (_, _) => { RefreshAudioBitrates(); MarkPresetDirty(); };
        AudioBitrateCombo.SelectionChanged += (_, _) => MarkPresetDirty();
        KeepTracksSeparateCheck.IsCheckedChanged += (_, _) => MarkPresetDirty();
        IgnoreVolumeCheck.IsCheckedChanged += (_, _) => MarkPresetDirty();
        VideoAdvancedToggle.IsCheckedChanged += (_, _) =>
        {
            VideoSimplePanel.IsVisible = VideoAdvancedToggle.IsChecked != true;
            VideoAdvancedBox.IsVisible = VideoAdvancedToggle.IsChecked == true;
            MarkPresetDirty();
            RefreshSpeedExportAvailability();
        };
        AudioAdvancedToggle.IsCheckedChanged += (_, _) =>
        {
            AudioSimplePanel.IsVisible = AudioAdvancedToggle.IsChecked != true;
            AudioAdvancedBox.IsVisible = AudioAdvancedToggle.IsChecked == true;
            MarkPresetDirty();
            RefreshSpeedExportAvailability();
        };
        VideoAdvancedBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { MarkPresetDirty(); RefreshSpeedExportAvailability(); } };
        AudioAdvancedBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { MarkPresetDirty(); RefreshSpeedExportAvailability(); } };
        BrowseOutputButton.Click += BrowseOutputOnClick;
        CopyPlaybackSpeedCheck.IsCheckedChanged += (_, _) => RefreshSpeedExportAvailability();
        RefreshSpeedExportAvailability();
        RefreshRateControl();

        RefreshPresetNames();
    }

    private static void UpdatePairedRow(Grid row, Control secondField, string wideColumns)
    {
        var stack = row.Bounds.Width < 540;
        if (Avalonia.Controls.Grid.GetRow(secondField) == (stack ? 1 : 0)) return;
        row.ColumnDefinitions = new ColumnDefinitions(stack ? "*" : wideColumns);
        row.RowDefinitions = new RowDefinitions(stack ? "Auto,Auto" : "Auto");
        Avalonia.Controls.Grid.SetColumn(secondField, stack ? 0 : 1);
        Avalonia.Controls.Grid.SetRow(secondField, stack ? 1 : 0);
    }

    private void UpdateQueueMinimumHeight()
    {
        // Only the surrounding controls determine the small-window fallback.
        // Queue length must never increase the list's viewport or move its actions.
        var controlsHeight = QueueLayout.RowDefinitions.Where((_, index) => index != 3).Sum(row => row.ActualHeight);
        var minimum = controlsHeight + QueueLayout.RowSpacing * (QueueLayout.RowDefinitions.Count - 1) + QueueList.MinHeight;
        if (Math.Abs(QueueLayout.MinHeight - minimum) > 0.5) QueueLayout.MinHeight = minimum;
    }

    internal void Attach(ExportManager manager)
    {
        _manager = manager;
        _refreshTimer.Start();
        RefreshQueue();
    }

    internal void SetContext(ExportContext context)
    {
        // Clearing a hidden context does not consume first-use defaults.
        if (IsVisible) EnsureDefaultsInitialized();
        var changedSource = !string.Equals(_lastSourcePath, context.SourcePath, StringComparison.Ordinal);
        _context = new ExportContext
        {
            SourcePath = context.SourcePath, CropEnabled = context.CropEnabled,
            Start = context.Start, End = context.End, Duration = context.Duration, PlaybackSpeed = context.PlaybackSpeed,
            Tracks = context.Tracks.Select(track => new AudioTrack
            {
                Id = track.Id, SourceIndex = track.SourceIndex, AudioOrdinal = track.AudioOrdinal,
                Label = track.Label, Enabled = track.Enabled, VolumePercent = track.VolumePercent
            }).ToArray()
        };
        SourceText.Text = string.IsNullOrWhiteSpace(context.SourcePath)
            ? "Open a video in the Video player tab to prepare an export." : BuildSourceDescription();
        if (changedSource && !string.IsNullOrWhiteSpace(context.SourcePath))
        {
            var directory = SettingsStore.GetDefaultOutputFolder();
            if (!string.IsNullOrWhiteSpace(OutputFolderBox.Text)) directory = OutputFolderBox.Text;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                directory = Path.GetDirectoryName(context.SourcePath) ?? Environment.CurrentDirectory;
            OutputFolderBox.Text = directory;
            OutputNameBox.Text = Path.GetFileNameWithoutExtension(context.SourcePath) + "_export";
        }
        if (!string.IsNullOrWhiteSpace(context.SourcePath)) _lastSourcePath = context.SourcePath;
        RefreshSpeedExportAvailability();
        RefreshQueue();
    }

    internal event Action<ExportManagerSnapshot>? StatusUpdated;

    private void EnsureDefaultsInitialized()
    {
        if (_defaultsInitialized) return;
        _defaultsInitialized = true;
        var name = SettingsStore.GetDefaultExportPresetName();
        RefreshPresetNames(name);
        if (!string.IsNullOrWhiteSpace(name) && SettingsStore.FindExportPreset(name) is { } preset)
            ApplyPreset(preset);
    }

    internal bool TryAddToQueue(ExportContext context, out string message)
    {
        EnsureDefaultsInitialized();
        SetContext(context);
        return TrySubmit(start: false, out message);
    }

    private string BuildSourceDescription()
    {
        var crop = HasCrop
            ? $"Crop {FormatSeconds(_context.Start)} → {FormatSeconds(_context.End)} ({FormatSeconds(_context.End - _context.Start)})"
            : $"Full duration {FormatSeconds(_context.Duration)}";
        var enabled = _context.Tracks.Count(track => track.Enabled);
        return $"Source: {Path.GetFileName(_context.SourcePath)}  •  {crop}  •  {enabled} audio track(s) selected";
    }

    private bool HasCrop => _context.CropEnabled;

    private void RefreshVideoPresets()
    {
        var codec = VideoCodecCombo.SelectedItem as string ?? "H.264";
        var copy = codec == "Copy";
        VideoGpuCheck.IsEnabled = !copy;
        ResolutionCombo.IsEnabled = !copy;
        VideoBitrateBox.IsEnabled = !copy;
        VideoRateControlCombo.IsEnabled = !copy;
        VideoPresetCombo.IsEnabled = !copy;
        if (copy)
        {
            VideoGpuCheck.IsChecked = false;
            ResolutionCombo.SelectedIndex = 0;
            VideoPresetCombo.ItemsSource = new[] { "Source" };
            VideoPresetCombo.SelectedIndex = 0;
            RefreshRateControl();
            RefreshSpeedExportAvailability();
            return;
        }

        var presets = VideoGpuCheck.IsChecked == true
            ? new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" }
            : GetCpuPresets(codec);
        VideoPresetCombo.ItemsSource = presets;
        VideoPresetCombo.SelectedItem = VideoGpuCheck.IsChecked == true ? "p4" : presets[0] == "1" ? "8" : "medium";
        RefreshRateControl();
        RefreshSpeedExportAvailability();
    }

    private void RefreshRateControl()
    {
        _updatingRateControl = true;
        try
        {
            _showingQuality = VideoRateControlCombo.SelectedIndex == 1;
            VideoBitrateBox.Text = _showingQuality ? _videoQuality : _videoBitrate;
            var gpu = VideoGpuCheck.IsChecked == true;
            VideoRateLabel.Text = _showingQuality ? $"Quality ({(gpu ? "CQ" : "CRF")})" : "Bitrate (Mbps)";
            var (minimum, maximum) = VideoRateControl.QualityRange(VideoCodecCombo.SelectedItem as string ?? "H.264", gpu);
            var tip = _showingQuality
                ? $"Enter a whole number from {minimum} to {maximum}. Lower values increase quality; output file size varies." +
                  (gpu ? " NVENC quality 0 means automatic and is not a target quality." : "")
                : "Target video bitrate in megabits per second. Target quality lets the bitrate vary to preserve quality.";
            ToolTip.SetTip(VideoBitrateBox, tip);
            ToolTip.SetTip(VideoRateControlCombo, tip);
        }
        finally { _updatingRateControl = false; }
    }

    private void RefreshAudioBitrates()
    {
        var codec = AudioCodecCombo.SelectedItem as string ?? "AAC";
        var copy = codec == "Copy";
        AudioBitrateCombo.IsEnabled = !copy && codec != "FLAC";
        KeepTracksSeparateCheck.IsEnabled = !copy;
        IgnoreVolumeCheck.IsEnabled = !copy;
        AudioBitrateCombo.ItemsSource = copy ? new[] { "Source" } : codec == "FLAC" ? new[] { "lossless" } : LossyAudioBitrates;
        AudioBitrateCombo.SelectedIndex = 0;
        if (!copy && codec != "FLAC")
            AudioBitrateCombo.SelectedItem = "192k";
        RefreshSpeedExportAvailability();
    }

    private void RefreshSpeedExportAvailability()
    {
        CopyPlaybackSpeedCheck.IsEnabled = ExportCommandBuilder.CanCopySpeed(CaptureOptions());
        if (!CopyPlaybackSpeedCheck.IsEnabled)
            CopyPlaybackSpeedCheck.IsChecked = false;
        CopyPlaybackSpeedCheck.Content = $"Copy playback speed ({_context.PlaybackSpeed:0.##}×)";
    }

    private static string[] GetCpuPresets(string codec) =>
        codec == "AV1"
            ? new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" }
            : new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };

    private void RefreshPresetNames(string? selectedName = null)
    {
        _suppressPresetSelection = true;
        try
        {
            _presetNames.Clear();
            _presetNames.Add("— Manual —");
            foreach (var preset in SettingsStore.Current.ExportPresets)
                _presetNames.Add(preset.Name);

            var index = selectedName is null ? 0 : _presetNames.IndexOf(selectedName);
            PresetCombo.SelectedIndex = index < 0 ? 0 : index;
        }
        finally
        {
            _suppressPresetSelection = false;
        }
    }

    private void MarkPresetDirty()
    {
        if (_loadingPreset || _suppressPresetSelection || PresetCombo.SelectedIndex <= 0)
            return;

        _suppressPresetSelection = true;
        PresetCombo.SelectedIndex = 0;
        _suppressPresetSelection = false;
    }

    private ExportPreset CapturePreset(string name) => new()
    {
        Name = name,
        Container = ContainerCombo.SelectedItem as string ?? "MP4",
        Resolution = SelectedResolution,
        VideoCodecName = VideoCodecCombo.SelectedItem as string ?? "H.264",
        UseGpu = VideoGpuCheck.IsChecked == true,
        VideoPreset = VideoPresetCombo.SelectedItem as string ?? "medium",
        VideoBitrate = _videoBitrate,
        VideoRateControl = _showingQuality ? "Quality" : "Bitrate",
        VideoQuality = _videoQuality,
        AudioCodecName = AudioCodecCombo.SelectedItem as string ?? "AAC",
        AudioBitrate = AudioBitrateCombo.SelectedItem as string ?? "192k",
        KeepTracksSeparate = KeepTracksSeparateCheck.IsChecked == true,
        IgnoreVolume = IgnoreVolumeCheck.IsChecked == true,
        VideoAdvanced = VideoAdvancedToggle.IsChecked == true,
        VideoAdvancedText = VideoAdvancedBox.Text ?? string.Empty,
        AudioAdvanced = AudioAdvancedToggle.IsChecked == true,
        AudioAdvancedText = AudioAdvancedBox.Text ?? string.Empty
    };

    private void ApplyPreset(ExportPreset preset)
    {
        _loadingPreset = true;
        try
        {
            ContainerCombo.SelectedItem = preset.Container == "MKV" ? "MKV" : "MP4";
            VideoCodecCombo.SelectedItem = VideoCodecs.Contains(preset.VideoCodecName) ? preset.VideoCodecName : "H.264";
            RefreshVideoPresets();
            if (VideoCodecCombo.SelectedItem as string != "Copy")
            {
                VideoGpuCheck.IsChecked = preset.UseGpu;
                RefreshVideoPresets();
                ResolutionCombo.SelectedItem = preset.Resolution == "3840x2160" ? FourKResolution
                    : Resolutions.Contains(preset.Resolution) ? preset.Resolution : "Source";
                VideoPresetCombo.SelectedItem = VideoPresetCombo.Items.Cast<object>()
                    .Any(item => string.Equals(item?.ToString(), preset.VideoPreset, StringComparison.Ordinal))
                    ? preset.VideoPreset
                    : VideoPresetCombo.Items.Cast<object>().FirstOrDefault();
            }
            _videoBitrate = string.IsNullOrWhiteSpace(preset.VideoBitrate) ? "8" : preset.VideoBitrate;
            _videoQuality = string.IsNullOrWhiteSpace(preset.VideoQuality) ? "23" : preset.VideoQuality;
            VideoRateControlCombo.SelectedIndex = preset.VideoRateControl == "Quality" ? 1 : 0;
            RefreshRateControl();

            AudioCodecCombo.SelectedItem = AudioCodecs.Contains(preset.AudioCodecName) ? preset.AudioCodecName : "AAC";
            RefreshAudioBitrates();
            if (AudioCodecCombo.SelectedItem as string != "Copy")
            {
                AudioBitrateCombo.SelectedItem = AudioBitrateCombo.Items.Cast<object>()
                    .Any(item => string.Equals(item?.ToString(), preset.AudioBitrate, StringComparison.Ordinal))
                    ? preset.AudioBitrate
                    : AudioBitrateCombo.Items.Cast<object>().FirstOrDefault();
                KeepTracksSeparateCheck.IsChecked = preset.KeepTracksSeparate;
                IgnoreVolumeCheck.IsChecked = preset.IgnoreVolume;
            }

            VideoAdvancedBox.Text = preset.VideoAdvancedText;
            VideoAdvancedToggle.IsChecked = preset.VideoAdvanced;
            AudioAdvancedBox.Text = preset.AudioAdvancedText;
            AudioAdvancedToggle.IsChecked = preset.AudioAdvanced;
        }
        finally
        {
            _loadingPreset = false;
        }
    }

    private void PresetComboOnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressPresetSelection || PresetCombo.SelectedIndex <= 0 ||
            PresetCombo.SelectedItem is not string name)
            return;

        if (SettingsStore.FindExportPreset(name) is { } preset)
            ApplyPreset(preset);
    }

    private async void SavePresetOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var dialog = new ExportPresetNameDialog();
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (SettingsStore.FindExportPreset(name) is not null)
        {
            var overwrite = await new ConfirmDialog(
                $"A preset named \"{name}\" already exists.\n\nDo you want to overwrite it?")
                .ShowDialog<bool>(owner);
            if (!overwrite)
                return;
        }

        try
        {
            SettingsStore.AddOrUpdateExportPreset(CapturePreset(name));
            RefreshPresetNames(name);
            StatusText.Text = $"Preset '{name}' saved.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save preset: {exception.Message}";
        }
    }

    private void DeletePresetOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (PresetCombo.SelectedIndex <= 0 || PresetCombo.SelectedItem is not string name)
            return;

        try
        {
            if (SettingsStore.DeleteExportPreset(name))
            {
                RefreshPresetNames();
                StatusText.Text = $"Preset '{name}' deleted.";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not delete preset: {exception.Message}";
        }
    }

    private async void BrowseOutputOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose export folder", AllowMultiple = false,
                SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(OutputFolderBox.Text ?? "")
            });
            ApplyOutputFolderSelection(result.FirstOrDefault()?.TryGetLocalPath());
        }
        catch (Exception exception) { StatusText.Text = $"Could not browse for an export folder: {exception.Message}"; }
    }

    internal void ApplyOutputFolderSelection(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder)) OutputFolderBox.Text = folder;
    }

    private ExportOptions CaptureOptions() => new()
    {
        SourcePath = _context.SourcePath,
        // Validation happens on submission; availability checks can run before
        // a source or destination has been selected.
        OutputPath = "",
        CropEnabled = _context.CropEnabled,
        Start = _context.Start, End = _context.End, Duration = _context.Duration,
        PlaybackSpeed = _context.PlaybackSpeed,
        Tracks = _context.Tracks.Select(t => new ExportAudioTrack(t.SourceIndex, t.AudioOrdinal, t.Enabled, t.VolumePercent)).ToImmutableArray(),
        Resolution = SelectedResolution,
        VideoCodec = VideoCodecCombo.SelectedItem as string ?? "H.264",
        VideoPreset = VideoPresetCombo.SelectedItem as string ?? "medium",
        VideoBitrate = _videoBitrate,
        VideoRateControl = _showingQuality ? "Quality" : "Bitrate",
        VideoQuality = _videoQuality,
        UseGpu = VideoGpuCheck.IsChecked == true,
        AudioCodec = AudioCodecCombo.SelectedItem as string ?? "AAC",
        AudioBitrate = AudioBitrateCombo.SelectedItem as string ?? "192k",
        KeepTracksSeparate = KeepTracksSeparateCheck.IsChecked == true,
        IgnoreVolume = IgnoreVolumeCheck.IsChecked == true,
        VideoAdvanced = VideoAdvancedToggle.IsChecked == true,
        AudioAdvanced = AudioAdvancedToggle.IsChecked == true,
        VideoAdvancedText = VideoAdvancedBox.Text ?? "",
        AudioAdvancedText = AudioAdvancedBox.Text ?? "",
        CopyPlaybackSpeed = CopyPlaybackSpeedCheck.IsChecked == true,
        Overwrite = OverwriteCheck.IsChecked == true
    };

    private void StartExportOnClick(object? sender, RoutedEventArgs eventArgs) => TrySubmit(start: true, out _);
    private void AddToQueueOnClick(object? sender, RoutedEventArgs eventArgs) => TrySubmit(start: false, out _);

    private bool TrySubmit(bool start, out string message)
    {
        try
        {
            if (_manager is null) throw new InvalidOperationException("The export queue is unavailable.");
            OutputNameBox.Text = ExportDestination.NormalizeName(OutputNameBox.Text);
            var options = CaptureOptions() with
            {
                OutputPath = ExportDestination.Build(OutputNameBox.Text, OutputFolderBox.Text, ContainerCombo.SelectedItem as string ?? "MP4")
            };
            var id = start ? _manager.StartExport(options) : _manager.AddToQueue(options);
            RefreshQueue();
            QueueList.SelectedItem = _queueItems.First(item => item.Id == id);
            var job = _manager.GetSnapshot().Jobs.First(item => item.Id == id);
            message = $"{(start ? "Export scheduled" : "Added to queue")}: {Path.GetFileName(job.Definition.Options.OutputPath)}";
            StatusText.Text = message;
            return true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            StatusText.Text = message;
            return false;
        }
        finally { RefreshQueue(); }
    }

    private void StartQueueOnClick(object? sender, RoutedEventArgs eventArgs) => RunAction(() => _manager?.StartQueue());
    private void PauseQueueOnClick(object? sender, RoutedEventArgs eventArgs) => RunAction(() => _manager?.PauseAfterCurrent());
    private void ResumeQueueOnClick(object? sender, RoutedEventArgs eventArgs) => RunAction(() => _manager?.ResumeQueue());
    private async void CancelExportOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_manager is not null) await _manager.CancelCurrentAsync();
        RefreshQueue();
    }
    private async void CancelAllOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_manager is not null) await _manager.CancelAllAsync();
        RefreshQueue();
    }
    private void RemoveJobOnClick(object? sender, RoutedEventArgs eventArgs) => RunAction(() =>
    {
        if (QueueList.SelectedItem is QueueItem item) _manager?.Remove(item.Id);
    });
    private void MoveUpOnClick(object? sender, RoutedEventArgs eventArgs) => MoveSelected(-1);
    private void MoveDownOnClick(object? sender, RoutedEventArgs eventArgs) => MoveSelected(1);
    private void MoveSelected(int direction) => RunAction(() =>
    {
        if (QueueList.SelectedItem is QueueItem item) _manager?.Move(item.Id, direction);
    });
    private void RetryJobOnClick(object? sender, RoutedEventArgs eventArgs) => RunAction(() =>
    {
        if (QueueList.SelectedItem is QueueItem item) _manager?.Retry(item.Id);
    });
    private void ClearFinishedOnClick(object? sender, RoutedEventArgs eventArgs) => RunAction(() => _manager?.ClearFinished());

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        RefreshQueue();
    }

    private void RefreshQueue()
    {
        if (_manager is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = _manager.GetSnapshot();
            var selection = (QueueList.SelectedItem as QueueItem)?.Id;
            var byId = _queueItems.ToDictionary(item => item.Id);
            for (var index = 0; index < snapshot.Jobs.Length; index++)
            {
                var job = snapshot.Jobs[index];
                if (!byId.TryGetValue(job.Id, out var item)) item = new QueueItem(job);
                item.Update(job, snapshot.QueuePaused);
                var existing = _queueItems.IndexOf(item);
                if (existing < 0) _queueItems.Insert(index, item);
                else if (existing != index) _queueItems.Move(existing, index);
            }
            var remaining = snapshot.Jobs.Select(job => job.Id).ToHashSet();
            foreach (var item in _queueItems.Where(item => !remaining.Contains(item.Id)).ToArray()) _queueItems.Remove(item);
            if (selection is { } selectedId) QueueList.SelectedItem = _queueItems.FirstOrDefault(item => item.Id == selectedId);
            var selected = QueueList.SelectedItem as QueueItem;
            var pending = snapshot.Jobs.Where(job => job.IsPending).ToArray();
            var pendingIndex = selected is null ? -1 : Array.FindIndex(pending, job => job.Id == selected.Id);
            var canAct = !snapshot.IsStopping;
            var hasSource = !string.IsNullOrWhiteSpace(_context.SourcePath) && _context.Duration > 0;
            StartExportButton.IsEnabled = AddToQueueButton.IsEnabled = canAct && hasSource;
            StartExportButton.Content = snapshot.IsRunning ? "Add after current export" : "Start export";
            if (snapshot.QueuePaused && snapshot.Active?.IsQueueJob == true) StartExportButton.Content = "Add to paused queue";
            StartQueueButton.IsEnabled = canAct && snapshot.WaitingCount > 0 && !snapshot.QueuePaused;
            PauseQueueButton.IsVisible = !snapshot.QueuePaused;
            PauseQueueButton.IsEnabled = canAct && snapshot.QueueStarted;
            ResumeQueueButton.IsVisible = snapshot.QueuePaused;
            ResumeQueueButton.IsEnabled = canAct && snapshot.QueuePaused;
            CancelButton.IsEnabled = canAct && snapshot.Active?.State == ExportJobState.Exporting;
            CancelAllButton.IsEnabled = canAct && snapshot.HasUnfinished;
            RemoveJobButton.IsEnabled = canAct && selected is not null && !selected.Job.IsActive;
            RetryJobButton.IsEnabled = canAct && selected?.Job.State is ExportJobState.Failed or ExportJobState.Cancelled;
            MoveUpButton.IsEnabled = canAct && pendingIndex > 0;
            MoveDownButton.IsEnabled = canAct && pendingIndex >= 0 && pendingIndex < pending.Length - 1;
            ClearFinishedButton.IsEnabled = canAct && snapshot.Jobs.Any(job => job.IsFinished);
            ExportProgress.Value = snapshot.Active?.Progress ?? 0;
            ProgressText.Text = snapshot.Active is { } active
                ? $"{(active.State == ExportJobState.Cancelling ? "Cancelling" : "Exporting")} {Path.GetFileName(active.Definition.Options.OutputPath)} · {active.Progress:0}%"
                : snapshot.IsStopping ? "Stopping exports…" : snapshot.QueuePaused ? "Queue paused" : "No export running";
            QueueSummaryText.Text = $"{snapshot.WaitingCount} waiting · {snapshot.Jobs.Count(job => job.State == ExportJobState.Completed)} completed · {snapshot.FailedCount} failed";
            if (snapshot.QueuePaused && snapshot.Active is not null) ProgressText.Text += " · pausing after this clip";
            SelectedJobText.Text = selected is null ? "Select a clip to see its details and FFmpeg log." : Describe(selected.Job);
            var log = selected?.Job.Log ?? "";
            var changedJob = _selectedLogJob != selected?.Id;
            if (changedJob || !string.Equals(_selectedLog, log, StringComparison.Ordinal))
            {
                var scroller = LogBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                var offset = scroller?.Offset ?? default;
                var follow = changedJob || scroller is null || offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 2;
                _selectedLogJob = selected?.Id;
                _selectedLog = log;
                LogBox.Text = log;
                if (follow) LogBox.CaretIndex = log.Length;
                if (scroller is not null)
                {
                    var logJob = _selectedLogJob;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_selectedLog != log || _selectedLogJob != logJob) return;
                        if (follow) scroller.Offset = new Vector(offset.X, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
                        else scroller.Offset = offset;
                    }, DispatcherPriority.Loaded);
                }
            }
            StatusUpdated?.Invoke(snapshot);
        }
        finally { _refreshing = false; }
    }

    private static string Describe(ExportJobSnapshot job)
    {
        var options = job.Definition.Options;
        var crop = options.CropEnabled ? $"{FormatSeconds(options.Start)} → {FormatSeconds(options.End)}" : "Full video";
        return $"Source: {options.SourcePath}\nOutput: {options.OutputPath}\n{crop} · {options.Tracks.Count(track => track.Enabled)} audio track(s) · {(options.CopyPlaybackSpeed ? options.PlaybackSpeed : 1):0.##}×\n" +
            $"Video: {(options.VideoAdvanced ? options.VideoAdvancedText : options.VideoCodec)} · Audio: {(options.AudioAdvanced ? options.AudioAdvancedText : options.AudioCodec)}" +
            (job.Definition.Command.Warning is { } warning ? $"\n{warning}" : "") +
            (job.Error is { } error ? $"\n{error}" : "");
    }

    private sealed class QueueItem(ExportJobSnapshot job) : INotifyPropertyChanged
    {
        public Guid Id => Job.Id;
        public ExportJobSnapshot Job { get; private set; } = job;
        public string Name => Path.GetFileName(Job.Definition.Options.OutputPath);
        public string OutputPath => Job.Definition.Options.OutputPath;
        public string Summary => $"{Path.GetFileName(Job.Definition.Options.SourcePath)} · {(Job.Definition.Options.CropEnabled ? $"{FormatSeconds(Job.Definition.Options.Start)} → {FormatSeconds(Job.Definition.Options.End)}" : "Full video")}";
        public string Status { get; private set; } = "Waiting";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(ExportJobSnapshot snapshot, bool paused)
        {
            Job = snapshot;
            var status = snapshot.State switch
            {
                ExportJobState.Waiting => "Waiting for Start queue",
                ExportJobState.Scheduled when paused && snapshot.IsQueueJob => "Paused",
                ExportJobState.Scheduled => "Scheduled",
                ExportJobState.Exporting => $"Exporting · {snapshot.Progress:0}%",
                _ => snapshot.State.ToString()
            };
            if (Status == status) return;
            Status = status;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }

    private static string FormatSeconds(double seconds)
    {
        var time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        var fraction = time.Milliseconds == 0 ? "" : $".{time.Milliseconds:000}".TrimEnd('0');
        return (time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}" : $"{time.Minutes:D2}:{time.Seconds:D2}") + fraction;
    }

    public void Dispose() => _refreshTimer.Stop();
}
