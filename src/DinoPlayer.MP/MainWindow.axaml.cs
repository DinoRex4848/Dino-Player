using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DinoPlayer.MP.Controls;
using DinoPlayer.MP.Export;
using DinoPlayer.MP.Playback;
using DinoPlayer.MP.Settings;

namespace DinoPlayer.MP;

public sealed partial class MainWindow : Window
{
    private const double PixelsPerSecond = 2.0;
    private const double TimelineWheelPixelsPerStep = 80;
    private const double FullscreenControlBarHeight = 56;
    private const double FullscreenControlBottomMargin = 16;
    private const double FullscreenPointerRevealZoneRatio = 0.20;
    private const double FullscreenControlWidthRatio = 0.80;
    private const double FullscreenControlMinimumWidth = 320;
    private static readonly TimeSpan FullscreenCursorHideDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FullscreenScrollControlsGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PlayerQueueStatusHoldDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PlayerQueueStatusFadeDuration = TimeSpan.FromMilliseconds(400);
    private static readonly Cursor FullscreenHiddenCursor = new(StandardCursorType.None);
    private static readonly Thickness NormalRootMargin = new(10);

    private readonly ExportManager _exportManager;
    private bool _exportTabSelected;
    private bool _updatingTabs;
    private bool _confirmingClose;
    private (IPlaybackEngine Player, long Generation)? _tabResumePlayback;
    private (IPlaybackEngine Player, long Generation, bool Paused)? _preparingMedia;
    private int _playbackUiUpdateQueued;
    private bool _isPlayerQueueStatusFading;
    private bool _pickingMedia;
    private bool _preferencesOpen;
    private readonly UiLogPump _logPump;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _mixTimer;
    private readonly DispatcherTimer _audioPresetTimer;
    private readonly DispatcherTimer _playerQueueStatusTimer;
    private readonly DispatcherTimer _fullscreenControlsTimer;
    private readonly DispatcherTimer _fullscreenCursorTimer;
    private readonly DispatcherTimer _fullscreenScrollGraceTimer;

    private IPlaybackEngine? _player;
    private Task _playerShutdown = Task.CompletedTask;
    private bool _closing;
    private bool _allowClose;
    private bool _closed;
    private long _hostGeneration;
    private PlaybackSnapshot? _resumeSnapshot;
    private string? _currentFilePath;
    private bool _timelineSized;
    private double _durationSeconds;
    private double _currentPos;
    private double _cropStart;
    private double _cropEnd;
    private Rectangle? _playhead;
    private Rectangle? _cropHighlight;
    private Thumb? _cropStartThumb;
    private Thumb? _cropEndThumb;
    private readonly Dictionary<int, (Rectangle Line, TextBlock Label)> _timelineMarkers = new();
    private readonly Stack<(Rectangle Line, TextBlock Label)> _markerPool = new();
    private DateTimeOffset _lastStatsLog = DateTimeOffset.MinValue;
    private bool _isFullscreen;
    private bool _isApplyingFullscreenSliderValue;
    private bool _isFullscreenTimelinePointerDown;
    private bool _isFullscreenVolumePointerDown;
    private bool _isApplyingMasterVolume;
    private double _lastPositiveMasterVolume = 100;
    private bool _fullscreenScrollGraceActive;
    private bool _isPointerInFullscreenRevealZone;
    private Cursor? _cursorBeforeFullscreen;
    private Cursor? _videoHostCursorBeforeFullscreen;
    private Cursor? _fullscreenControlsBarCursorBeforeFullscreen;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;

    public MainWindow() : this(null)
    {
    }

    internal MainWindow(ExportManager.RunExport? exportRunner)
    {
        InitializeComponent();
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _exportManager = new ExportManager(exportRunner, playerSource: () => _currentFilePath);
        var general = SettingsStore.Current.General;
        DiagnosticLogExpander.IsVisible = general.ShowDiagnosticLog;
        PlayerAddToQueueButton.IsVisible = general.ShowPlayerQueueButton;
        _logPump = new UiLogPump(LogTextBox);
        TimelineScroller.AddHandler(PointerWheelChangedEvent, TimelineScrollerOnPointerWheelChanged,
            RoutingStrategies.Tunnel);

        LogPathText.Text = $"Log: {AppLog.LogPath}";
        SetCropValues(0, 0);
        ApplyCropTimeEditingPreference(general.EditableCropTimes);

        VideoHost.HandleCreated += InitializePlayer;
        VideoHost.HandleDestroying += DisposePlayer;
        VideoHost.PointerInput += pointerEvent => PostPointerInput(pointerEvent);
        VideoBorder.SizeChanged += (_, _) => UpdateFullscreenPopupGeometry();
        SizeChanged += (_, _) => UpdateFullscreenPopupGeometry();
        WorkspaceGrid.SizeChanged += (_, _) => UpdateImportPrompt();
        Opened += (_, _) => UpdateImportPrompt();
        Activated += (_, _) =>
        {
            FocusPlayer();
        };
        PlayPauseButton.Click += (_, _) => RunPlayerAction(player => player.TogglePauseAsync());
        FullscreenButton.Click += (_, _) => ToggleFullscreen();
        FullscreenPlayPauseButton.Click += (_, _) =>
        {
            ShowFullscreenControls();
            RunPlayerAction(player => player.TogglePauseAsync());
        };
        FullscreenTimelineSlider.ValueChanged += FullscreenTimelineSliderOnValueChanged;
        FullscreenTimelineSlider.PointerPressed += FullscreenTimelineSliderOnPointerPressed;
        FullscreenTimelineSlider.PointerReleased += FullscreenTimelineSliderOnPointerReleased;
        FullscreenTimelineSlider.PointerCaptureLost += FullscreenTimelineSliderOnPointerCaptureLost;
        FullscreenControlsPopup.AddHandler(KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel);
        FullscreenControlsBar.AddHandler(KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel);
        FullscreenControlsBar.SizeChanged += (_, _) => UpdateFullscreenPopupGeometry();
        FullscreenControlsBar.AddHandler(PointerWheelChangedEvent, FullscreenControlsBarOnPointerWheelChanged,
            RoutingStrategies.Bubble, handledEventsToo: true);
        MuteAllButton.Click += (_, _) => ToggleMasterMute();
        SkipBackButton.Click += (_, _) => Skip(-5);
        SkipForwardButton.Click += (_, _) => Skip(5);
        JumpCropStartButton.Click += (_, _) => RunPlayerAction(player => player.SeekAsync(_cropStart));
        JumpCropEndButton.Click += (_, _) => RunPlayerAction(player => player.SeekAsync(_cropEnd));
        SetCropStartButton.Click += (_, _) => SetCropStartToCurrent();
        SetCropEndButton.Click += (_, _) => SetCropEndToCurrent();
        CropToggle.IsCheckedChanged += CropToggleOnCheckedChanged;
        MasterVolumeSlider.ValueChanged += MasterVolumeOnValueChanged;
        FullscreenMasterVolumeSlider.ValueChanged += MasterVolumeOnValueChanged;
        ConfigureMasterVolumeSlider(MasterVolumeSlider);
        ConfigureMasterVolumeSlider(FullscreenMasterVolumeSlider);
        CropStartBox.KeyDown += CropTimeBoxOnKeyDown;
        CropEndBox.KeyDown += CropTimeBoxOnKeyDown;
        CropStartBox.LostFocus += (_, _) => CommitCropTimeBox(CropStartBox);
        CropEndBox.LostFocus += (_, _) => CommitCropTimeBox(CropEndBox);
        WorkspaceTabs.SelectionChanged += async (_, _) =>
        {
            if (!_updatingTabs) await SelectWorkspaceTabAsync(WorkspaceTabs.SelectedIndex == 1);
        };
        ExportPanel.StatusUpdated += UpdateExportTabHeader;
        ExportPanel.Attach(_exportManager);
        AddHandler(KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel);
        AppLog.MessageWritten += OnLogMessage;

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, UpdatePlaybackUi);
        _mixTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background, ApplyPendingMix)
        {
            IsEnabled = false
        };
        _audioPresetTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, SavePendingAudioPreset)
        {
            IsEnabled = false
        };
        _playerQueueStatusTimer = new DispatcherTimer(PlayerQueueStatusHoldDuration, DispatcherPriority.Background, PlayerQueueStatusTimerOnTick)
        {
            IsEnabled = false
        };
        _fullscreenControlsTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, FullscreenControlsTimerOnTick)
        {
            IsEnabled = false
        };
        _fullscreenCursorTimer = new DispatcherTimer(FullscreenCursorHideDelay, DispatcherPriority.Background, FullscreenCursorTimerOnTick)
        {
            IsEnabled = false
        };
        _fullscreenScrollGraceTimer = new DispatcherTimer(FullscreenScrollControlsGracePeriod,
            DispatcherPriority.Background, FullscreenScrollGraceTimerOnTick)
        {
            IsEnabled = false
        };
    }

    private async void InitializePlayer(IntPtr handle, string descriptor)
    {
        var hostGeneration = ++_hostGeneration;
        if (_player is not null || _closing) return;
        IPlaybackEngine? player = null;
        try
        {
            await _playerShutdown;
            if (_closing || hostGeneration != _hostGeneration) return;
            player = new MpvPlaybackEngine(handle, descriptor);
            _player = player;
            player.MediaLoaded += generation => OnMediaLoaded(player, generation);
            player.PositionChanged += _ => QueuePlaybackUiUpdate();
            player.MediaEnded += generation => Dispatcher.UIThread.Post(() =>
            {
                if (IsCurrentMedia(player, generation)) UpdatePlaybackUi();
            });
            player.Error += error => Dispatcher.UIThread.Post(() =>
            {
                if (IsCurrentMedia(player, error.Generation))
                {
                    _preparingMedia = null;
                    ShowError(error.Message);
                }
            });
            player.PointerInput += input => PostPointerInput(input, player);
            await player.SetMasterVolumeAsync(MasterVolumeSlider.Value);
            await player.SetLoopingAsync(SettingsStore.Current.General.LoopByDefault);
            if (!ReferenceEquals(_player, player) || _closing) return;
            _uiTimer.Start();
            OpenMenuItem.IsEnabled = ImportPrompt.IsEnabled = true;
            UpdateImportPrompt();
            FocusPlayer();
            FullscreenMasterVolumeSlider.IsEnabled = true;
            AppLog.Info("Playback component initialized and ready.");
            if (_currentFilePath is { } path) LoadVideoCore(path, restorePlayback: true);
        }
        catch (Exception exception)
        {
            if (player is not null)
            {
                if (ReferenceEquals(_player, player)) _player = null;
                await player.StopAsync();
            }
            if (!_closing && hostGeneration == _hostGeneration)
            {
                AppLog.Error($"Playback initialization failed: {exception}");
                ShowError(exception.Message);
            }
        }
    }

    private bool IsCurrentMedia(IPlaybackEngine player, long generation) =>
        !_closing && ReferenceEquals(_player, player) && generation == player.Generation;

    private bool IsPreparingMedia(IPlaybackEngine player) =>
        _preparingMedia is { } preparing && ReferenceEquals(preparing.Player, player) && preparing.Generation == player.Generation;

    private void QueuePlaybackUiUpdate()
    {
        // A burst can generate both seek-start and restart events for each key.
        // Post at most one refresh and read the newest snapshot on the UI thread.
        if (Interlocked.Exchange(ref _playbackUiUpdateQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _playbackUiUpdateQueued, 0);
            if (!_closing && !_closed) UpdatePlaybackUi();
        }, DispatcherPriority.Input);
    }

    // Pointer input arrives from the Windows host bridge or the mpv Lua bridge,
    // off the UI thread. Input from a replaced engine is ignored.
    private void PostPointerInput(PlaybackPointerEvent pointerEvent, IPlaybackEngine? source = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || _closed || (source is not null && !ReferenceEquals(_player, source))) return;
            HandlePlaybackPointerInput(pointerEvent);
        }, DispatcherPriority.Input);

    private void HandlePlaybackPointerInput(PlaybackPointerEvent pointerEvent)
    {
        if (_exportTabSelected) return;
        if (pointerEvent.Kind == PlaybackPointerEventKind.DoubleClick)
        {
            var canToggleFullscreen = CanUseFullscreen || _isFullscreen;
            AppLog.Info($"Video double-click received: fullscreen={_isFullscreen}; fullscreen-available={canToggleFullscreen}");
            if (canToggleFullscreen)
                ToggleFullscreen();
            return;
        }

        // The native mpv surface owns pointer input, so a wheel event here
        // means the pointer is over the video play area in normal mode.
        if (!_isFullscreen && pointerEvent.Kind == PlaybackPointerEventKind.Wheel)
        {
            AdjustMasterVolume(pointerEvent.WheelDelta);
            return;
        }

        if (!_isFullscreen)
            return;

        if (pointerEvent.Kind is PlaybackPointerEventKind.Move or PlaybackPointerEventKind.Enter or PlaybackPointerEventKind.Wheel)
            ShowFullscreenCursor();

        if (pointerEvent.Kind == PlaybackPointerEventKind.Wheel)
        {
            UpdateFullscreenPointerRevealZone(pointerEvent.Y, pointerEvent.SurfaceHeight);
            BeginFullscreenScrollControlsGrace();
            AdjustMasterVolume(pointerEvent.WheelDelta);
            ShowFullscreenControls();
            return;
        }

        if (pointerEvent.Kind == PlaybackPointerEventKind.RightClick)
        {
            ShowFullscreenCursor();
            ShowFullscreenControls();
            RunPlayerAction(player => player.TogglePauseAsync());
            return;
        }

        if (pointerEvent.Kind is not (PlaybackPointerEventKind.Move or PlaybackPointerEventKind.Enter))
            return;

        var isInRevealZone = pointerEvent.SurfaceHeight > 0 &&
                             pointerEvent.Y >= pointerEvent.SurfaceHeight * (1 - FullscreenPointerRevealZoneRatio);
        _isPointerInFullscreenRevealZone = isInRevealZone;
        if (_fullscreenScrollGraceActive)
        {
            if (isInRevealZone)
                ShowFullscreenControls();
            return;
        }

        if (isInRevealZone)
            ShowFullscreenControls();
        else
            HideFullscreenControls();
    }

    private async void OpenButtonOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await PickVideoAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a video", AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video and media")
                    {
                        Patterns = new[] { "*.mkv", "*.mp4", "*.webm", "*.mov", "*.avi", "*.m4v", "*.ts" },
                        MimeTypes = new[] { "video/*", "application/x-matroska", "application/octet-stream" }
                    },
                    FilePickerFileTypes.All
                }
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        });
    }

    // One picker at a time; keep the import prompt in place while browsing.
    internal async Task PickVideoAsync(Func<Task<string?>> pickFile)
    {
        if (_player is null || _pickingMedia || _closing) return;
        _pickingMedia = true;
        try
        {
            if (await pickFile() is { } path) LoadVideo(path);
        }
        catch (Exception exception) { ShowError($"Could not browse for a video: {exception.Message}"); }
        finally { _pickingMedia = false; UpdateImportPrompt(); }
    }

    private void UpdateImportPrompt()
    {
        // Visibility does not detach the native host or recreate its player.
        VideoHost.IsVisible = !string.IsNullOrWhiteSpace(_currentFilePath);
        var show = IsVisible && !_closing && !_closed && !_preferencesOpen &&
                   !_isFullscreen && string.IsNullOrWhiteSpace(_currentFilePath) && WorkspaceGrid.Bounds.Width > 0;
        var wasVisible = ImportPrompt.IsVisible;
        ImportPrompt.IsVisible = show;
        if (show && !wasVisible) FocusPlayer();
    }

    // A neutral focus target keeps playback keys out of buttons, sliders and native popups.
    private void FocusPlayer() => Dispatcher.UIThread.Post(() =>
    {
        if (IsActive && !_exportTabSelected && !_preferencesOpen && !_closing) RootGrid.Focus();
    }, DispatcherPriority.Loaded);

    private void LoadVideo(string path) => LoadVideoCore(path, restorePlayback: false);

    private async void LoadVideoCore(string path, bool restorePlayback)
    {
        if (_player is null)
            return;

        _tabResumePlayback = null;
        await SelectWorkspaceTabAsync(false);
        var player = _player;
        long generation = player.Generation;
        try
        {
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("The selected media file does not exist.", path);
            FlushAudioPreset();
            _mixTimer.Stop();
            AudioTracksPanel.IsEnabled = false;
            AudioTracksPanel.Children.Clear();
            MuteAllButton.IsEnabled = false;
            var fullPath = System.IO.Path.GetFullPath(path);
            if (!restorePlayback) _resumeSnapshot = null;
            _currentFilePath = fullPath;
            MediaNameText.Text = System.IO.Path.GetFileName(_currentFilePath);
            ErrorBanner.IsVisible = false;
            HidePlayerQueueStatus();
            UpdateImportPrompt();
            SetMediaControlsEnabled(false);
            ResetTimeline();
            CropToggle.IsChecked = false;
            ExportPanel.SetContext(new());
            var snapshot = player.GetSnapshot();
            var paused = restorePlayback && _resumeSnapshot is { } resume ? resume.IsPaused :
                IsPreparingMedia(player) ? _preparingMedia!.Value.Paused : snapshot.IsLoaded && snapshot.IsPaused;
            var opening = player.OpenAsync(_currentFilePath, startPaused: true);
            generation = player.Generation;
            _preparingMedia = (player, generation, paused);
            await opening;
        }
        catch (Exception exception)
        {
            if (IsCurrentMedia(player, generation))
            {
                _preparingMedia = null;
                AppLog.Error($"Could not open media: {exception}");
                ShowError(exception.Message);
            }
        }
    }

    private async void ExportButtonOnClick(object? sender, RoutedEventArgs eventArgs) =>
        await SelectWorkspaceTabAsync(true);

    private void ExitMenuItemOnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void PlayerAddToQueueOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!PlayerAddToQueueButton.IsEnabled || _closing || _closed) return;
        if (ExportPanel.TryAddToQueue(CaptureExportContext(), out var message))
        {
            ShowPlayerQueueStatus(message);
            ErrorBanner.IsVisible = false;
        }
        else
        {
            HidePlayerQueueStatus();
            ShowError(message);
        }
    }

    private void ShowPlayerQueueStatus(string message)
    {
        _playerQueueStatusTimer.Stop();
        _isPlayerQueueStatusFading = false;
        PlayerQueueStatusText.Text = message;
        PlayerQueueStatusText.IsVisible = true;
        PlayerQueueStatusText.Opacity = 1;
        _playerQueueStatusTimer.Interval = PlayerQueueStatusHoldDuration;
        _playerQueueStatusTimer.Start();
    }

    private void PlayerQueueStatusTimerOnTick(object? sender, EventArgs eventArgs)
    {
        _playerQueueStatusTimer.Stop();
        if (_isPlayerQueueStatusFading)
        {
            HidePlayerQueueStatus();
            return;
        }

        _isPlayerQueueStatusFading = true;
        PlayerQueueStatusText.Opacity = 0;
        _playerQueueStatusTimer.Interval = PlayerQueueStatusFadeDuration;
        _playerQueueStatusTimer.Start();
    }

    private void HidePlayerQueueStatus()
    {
        _playerQueueStatusTimer.Stop();
        _isPlayerQueueStatusFading = false;
        PlayerQueueStatusText.IsVisible = false;
        PlayerQueueStatusText.Opacity = 0;
    }

    private ExportContext CaptureExportContext()
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath) || _durationSeconds <= 0)
            return new();
        return new ExportContext
        {
            SourcePath = _currentFilePath,
            CropEnabled = CropToggle.IsChecked == true,
            Start = CropToggle.IsChecked == true ? _cropStart : 0,
            End = CropToggle.IsChecked == true ? _cropEnd : 0,
            Duration = _durationSeconds,
            PlaybackSpeed = _player.GetSnapshot().PlaybackSpeed,
            Tracks = _player.AudioTracks.ToArray()
        };
    }

    private async Task SelectWorkspaceTabAsync(bool export)
    {
        if (_closing || _closed) return;
        if (export && _isFullscreen) SetFullscreen(false);
        var changed = _exportTabSelected != export;
        _exportTabSelected = export;
        _updatingTabs = true;
        try { WorkspaceTabs.SelectedIndex = export ? 1 : 0; }
        finally { _updatingTabs = false; }
        VideoBorder.IsVisible = !export;
        PlayerSidebar.IsVisible = !export && !_isFullscreen;
        NormalPlaybackArea.IsVisible = !export && !_isFullscreen;
        ExportPanel.IsVisible = export;
        if (export) ExportPanel.SetContext(CaptureExportContext());
        else FocusPlayer();
        if (!changed) return;
        try
        {
            if (export && _player is { } player)
            {
                var snapshot = player.GetSnapshot();
                _tabResumePlayback = !snapshot.IsPaused && snapshot.IsLoaded ? (player, player.Generation) : null;
                await player.SetPausedAsync(true);
            }
            else if (!export && _tabResumePlayback is { } resume)
            {
                _tabResumePlayback = null;
                if (IsCurrentMedia(resume.Player, resume.Generation)) await resume.Player.SetPausedAsync(false);
            }
        }
        catch (Exception exception)
        {
            if (!_closing) AppLog.Warn($"Could not change playback state when switching tabs: {exception.Message}");
        }
    }

    private void UpdateExportTabHeader(ExportManagerSnapshot snapshot)
    {
        PlayerAddToQueueButton.IsEnabled = !_closing && !_closed && !snapshot.IsStopping &&
            _player is not null && PlayPauseButton.IsEnabled;
        var details = new List<string>();
        if (snapshot.Active is { } active) details.Add($"{active.Progress:0}%");
        if (snapshot.WaitingCount > 0) details.Add($"{snapshot.WaitingCount} waiting");
        if (snapshot.QueuePaused) details.Add("paused");
        if (snapshot.FailedCount > 0) details.Add($"{snapshot.FailedCount} failed");
        var text = details.Count == 0 ? "Export" : $"Export · {string.Join(" · ", details)}";
        if (!Equals(ExportTabHeader.Content, text)) ExportTabHeader.Content = text;
    }

    private async void PreferencesButtonOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _preferencesOpen = true;
        ImportPrompt.IsVisible = false;
        try
        {
            if (await new PreferencesWindow().ShowDialog<bool>(this)) ApplyPreferences();
        }
        finally { _preferencesOpen = false; UpdateImportPrompt(); }
    }

    internal void ApplyPreferences()
    {
        var general = SettingsStore.Current.General;
        DiagnosticLogExpander.IsVisible = general.ShowDiagnosticLog;
        PlayerAddToQueueButton.IsVisible = general.ShowPlayerQueueButton;
        ApplyCropTimeEditingPreference(general.EditableCropTimes);
        if (!general.ShowPlayerQueueButton) HidePlayerQueueStatus();
        if (!general.ShowDiagnosticLog) DiagnosticLogExpander.IsExpanded = false;
        RunPlayerAction(player => player.SetLoopingAsync(general.LoopByDefault));
    }

    private void ApplyCropTimeEditingPreference(bool editable)
    {
        foreach (var box in new[] { CropStartBox, CropEndBox })
        {
            if (!editable && box.IsFocused)
            {
                CommitCropTimeBox(box);
                RootGrid.Focus();
            }
            box.IsReadOnly = !editable;
            // These local values override the player's non-focusable control styles.
            box.Focusable = box.IsTabStop = editable;
            box.IsVisible = editable;
        }
        CropStartText.IsVisible = CropEndText.IsVisible = !editable;
    }

    private void SetMediaControlsEnabled(bool enabled)
    {
        PlayPauseButton.IsEnabled = FullscreenPlayPauseButton.IsEnabled = enabled;
        TimelineCanvas.IsEnabled = FullscreenTimelineSlider.IsEnabled = enabled;
        FullscreenButton.IsEnabled = CropToggle.IsEnabled = enabled;
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = enabled;
        PlayerAddToQueueButton.IsEnabled = enabled && !_closing && !_exportManager.GetSnapshot().IsStopping;
    }

    private void OnMediaLoaded(IPlaybackEngine player, long generation)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsCurrentMedia(player, generation)) return;
            try
            {
                var paused = IsPreparingMedia(player) ? _preparingMedia!.Value.Paused : player.GetSnapshot().IsPaused;
                RestoreAudioPreset();
                BuildAudioTrackControls();
                UpdatePlaybackUi();
                await player.ApplyAudioMixAsync();
                if (!IsCurrentMedia(player, generation)) return;
                var resume = _resumeSnapshot;
                _resumeSnapshot = null;
                // Audio reconfiguration may rebuild mpv's playback pipeline.
                // Rewind only after it is applied, while the import is paused.
                await player.SeekAndWaitAsync(resume?.PositionSeconds ?? 0);
                if (!IsCurrentMedia(player, generation)) return;
                paused = resume?.IsPaused ?? paused;
                if (_exportTabSelected)
                {
                    ExportPanel.SetContext(CaptureExportContext());
                    _tabResumePlayback = !paused ? (player, generation) : null;
                    paused = true;
                }
                await player.SetPausedAsync(paused);
                if (!IsCurrentMedia(player, generation)) return;
                _preparingMedia = null;
                AudioTracksPanel.IsEnabled = true;
                UpdatePlaybackUi();
                SetMediaControlsEnabled(_durationSeconds > 0);
                UpdateImportPrompt();
            }
            catch (Exception exception)
            {
                if (IsCurrentMedia(player, generation))
                {
                    _preparingMedia = null;
                    AppLog.Error($"Could not finish preparing media: {exception}");
                    ShowError(exception.Message);
                }
            }
        });
    }

    private void BuildAudioTrackControls()
    {
        AudioTracksPanel.Children.Clear();
        var tracks = _player?.AudioTracks ?? Array.Empty<AudioTrack>();
        MuteAllButton.IsEnabled = tracks.Count > 0;

        if (tracks.Count == 0)
        {
            AudioTracksPanel.Children.Add(new TextBlock
            {
                Text = "No audio tracks discovered. Video remains available and audio is muted.",
                Foreground = UiTheme.Resource<IBrush>("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var track in tracks)
        {
            var enabled = new CheckBox { Content = track.Label, IsChecked = track.Enabled };
            enabled.IsCheckedChanged += (_, _) =>
            {
                track.Enabled = enabled.IsChecked == true;
                QueueMixUpdate();
                QueueAudioPresetSave();
            };

            var volumeValue = new TextBlock
            {
                Text = $"{track.VolumePercent:0}%",
                Width = 42,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var volume = new Slider { Minimum = 0, Maximum = 150, Value = track.VolumePercent, TickFrequency = 5 };
            volume.ValueChanged += (_, args) =>
            {
                track.VolumePercent = args.NewValue;
                volumeValue.Text = $"{track.VolumePercent:0}%";
                QueueMixUpdate();
                QueueAudioPresetSave();
            };

            VolumeSliderBehavior.Attach(volume);

            var volumeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            volumeRow.Children.Add(volume);
            Grid.SetColumn(volumeValue, 1);
            volumeRow.Children.Add(volumeValue);

            var trackPanel = new StackPanel { Spacing = 3 };
            trackPanel.Children.Add(enabled);
            trackPanel.Children.Add(volumeRow);
            AudioTracksPanel.Children.Add(trackPanel);
        }
    }

    private void ToggleMasterMute()
    {
        if (_player is null)
            return;

        MasterVolumeSlider.Value = MasterVolumeSlider.Value > 0 ? 0 : _lastPositiveMasterVolume;
    }

    private void RestoreAudioPreset()
    {
        var tracks = _player?.AudioTracks;
        if (tracks is null || tracks.Count == 0)
            return;

        var preset = SettingsStore.FindAudioTrackPreset(tracks.Count);
        if (preset is null)
            return;

        if (preset.Tracks is null || preset.TrackCount != tracks.Count || preset.Tracks.Count != tracks.Count)
        {
            AppLog.Warn($"Ignoring audio preset for {tracks.Count} tracks because its settings are missing or have the wrong count.");
            return;
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var savedTrack = preset.Tracks[index];
            if (savedTrack is null || !double.IsFinite(savedTrack.VolumePercent))
            {
                AppLog.Warn($"Ignoring audio preset for {tracks.Count} tracks because track {index + 1} has an invalid volume.");
                return;
            }
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var savedTrack = preset.Tracks[index];
            tracks[index].Enabled = savedTrack.Enabled;
            tracks[index].VolumePercent = Math.Clamp(savedTrack.VolumePercent, 0, 150);
        }

        AppLog.Info($"Restored audio preset for {tracks.Count} tracks.");
    }

    private void QueueAudioPresetSave()
    {
        _audioPresetTimer.Stop();
        if (_player?.AudioTracks.Count > 0)
            _audioPresetTimer.Start();
    }

    private void SavePendingAudioPreset(object? sender, EventArgs eventArgs) => FlushAudioPreset();

    private void FlushAudioPreset()
    {
        _audioPresetTimer.Stop();
        SaveCurrentAudioPreset();
    }

    private void SaveCurrentAudioPreset()
    {
        var tracks = _player?.AudioTracks;
        if (tracks is null || tracks.Count == 0)
            return;

        try
        {
            SettingsStore.AddOrUpdateAudioTrackPreset(new AudioTrackPreset
            {
                TrackCount = tracks.Count,
                Tracks = tracks.Select(track => new AudioTrackSetting
                {
                    Enabled = track.Enabled,
                    VolumePercent = Math.Clamp(track.VolumePercent, 0, 150)
                }).ToList()
            });
            AppLog.Info($"Saved audio preset for {tracks.Count} tracks.");
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Could not save audio preset: {exception.Message}");
        }
    }

    private void QueueMixUpdate()
    {
        _mixTimer.Stop();
        _mixTimer.Start();
    }

    private void ApplyPendingMix(object? sender, EventArgs eventArgs)
    {
        _mixTimer.Stop();
        RunPlayerAction(player => player.ApplyAudioMixAsync());
    }

    private void ConfigureMasterVolumeSlider(Slider volume)
    {
        void RefreshFullscreenControls()
        {
            if (_isFullscreen)
            {
                ShowFullscreenCursor();
                ShowFullscreenControls();
            }
        }

        VolumeSliderBehavior.Attach(volume,
            pointerActivity: RefreshFullscreenControls,
            dragChanged: dragging =>
            {
                if (ReferenceEquals(volume, FullscreenMasterVolumeSlider))
                    _isFullscreenVolumePointerDown = dragging;
                RefreshFullscreenControls();
            },
            wheelChanged: () =>
            {
                if (_isFullscreen)
                {
                    _isPointerInFullscreenRevealZone = true;
                    BeginFullscreenScrollControlsGrace();
                }
                RefreshFullscreenControls();
            });
    }

    private void MasterVolumeOnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_isApplyingMasterVolume)
            return;

        var value = Math.Clamp(eventArgs.NewValue, MasterVolumeSlider.Minimum, MasterVolumeSlider.Maximum);
        _isApplyingMasterVolume = true;
        try
        {
            MasterVolumeSlider.Value = value;
            FullscreenMasterVolumeSlider.Value = value;
        }
        finally
        {
            _isApplyingMasterVolume = false;
        }

        MasterVolumeText.Text = $"{value:0}%";
        FullscreenMasterVolumeText.Text = $"{value:0}%";
        if (value > 0) _lastPositiveMasterVolume = value;
        MuteAllButton.IsChecked = value == 0;
        var muteAction = value == 0 ? $"Restore master volume ({_lastPositiveMasterVolume:0}%)" : "Mute master volume";
        ToolTip.SetTip(MuteAllButton, muteAction);
        AutomationProperties.SetName(MuteAllButton, muteAction);
        RunPlayerAction(player => player.SetMasterVolumeAsync(value));
    }

    private void Skip(double seconds)
    {
        if (_player is null)
            return;

        if (_isFullscreen)
        {
            ShowFullscreenControls();
            BeginFullscreenScrollControlsGrace();
        }
        RunPlayerAction(player => player.SeekRelativeAsync(seconds));
    }

    private void MainWindowOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        // The popup's event route can also pass through the main window.
        if (eventArgs.Handled) return;
        if (ImportPrompt.IsVisible)
        {
            if (eventArgs.Key is Key.Enter or Key.Space)
            {
                eventArgs.Handled = true;
                OpenButtonOnClick(sender, new RoutedEventArgs());
            }
            return;
        }
        if (_exportTabSelected) return;
        var shortcuts = SettingsStore.GetPlaybackShortcuts();
        if (eventArgs.Key == Key.F11)
        {
            ToggleFullscreen();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape && _isFullscreen)
        {
            SetFullscreen(false);
            eventArgs.Handled = true;
        }
        else if (_player is { } player && IsPreparingMedia(player))
        {
            return;
        }
        else if (CropStartBox.IsFocused || CropEndBox.IsFocused)
        {
            return;
        }
        else if (PlaybackShortcut.Matches(shortcuts.SpeedUpKey,
                     shortcuts.SpeedUpModifiers, eventArgs))
        {
            if (_isFullscreen)
                ShowFullscreenControls();
            RunPlayerAction(player => player.AdjustPlaybackSpeedAsync(1));
            eventArgs.Handled = true;
        }
        else if (PlaybackShortcut.Matches(shortcuts.SpeedDownKey,
                     shortcuts.SpeedDownModifiers, eventArgs))
        {
            if (_isFullscreen)
                ShowFullscreenControls();
            RunPlayerAction(player => player.AdjustPlaybackSpeedAsync(-1));
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Space)
        {
            if (_isFullscreen)
                ShowFullscreenControls();
            RunPlayerAction(player => player.TogglePauseAsync());
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Left)
        {
            Skip(-5);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Right)
        {
            Skip(5);
            eventArgs.Handled = true;
        }
    }

    private void FullscreenControlsBar_PointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_isFullscreen)
        {
            _isPointerInFullscreenRevealZone = true;
            ShowFullscreenCursor();
            ShowFullscreenControls();
        }
    }

    private void FullscreenControlsBarOnPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (!_isFullscreen)
            return;

        ShowFullscreenCursor();
        // The volume slider handles its own wheel event. The bar also listens
        // to handled events so scrolling over the timeline or any other pill
        // control still adjusts the master volume.
        if (FullscreenMasterVolumeSlider.IsPointerOver)
            return;

        _isPointerInFullscreenRevealZone = true;
        BeginFullscreenScrollControlsGrace();
        AdjustMasterVolume(eventArgs.Delta.Y);
        ShowFullscreenControls();
        eventArgs.Handled = true;
    }

    private void AdjustMasterVolume(double delta)
    {
        var direction = Math.Sign(delta);
        if (direction == 0)
            return;

        MasterVolumeSlider.Value = Math.Clamp(
            Math.Round(MasterVolumeSlider.Value) + direction,
            MasterVolumeSlider.Minimum,
            MasterVolumeSlider.Maximum);
    }

    private void FullscreenTimelineSliderOnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!_isFullscreen)
            return;

        ShowFullscreenCursor();
        _isFullscreenTimelinePointerDown = true;
        ShowFullscreenControls();
    }

    private void FullscreenTimelineSliderOnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        _isFullscreenTimelinePointerDown = false;
        if (_isFullscreen)
            ShowFullscreenControls();
    }

    private void FullscreenTimelineSliderOnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs) =>
        _isFullscreenTimelinePointerDown = false;

    private void FullscreenTimelineSliderOnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_isApplyingFullscreenSliderValue || !_isFullscreen || _durationSeconds <= 0)
            return;

        ShowFullscreenControls();
        RunPlayerAction(player => player.SeekAsync(eventArgs.NewValue));
    }

    private bool CanUseFullscreen => !_exportTabSelected && _player is not null && _durationSeconds > 0 && !string.IsNullOrWhiteSpace(_currentFilePath);

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            SetFullscreen(false);
            return;
        }

        if (CanUseFullscreen)
            SetFullscreen(true);
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == _isFullscreen)
            return;

        if (fullscreen)
        {
            _windowStateBeforeFullscreen = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState;
            _cursorBeforeFullscreen = Cursor;
            _videoHostCursorBeforeFullscreen = VideoHost.Cursor;
            _fullscreenControlsBarCursorBeforeFullscreen = FullscreenControlsBar.Cursor;
            _isFullscreen = true;
            _fullscreenScrollGraceActive = false;
            _isPointerInFullscreenRevealZone = false;
            ApplyFullscreenLayout(true);
            WindowState = WindowState.FullScreen;
            SetFullscreenPlayerCursorAutohide(true);
            ShowFullscreenCursor();
            ShowFullscreenControls();
            FocusPlayer();
            return;
        }

        _isFullscreen = false;
        _fullscreenControlsTimer.Stop();
        _fullscreenCursorTimer.Stop();
        _fullscreenScrollGraceTimer.Stop();
        _fullscreenScrollGraceActive = false;
        _isPointerInFullscreenRevealZone = false;
        FullscreenControlsPopup.IsOpen = false;
        SetFullscreenPlayerCursorAutohide(false);
        RestoreFullscreenCursor();
        ApplyFullscreenLayout(false);
        if (WindowState == WindowState.FullScreen)
            WindowState = _windowStateBeforeFullscreen;
        FocusPlayer();
    }

    private void SetFullscreenPlayerCursorAutohide(bool enabled)
    {
        // On Windows the embedded mpv child window does not receive the same
        // pointer messages as the host. The host owns cursor visibility there;
        // letting mpv hide its own cursor would race with the native bridge.
        RunPlayerAction(player => player.SetCursorAutohideAsync(
            OperatingSystem.IsWindows() ? false : enabled));
    }

    private void ApplyFullscreenLayout(bool fullscreen)
    {
        RootGrid.Margin = fullscreen ? new Thickness(0) : NormalRootMargin;
        HeaderGrid.IsVisible = !fullscreen;
        WorkspaceTabs.IsVisible = !fullscreen;
        NormalPlaybackArea.IsVisible = !fullscreen && !_exportTabSelected;
        PlayerSidebar.IsVisible = !fullscreen && !_exportTabSelected;
        WorkspaceGrid.ColumnSpacing = fullscreen ? 0 : 12;
        WorkspaceGrid.RowSpacing = fullscreen ? 0 : 12;
        WorkspaceGrid.ColumnDefinitions[1].Width = fullscreen ? new GridLength(0) : new GridLength(320);
        VideoBorder.BorderThickness = fullscreen ? new Thickness(0) : new Thickness(1);
        VideoBorder.CornerRadius = fullscreen ? new CornerRadius(0) : UiTheme.Resource<CornerRadius>("PanelRadius");
        Grid.SetColumnSpan(VideoBorder, fullscreen ? 2 : 1);
        UpdateImportPrompt();
        if (fullscreen) Dispatcher.UIThread.Post(UpdateFullscreenPopupGeometry);
    }

    private void ShowFullscreenControls()
    {
        if (!_isFullscreen)
            return;

        FullscreenControlsPopup.IsOpen = true;
        UpdateFullscreenPopupGeometry();
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsTimer.Start();
    }

    private void HideFullscreenControls()
    {
        if (!_isFullscreen || _isFullscreenVolumePointerDown)
            return;

        _fullscreenControlsTimer.Stop();
        FullscreenControlsPopup.IsOpen = false;
    }

    private void FullscreenControlsTimerOnTick(object? sender, EventArgs eventArgs)
    {
        if (_isFullscreen && (FullscreenControlsBar.IsPointerOver || _isFullscreenTimelinePointerDown || _isFullscreenVolumePointerDown))
        {
            _fullscreenControlsTimer.Stop();
            _fullscreenControlsTimer.Start();
            return;
        }

        HideFullscreenControls();
    }

    private void UpdateFullscreenPointerRevealZone(double y, double surfaceHeight)
    {
        if (surfaceHeight > 0)
            _isPointerInFullscreenRevealZone = y >= surfaceHeight * (1 - FullscreenPointerRevealZoneRatio);
    }

    private void BeginFullscreenScrollControlsGrace()
    {
        if (!_isFullscreen)
            return;

        _fullscreenScrollGraceActive = true;
        _fullscreenScrollGraceTimer.Stop();
        _fullscreenScrollGraceTimer.Start();
    }

    private void FullscreenScrollGraceTimerOnTick(object? sender, EventArgs eventArgs)
    {
        _fullscreenScrollGraceTimer.Stop();
        _fullscreenScrollGraceActive = false;
        if (_isFullscreen && !_isPointerInFullscreenRevealZone)
            HideFullscreenControls();
    }

    private void FullscreenCursorTimerOnTick(object? sender, EventArgs eventArgs)
    {
        if (!_isFullscreen)
            return;

        if (FullscreenControlsBar.IsPointerOver || _isFullscreenTimelinePointerDown || _isFullscreenVolumePointerDown)
        {
            _fullscreenCursorTimer.Stop();
            _fullscreenCursorTimer.Start();
            return;
        }

        Cursor = FullscreenHiddenCursor;
        VideoHost.Cursor = FullscreenHiddenCursor;
        FullscreenControlsBar.Cursor = FullscreenHiddenCursor;
        VideoHost.SetNativeCursorVisible(false);
    }

    private void ShowFullscreenCursor()
    {
        if (!_isFullscreen)
            return;

        Cursor = _cursorBeforeFullscreen;
        VideoHost.Cursor = _videoHostCursorBeforeFullscreen;
        FullscreenControlsBar.Cursor = _fullscreenControlsBarCursorBeforeFullscreen;
        VideoHost.SetNativeCursorVisible(true);
        _fullscreenCursorTimer.Stop();
        _fullscreenCursorTimer.Start();
    }

    private void RestoreFullscreenCursor()
    {
        _fullscreenCursorTimer.Stop();
        Cursor = _cursorBeforeFullscreen;
        VideoHost.Cursor = _videoHostCursorBeforeFullscreen;
        FullscreenControlsBar.Cursor = _fullscreenControlsBarCursorBeforeFullscreen;
        VideoHost.SetNativeCursorVisible(true);
        _cursorBeforeFullscreen = null;
        _videoHostCursorBeforeFullscreen = null;
        _fullscreenControlsBarCursorBeforeFullscreen = null;
    }

    private void UpdateFullscreenPopupGeometry()
    {
        if (!_isFullscreen || VideoBorder.Bounds.Width <= 0)
            return;

        var availableWidth = Math.Max(0, VideoBorder.Bounds.Width - 32);
        var popupWidth = Math.Min(availableWidth,
            Math.Max(FullscreenControlMinimumWidth, availableWidth * FullscreenControlWidthRatio));

        FullscreenControlsBar.Width = popupWidth;
        FullscreenControlsBar.Height = FullscreenControlBarHeight;
        FullscreenControlsPopup.VerticalOffset = -(FullscreenControlBarHeight + FullscreenControlBottomMargin);
    }

    private static void SetPlayPauseIcon(Button button, PathIcon icon, bool paused)
    {
        var label = paused ? "Play" : "Pause";
        if (Avalonia.Automation.AutomationProperties.GetName(button) == label) return;
        icon.Data = UiTheme.Resource<Geometry>(paused ? "PlayIcon" : "PauseIcon");
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }

    private void UpdatePlaybackUi(object? sender = null, EventArgs? eventArgs = null)
    {
        if (_player is null)
            return;

        try
        {
            var snapshot = _player.GetSnapshot();
            if (!snapshot.IsLoaded || snapshot.Generation != _player.Generation) return;
            if (snapshot.DurationSeconds > 0 &&
                (!_timelineSized || Math.Abs(snapshot.DurationSeconds - _durationSeconds) > 0.01))
            {
                SetTimelineDuration(snapshot.DurationSeconds);
            }

            if (_durationSeconds > 0 && !string.IsNullOrWhiteSpace(_currentFilePath) && !IsPreparingMedia(_player))
            {
                FullscreenButton.IsEnabled = true;
                FullscreenPlayPauseButton.IsEnabled = true;
            }

            _currentPos = Math.Clamp(snapshot.PositionSeconds, 0, Math.Max(0, _durationSeconds));
            PositionText.Text = TimeFormat.Clock(snapshot.PositionSeconds);
            SpeedText.Text = $"{snapshot.PlaybackSpeed:0.##}×";
            SetPlayPauseIcon(PlayPauseButton, PlayPauseIcon, snapshot.IsPaused);
            FullscreenPositionText.Text = TimeFormat.Clock(snapshot.PositionSeconds);
            FullscreenDurationText.Text = TimeFormat.Clock(snapshot.DurationSeconds);
            FullscreenSpeedText.Text = $"{snapshot.PlaybackSpeed:0.##}×";
            SetPlayPauseIcon(FullscreenPlayPauseButton, FullscreenPlayPauseIcon, snapshot.IsPaused);
            if (!_isFullscreenTimelinePointerDown)
            {
                _isApplyingFullscreenSliderValue = true;
                FullscreenTimelineSlider.Value = Math.Clamp(snapshot.PositionSeconds, 0, Math.Max(0, _durationSeconds));
                _isApplyingFullscreenSliderValue = false;
            }
            if (_playhead is not null) Canvas.SetLeft(_playhead, TimeToX(_currentPos));
            UpdatePlayheadIndicators();

            if (snapshot.DurationSeconds > 0 && DateTimeOffset.Now - _lastStatsLog >= TimeSpan.FromSeconds(10))
            {
                _lastStatsLog = DateTimeOffset.Now;
                AppLog.Info($"Playback diagnostics: position={snapshot.PositionSeconds:0.000}/{snapshot.DurationSeconds:0.000}; " +
                            $"paused={snapshot.IsPaused}; vo={snapshot.VideoOutput}; gpu-context={snapshot.GpuContext}; " +
                            $"active-hwdec={snapshot.HardwareDecoder}; decoder-drops={snapshot.DecoderDroppedFrames}; " +
                            $"output-drops={snapshot.OutputDroppedFrames}");
            }
        }
        catch (ObjectDisposedException)
        {
            // A final timer tick can race with window teardown.
        }
        catch (Exception exception)
        {
            AppLog.Error($"Failed to update playback UI: {exception.Message}");
        }
    }

    private void ResetTimeline()
    {
        _durationSeconds = 0;
        _currentPos = 0;
        _timelineSized = false;
        TimelineCanvas.Width = 0;
        TimelineCanvas.Children.Clear();
        _timelineMarkers.Clear();
        _playhead = null;
        _cropHighlight = null;
        _cropStartThumb = null;
        _cropEndThumb = null;
        SetCropValues(0, 0);
        TimelineStartLabel.Text = "0:00";
        TimelineEndLabel.Text = "0:00";
        PositionText.Text = "00:00";
        SpeedText.Text = "1×";
        FullscreenSpeedText.Text = "1×";
        FullscreenButton.IsEnabled = false;
        ResetFullscreenTimeline();
    }

    private void SetTimelineDuration(double duration)
    {
        _durationSeconds = Math.Max(0, duration);
        _timelineSized = _durationSeconds > 0;
        TimelineCanvas.Children.Clear();
        _timelineMarkers.Clear();
        _playhead = null;
        _cropHighlight = null;
        _cropStartThumb = null;
        _cropEndThumb = null;
        TimelineCanvas.Width = Math.Max(_durationSeconds * PixelsPerSecond, Math.Max(0, TimelineScroller.Viewport.Width - 2 * TimelineViewport.LabelGutter));
        TimelineStartLabel.Text = TimeFormat.Clock(0);
        TimelineEndLabel.Text = TimeFormat.Clock(_durationSeconds);
        SetFullscreenTimelineDuration(_durationSeconds);
        EnsurePlayhead();
        DrawMinuteMarkers();
        if (CropToggle.IsChecked == true)
        {
            EnsureCropVisuals();
            UpdateCropVisuals();
        }
    }

    private void ResetFullscreenTimeline()
    {
        _isFullscreenTimelinePointerDown = false;
        _isApplyingFullscreenSliderValue = true;
        FullscreenTimelineSlider.IsEnabled = false;
        FullscreenTimelineSlider.Maximum = 1;
        FullscreenTimelineSlider.Value = 0;
        _isApplyingFullscreenSliderValue = false;
        FullscreenPositionText.Text = "00:00";
        FullscreenDurationText.Text = "00:00";
        FullscreenPlayPauseButton.IsEnabled = false;
    }

    private void SetFullscreenTimelineDuration(double duration)
    {
        _isApplyingFullscreenSliderValue = true;
        FullscreenTimelineSlider.Maximum = Math.Max(1, duration);
        FullscreenTimelineSlider.IsEnabled = duration > 0;
        FullscreenTimelineSlider.Value = Math.Clamp(_currentPos, 0, Math.Max(0, duration));
        _isApplyingFullscreenSliderValue = false;
        FullscreenDurationText.Text = TimeFormat.Clock(duration);
        FullscreenPlayPauseButton.IsEnabled = duration > 0;
    }

    private void TimelineScroller_SizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (_durationSeconds > 0)
        {
            TimelineCanvas.Width = Math.Max(_durationSeconds * PixelsPerSecond, Math.Max(0, eventArgs.NewSize.Width - 2 * TimelineViewport.LabelGutter));
            DrawMinuteMarkers();
            if (_playhead is not null)
                Canvas.SetLeft(_playhead, TimeToX(_currentPos));
            UpdateCropVisuals();
        }
        UpdatePlayheadIndicators();
    }

    private void EnsurePlayhead()
    {
        if (_playhead is not null)
            return;

        _playhead = new Rectangle
        {
            Width = 2,
            Height = TimelineCanvas.Height,
            Fill = UiTheme.Resource<IBrush>("TextPrimary"),
            IsHitTestVisible = false,
            ZIndex = 10
        };
        TimelineCanvas.Children.Add(_playhead);
        Canvas.SetLeft(_playhead, TimeToX(_currentPos));
    }

    private double TimeToX(double seconds) =>
        _durationSeconds <= 0 ? 0 : Math.Clamp(seconds, 0, _durationSeconds) / _durationSeconds * TimelineCanvas.Width;

    private double XToTime(double x) =>
        TimelineCanvas.Width <= 0 ? 0 : Math.Clamp(x, 0, TimelineCanvas.Width) / TimelineCanvas.Width * _durationSeconds;

    private void DrawMinuteMarkers()
    {
        var (first, last) = TimelineViewport.VisibleMinutes(_durationSeconds, TimelineCanvas.Width,
            TimelineScroller.Viewport.Width, TimelineScroller.Offset.X);
        foreach (var minute in _timelineMarkers.Keys.Where(m => m < first || m > last).ToArray())
        {
            var marker = _timelineMarkers[minute];
            TimelineCanvas.Children.Remove(marker.Line);
            TimelineCanvas.Children.Remove(marker.Label);
            _timelineMarkers.Remove(minute);
            if (_markerPool.Count < 64) _markerPool.Push(marker);
        }
        for (var minute = first; minute <= last && minute >= 0; minute++)
        {
            if (!_timelineMarkers.TryGetValue(minute, out var marker))
            {
                marker = _markerPool.TryPop(out var recycled) ? recycled : (
                    new Rectangle { Width = 1, Fill = UiTheme.Resource<IBrush>("TextMuted"), Opacity = 0.4, IsHitTestVisible = false },
                    new TextBlock { Foreground = UiTheme.Resource<IBrush>("TextMuted"), FontSize = 10, IsHitTestVisible = false });
                _timelineMarkers.Add(minute, marker);
                TimelineCanvas.Children.Add(marker.Line);
                TimelineCanvas.Children.Add(marker.Label);
                marker.Label.Text = TimeFormat.Clock(minute * 60d);
                marker.Label.IsVisible = minute != 0;
                Canvas.SetTop(marker.Label, 2);
            }
            var x = TimeToX(minute * 60d);
            marker.Line.Height = TimelineCanvas.Height;
            Canvas.SetLeft(marker.Line, x);
            Canvas.SetLeft(marker.Label, x + 3);
            if (minute == int.MaxValue) break;
        }
    }

    private void TimelineCanvas_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (_player is null || !eventArgs.GetCurrentPoint(TimelineCanvas).Properties.IsLeftButtonPressed)
            return;
        RunPlayerAction(player => player.SeekAsync(XToTime(eventArgs.GetPosition(TimelineCanvas).X)));
    }

    private void EnsureCropVisuals()
    {
        if (_cropHighlight is not null || _durationSeconds <= 0)
            return;

        _cropHighlight = new Rectangle
        {
            Height = TimelineCanvas.Height,
            Fill = UiTheme.Resource<IBrush>("CropSelectionBrush"),
            IsHitTestVisible = false,
            ZIndex = 4
        };
        TimelineCanvas.Children.Add(_cropHighlight);
        _cropStartThumb = MakeCropHandle();
        _cropEndThumb = MakeCropHandle();
        _cropStartThumb.DragDelta += CropStartThumbOnDragDelta;
        _cropEndThumb.DragDelta += CropEndThumbOnDragDelta;
        TimelineCanvas.Children.Add(_cropStartThumb);
        TimelineCanvas.Children.Add(_cropEndThumb);
    }

    private Thumb MakeCropHandle() => new()
    {
        Width = 8,
        Height = TimelineCanvas.Height,
        Background = UiTheme.Resource<IBrush>("AccentBrush"),
        Opacity = 0.7,
        Cursor = new Cursor(StandardCursorType.SizeWestEast),
        ZIndex = 5
    };

    private void UpdateCropVisuals()
    {
        if (_cropHighlight is null || _cropStartThumb is null || _cropEndThumb is null || _durationSeconds <= 0)
            return;

        var xStart = TimeToX(_cropStart);
        var xEnd = TimeToX(_cropEnd);
        _cropHighlight.Width = Math.Max(0, xEnd - xStart);
        Canvas.SetLeft(_cropHighlight, xStart);
        Canvas.SetLeft(_cropStartThumb, xStart - 4);
        Canvas.SetLeft(_cropEndThumb, xEnd - 4);
    }

    private void CropToggleOnCheckedChanged(object? sender, RoutedEventArgs eventArgs)
    {
        var visible = CropToggle.IsChecked == true;
        if (!visible)
        {
            SetCropVisualsVisible(false);
            return;
        }
        if (_durationSeconds <= 0)
            return;

        EnsureCropVisuals();
        SetCropValues(_currentPos, Math.Min(_currentPos + 10, _durationSeconds));
        SetCropVisualsVisible(true);
        UpdateCropVisuals();
    }

    private void SetCropVisualsVisible(bool visible)
    {
        if (_cropHighlight is not null) _cropHighlight.IsVisible = visible;
        if (_cropStartThumb is not null) _cropStartThumb.IsVisible = visible;
        if (_cropEndThumb is not null) _cropEndThumb.IsVisible = visible;
    }

    private void CropStartThumbOnDragDelta(object? sender, VectorEventArgs eventArgs) =>
        SetCropValues(XToTime(TimeToX(_cropStart) + eventArgs.Vector.X), _cropEnd);

    private void CropEndThumbOnDragDelta(object? sender, VectorEventArgs eventArgs) =>
        SetCropValues(_cropStart, XToTime(TimeToX(_cropEnd) + eventArgs.Vector.X));

    private void SetCropStartToCurrent()
    {
        if (_currentPos <= _cropEnd)
            SetCropValues(_currentPos, _cropEnd);
        else
            SetCropValues(Math.Min(_currentPos, _durationSeconds), Math.Min(_currentPos + 10, _durationSeconds));
    }

    private void SetCropEndToCurrent()
    {
        if (_currentPos >= _cropStart)
            SetCropValues(_cropStart, _currentPos);
        else
            SetCropValues(Math.Max(0, _currentPos - 10), _currentPos);
    }

    private void SetCropValues(double start, double end)
    {
        _cropStart = Math.Clamp(start, 0, Math.Max(0, _durationSeconds));
        _cropEnd = Math.Clamp(end, _cropStart, Math.Max(0, _durationSeconds));
        CropStartText.Text = CropStartBox.Text = TimeFormat.TimeCode(_cropStart);
        CropEndText.Text = CropEndBox.Text = TimeFormat.TimeCode(_cropEnd);
        UpdateCropVisuals();
    }

    private void CropTimeBoxOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (sender is not TextBox box || box.IsReadOnly) return;
        if (eventArgs.Key == Key.Enter)
        {
            CommitCropTimeBox(box);
            RootGrid.Focus();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            box.Text = TimeFormat.TimeCode(ReferenceEquals(box, CropStartBox) ? _cropStart : _cropEnd);
            RootGrid.Focus();
            eventArgs.Handled = true;
        }
    }

    private void CommitCropTimeBox(TextBox box)
    {
        if (box.IsReadOnly) return;
        var parts = box.Text?.Trim().Split(':');
        if (parts is { Length: 3 } &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes < 60 &&
            double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            double.IsFinite(seconds) && seconds >= 0 && seconds < 60)
        {
            var value = hours * 3600d + minutes * 60d + seconds;
            if (ReferenceEquals(box, CropStartBox)) SetCropValues(value, _cropEnd);
            else SetCropValues(_cropStart, value);
        }
        else box.Text = TimeFormat.TimeCode(ReferenceEquals(box, CropStartBox) ? _cropStart : _cropEnd);
    }

    private void TimelineScroller_ScrollChanged(object? sender, ScrollChangedEventArgs eventArgs)
    {
        DrawMinuteMarkers();
        UpdatePlayheadIndicators();
    }

    private void TimelineScrollerOnPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        var delta = eventArgs.Delta;
        if (eventArgs.KeyModifiers == KeyModifiers.Shift ||
            Math.Abs(delta.Y) <= Math.Abs(delta.X))
            return;

        var maximumOffset = Math.Max(0, TimelineScroller.Extent.Width - TimelineScroller.Viewport.Width);
        if (maximumOffset <= 0)
            return;

        var target = Math.Clamp(TimelineScroller.Offset.X + delta.Y * TimelineWheelPixelsPerStep,
            0, maximumOffset);
        TimelineScroller.Offset = new Vector(target, TimelineScroller.Offset.Y);
        eventArgs.Handled = true;
    }

    private void UpdatePlayheadIndicators()
    {
        if (_playhead is null || _durationSeconds <= 0)
        {
            PlayheadLeftIndicator.IsVisible = false;
            PlayheadRightIndicator.IsVisible = false;
            return;
        }

        var headX = TimelineViewport.LabelGutter + TimeToX(_currentPos);
        var viewLeft = TimelineScroller.Offset.X;
        var viewRight = viewLeft + TimelineScroller.Viewport.Width;
        PlayheadLeftIndicator.IsVisible = headX < viewLeft;
        PlayheadRightIndicator.IsVisible = headX > viewRight;
    }

    private void PlayheadIndicator_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var target = Math.Max(0, TimelineViewport.LabelGutter + TimeToX(_currentPos) - TimelineScroller.Viewport.Width / 2);
        TimelineScroller.Offset = new Vector(target, TimelineScroller.Offset.Y);
        eventArgs.Handled = true;
    }

    private async void RunPlayerAction(Func<IPlaybackEngine, Task> action)
    {
        if (_player is not { } player || _closing) return;
        var generation = player.Generation;
        try { await action(player); }
        catch (Exception exception)
        {
            if (IsCurrentMedia(player, generation))
            {
                AppLog.Error($"Playback action failed: {exception}");
                ShowError(exception.Message);
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.IsVisible = true;
        if (_durationSeconds <= 0)
        {
            _currentFilePath = null;
            MediaNameText.Text = "Dino Player";
            UpdateImportPrompt();
        }
    }

    private void DismissErrorOnClick(object? sender, RoutedEventArgs eventArgs) => ErrorBanner.IsVisible = false;

    private void Window_DragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = eventArgs.DataTransfer.TryGetFiles()?.Any() == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void Window_Drop(object? sender, DragEventArgs eventArgs)
    {
        if (eventArgs.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            eventArgs.Handled = true;
            LoadVideo(path);
        }
    }

    private void OnLogMessage(string line) => _logPump.Append(line);

    private void DisposePlayer()
    {
        ++_hostGeneration;
        if (_player is { } current && !_closing) _resumeSnapshot = current.GetSnapshot();
        if (_isFullscreen) SetFullscreen(false);
        FlushAudioPreset();
        _uiTimer.Stop();
        _mixTimer.Stop();
        HidePlayerQueueStatus();
        _fullscreenControlsTimer.Stop();
        _fullscreenCursorTimer.Stop();
        _fullscreenScrollGraceTimer.Stop();
        var player = Interlocked.Exchange(ref _player, null);
        _preparingMedia = null;
        OpenMenuItem.IsEnabled = ImportPrompt.IsEnabled = false;
        SetMediaControlsEnabled(false);
        FullscreenMasterVolumeSlider.IsEnabled = false;
        if (player is not null) _playerShutdown = player.StopAsync();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing || _confirmingClose) return;
        var exports = _exportManager.GetSnapshot();
        if (exports.HasUnfinished)
        {
            _confirmingClose = true;
            try
            {
                var close = await new ConfirmDialog(
                    $"There are {exports.WaitingCount} waiting export(s) and {(exports.Active is null ? 0 : 1)} active export(s).\n\nClosing cancels unfinished exports and discards the queue. The queue is not saved.",
                    "Cancel exports and close", "Keep open", defaultToNo: true)
                    { Title = "Unfinished exports", Width = 530, Height = 220 }.ShowDialog<bool>(this);
                if (!close) return;
            }
            finally { _confirmingClose = false; }
        }
        _closing = true;
        ImportPrompt.IsVisible = false;
        _tabResumePlayback = null;
        try { await _exportManager.StopAsync(); }
        catch (Exception exception) { AppLog.Warn($"An export failed while closing: {exception.Message}"); }
        finally
        {
            DisposePlayer();
            try { await _playerShutdown; }
            catch (Exception exception) { AppLog.Warn($"Playback shutdown failed: {exception.Message}"); }
            _allowClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        ImportPrompt.IsVisible = false;
        ExportPanel.Dispose();
        AppLog.MessageWritten -= OnLogMessage;
        _logPump.Dispose();
        base.OnClosed(e);
    }
}
