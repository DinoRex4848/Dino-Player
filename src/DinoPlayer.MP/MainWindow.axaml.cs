using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
            if (!CropStartBox.IsFocused && !CropEndBox.IsFocused) FocusPlayer();
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

    // A neutral focus target keeps playback keys out of buttons, sliders and native popups.
    private void FocusPlayer() => Dispatcher.UIThread.Post(() =>
    {
        if (IsActive && !_exportTabSelected && !_preferencesOpen && !_closing) RootGrid.Focus();
    }, DispatcherPriority.Loaded);

    private void ExitMenuItemOnClick(object? sender, RoutedEventArgs eventArgs) => Close();

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
        ExportPanel.ApplyDefaultOutputFolder();
        if (!general.ShowPlayerQueueButton) HidePlayerQueueStatus();
        if (!general.ShowDiagnosticLog) DiagnosticLogExpander.IsExpanded = false;
        RunPlayerAction(player => player.SetLoopingAsync(general.LoopByDefault));
    }

    private void SetMediaControlsEnabled(bool enabled)
    {
        PlayPauseButton.IsEnabled = FullscreenPlayPauseButton.IsEnabled = enabled;
        TimelineCanvas.IsEnabled = FullscreenTimelineSlider.IsEnabled = enabled;
        FullscreenButton.IsEnabled = CropToggle.IsEnabled = enabled;
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = enabled;
        PlayerAddToQueueButton.IsEnabled = enabled && !_closing && !_exportManager.GetSnapshot().IsStopping;
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
                    $"{DescribeUnfinishedExports(exports)}\n\nClosing cancels unfinished exports and discards the queue. The queue is not saved.",
                    "Cancel exports and close", "Keep open", defaultToNo: true)
                    { Title = "Unfinished exports", Width = 530 }.ShowDialog<bool>(this);
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

    private static string DescribeUnfinishedExports(ExportManagerSnapshot exports)
    {
        var parts = new List<string>();
        if (exports.Active is not null) parts.Add("1 export is running");
        if (exports.WaitingCount > 0)
            parts.Add(exports.WaitingCount == 1 ? "1 export is waiting" : $"{exports.WaitingCount} exports are waiting");
        return string.Join(" and ", parts) + ".";
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
