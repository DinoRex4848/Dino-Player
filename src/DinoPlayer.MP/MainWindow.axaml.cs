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

    private readonly MpvVideoHost _videoHost;
    private readonly Grid _rootGrid;
    private readonly TabStrip _workspaceTabs;
    private readonly TabStripItem _exportTabHeader;
    private readonly ExportView _exportView;
    private readonly ExportManager _exportManager;
    private bool _exportTabSelected;
    private bool _updatingTabs;
    private bool _confirmingClose;
    private (IPlaybackEngine Player, long Generation)? _tabResumePlayback;
    private (IPlaybackEngine Player, long Generation, bool Paused)? _preparingMedia;
    private int _playbackUiUpdateQueued;
    private readonly Grid _headerGrid;
    private readonly Grid _workspaceGrid;
    private readonly Grid _playerSidebar;
    private readonly Border _videoBorder;
    private readonly Border _errorBanner;
    private readonly TextBlock _errorText;
    private readonly Expander _diagnosticLogExpander;
    private readonly Button _importPrompt;
    private readonly Button _playerAddToQueueButton;
    private readonly TextBlock _playerQueueStatusText;
    private bool _isPlayerQueueStatusFading;
    private bool _pickingMedia;
    private bool _preferencesOpen;
    private readonly Popup _fullscreenControlsPopup;
    private readonly Border _fullscreenControlsBar;
    private readonly Grid _normalPlaybackArea;
    private readonly MenuItem _openMenuItem;
    private readonly PathIcon _playPauseIcon;
    private readonly PathIcon _fullscreenPlayPauseIcon;
    private readonly Button _playPauseButton;
    private readonly Button _fullscreenButton;
    private readonly ToggleButton _muteAllButton;
    private readonly Button _skipBackButton;
    private readonly Button _skipForwardButton;
    private readonly ToggleButton _cropToggle;
    private readonly Button _jumpCropStartButton;
    private readonly Button _setCropStartButton;
    private readonly Button _setCropEndButton;
    private readonly Button _jumpCropEndButton;
    private readonly Slider _masterVolumeSlider;
    private readonly TextBlock _masterVolumeText;
    private readonly TextBlock _positionText;
    private readonly TextBlock _speedText;
    private readonly TextBlock _mediaNameText;
    private readonly TextBlock _timelineStartLabel;
    private readonly TextBlock _timelineEndLabel;
    private readonly Slider _fullscreenTimelineSlider;
    private readonly Button _fullscreenPlayPauseButton;
    private readonly TextBlock _fullscreenPositionText;
    private readonly TextBlock _fullscreenDurationText;
    private readonly TextBlock _fullscreenSpeedText;
    private readonly Slider _fullscreenMasterVolumeSlider;
    private readonly TextBlock _fullscreenMasterVolumeText;
    private readonly TextBlock _logPathText;
    private readonly TextBox _logTextBox;
    private readonly UiLogPump _logPump;
    private readonly TextBox _cropStartBox;
    private readonly TextBox _cropEndBox;
    private readonly TextBlock _cropStartText;
    private readonly TextBlock _cropEndText;
    private readonly StackPanel _audioTracksPanel;
    private readonly Canvas _timelineCanvas;
    private readonly ScrollViewer _timelineScroller;
    private readonly Avalonia.Controls.Shapes.Path _playheadLeftIndicator;
    private readonly Avalonia.Controls.Shapes.Path _playheadRightIndicator;
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

        _rootGrid = Require<Grid>("RootGrid");
        _workspaceTabs = Require<TabStrip>("WorkspaceTabs");
        _exportTabHeader = Require<TabStripItem>("ExportTabHeader");
        _exportView = Require<ExportView>("ExportPanel");
        _exportManager = new ExportManager(exportRunner, playerSource: () => _currentFilePath);
        _headerGrid = Require<Grid>("HeaderGrid");
        _workspaceGrid = Require<Grid>("WorkspaceGrid");
        _playerSidebar = Require<Grid>("PlayerSidebar");
        _videoBorder = Require<Border>("VideoBorder");
        _errorBanner = Require<Border>("ErrorBanner");
        _errorText = Require<TextBlock>("ErrorText");
        _diagnosticLogExpander = Require<Expander>("DiagnosticLogExpander");
        _diagnosticLogExpander.IsVisible = SettingsStore.Current.General.ShowDiagnosticLog;
        _importPrompt = Require<Button>("ImportPrompt");
        _playerAddToQueueButton = Require<Button>("PlayerAddToQueueButton");
        _playerQueueStatusText = Require<TextBlock>("PlayerQueueStatusText");
        _playerAddToQueueButton.IsVisible = SettingsStore.Current.General.ShowPlayerQueueButton;
        _fullscreenControlsPopup = Require<Popup>("FullscreenControlsPopup");
        _fullscreenControlsBar = Require<Border>("FullscreenControlsBar");
        _normalPlaybackArea = Require<Grid>("NormalPlaybackArea");
        _videoHost = Require<MpvVideoHost>("VideoHost");
        _openMenuItem = Require<MenuItem>("OpenMenuItem");
        _playPauseIcon = Require<PathIcon>("PlayPauseIcon");
        _fullscreenPlayPauseIcon = Require<PathIcon>("FullscreenPlayPauseIcon");
        _playPauseButton = Require<Button>("PlayPauseButton");
        _fullscreenButton = Require<Button>("FullscreenButton");
        _muteAllButton = Require<ToggleButton>("MuteAllButton");
        _skipBackButton = Require<Button>("SkipBackButton");
        _skipForwardButton = Require<Button>("SkipForwardButton");
        _cropToggle = Require<ToggleButton>("CropToggle");
        _jumpCropStartButton = Require<Button>("JumpCropStartButton");
        _setCropStartButton = Require<Button>("SetCropStartButton");
        _setCropEndButton = Require<Button>("SetCropEndButton");
        _jumpCropEndButton = Require<Button>("JumpCropEndButton");
        _masterVolumeSlider = Require<Slider>("MasterVolumeSlider");
        _masterVolumeText = Require<TextBlock>("MasterVolumeText");
        _positionText = Require<TextBlock>("PositionText");
        _speedText = Require<TextBlock>("SpeedText");
        _mediaNameText = Require<TextBlock>("MediaNameText");
        _timelineStartLabel = Require<TextBlock>("TimelineStartLabel");
        _timelineEndLabel = Require<TextBlock>("TimelineEndLabel");
        _fullscreenTimelineSlider = Require<Slider>("FullscreenTimelineSlider");
        _fullscreenPlayPauseButton = Require<Button>("FullscreenPlayPauseButton");
        _fullscreenPositionText = Require<TextBlock>("FullscreenPositionText");
        _fullscreenDurationText = Require<TextBlock>("FullscreenDurationText");
        _fullscreenSpeedText = Require<TextBlock>("FullscreenSpeedText");
        _fullscreenMasterVolumeSlider = Require<Slider>("FullscreenMasterVolumeSlider");
        _fullscreenMasterVolumeText = Require<TextBlock>("FullscreenMasterVolumeText");
        _logPathText = Require<TextBlock>("LogPathText");
        _logTextBox = Require<TextBox>("LogTextBox");
        _logPump = new UiLogPump(_logTextBox);
        _cropStartBox = Require<TextBox>("CropStartBox");
        _cropEndBox = Require<TextBox>("CropEndBox");
        _cropStartText = Require<TextBlock>("CropStartText");
        _cropEndText = Require<TextBlock>("CropEndText");
        _audioTracksPanel = Require<StackPanel>("AudioTracksPanel");
        _timelineCanvas = Require<Canvas>("TimelineCanvas");
        _timelineScroller = Require<ScrollViewer>("TimelineScroller");
        _timelineScroller.AddHandler(PointerWheelChangedEvent, TimelineScrollerOnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        _playheadLeftIndicator = Require<Avalonia.Controls.Shapes.Path>("PlayheadLeftIndicator");
        _playheadRightIndicator = Require<Avalonia.Controls.Shapes.Path>("PlayheadRightIndicator");

        _logPathText.Text = $"Log: {AppLog.LogPath}";
        SetCropValues(0, 0);
        ApplyCropTimeEditingPreference(SettingsStore.Current.General.EditableCropTimes);

        _videoHost.HandleCreated += InitializePlayer;
        _videoHost.HandleDestroying += DisposePlayer;
        _videoHost.PointerInput += OnVideoHostPointerInput;
        _videoBorder.SizeChanged += (_, _) => UpdateFullscreenPopupGeometry();
        SizeChanged += (_, _) => UpdateFullscreenPopupGeometry();
        _workspaceGrid.SizeChanged += (_, _) => UpdateImportPrompt();
        Opened += (_, _) => UpdateImportPrompt();
        Activated += (_, _) =>
        {
            FocusPlayer();
        };
        _playPauseButton.Click += (_, _) => RunPlayerAction(player => player.TogglePauseAsync());
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _fullscreenPlayPauseButton.Click += (_, _) =>
        {
            ShowFullscreenControls();
            RunPlayerAction(player => player.TogglePauseAsync());
        };
        _fullscreenTimelineSlider.ValueChanged += FullscreenTimelineSliderOnValueChanged;
        _fullscreenTimelineSlider.PointerPressed += FullscreenTimelineSliderOnPointerPressed;
        _fullscreenTimelineSlider.PointerReleased += FullscreenTimelineSliderOnPointerReleased;
        _fullscreenTimelineSlider.PointerCaptureLost += FullscreenTimelineSliderOnPointerCaptureLost;
        _fullscreenControlsPopup.AddHandler(KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel);
        _fullscreenControlsBar.AddHandler(KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel);
        _fullscreenControlsBar.SizeChanged += (_, _) => UpdateFullscreenPopupGeometry();
        _fullscreenControlsBar.AddHandler(PointerWheelChangedEvent, FullscreenControlsBarOnPointerWheelChanged,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _muteAllButton.Click += (_, _) => ToggleMasterMute();
        _skipBackButton.Click += (_, _) => Skip(-5);
        _skipForwardButton.Click += (_, _) => Skip(5);
        _jumpCropStartButton.Click += (_, _) => RunPlayerAction(player => player.SeekAsync(_cropStart));
        _jumpCropEndButton.Click += (_, _) => RunPlayerAction(player => player.SeekAsync(_cropEnd));
        _setCropStartButton.Click += (_, _) => SetCropStartToCurrent();
        _setCropEndButton.Click += (_, _) => SetCropEndToCurrent();
        _cropToggle.IsCheckedChanged += CropToggleOnCheckedChanged;
        _masterVolumeSlider.ValueChanged += MasterVolumeOnValueChanged;
        _fullscreenMasterVolumeSlider.ValueChanged += MasterVolumeOnValueChanged;
        ConfigureMasterVolumeSlider(_masterVolumeSlider);
        ConfigureMasterVolumeSlider(_fullscreenMasterVolumeSlider);
        _cropStartBox.KeyDown += CropTimeBoxOnKeyDown;
        _cropEndBox.KeyDown += CropTimeBoxOnKeyDown;
        _cropStartBox.LostFocus += (_, _) => CommitCropTimeBox(_cropStartBox);
        _cropEndBox.LostFocus += (_, _) => CommitCropTimeBox(_cropEndBox);
        _workspaceTabs.SelectionChanged += async (_, _) =>
        {
            if (!_updatingTabs) await SelectWorkspaceTabAsync(_workspaceTabs.SelectedIndex == 1);
        };
        _exportView.StatusUpdated += UpdateExportTabHeader;
        _exportView.Attach(_exportManager);
        AddHandler(KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel);
        AppLog.MessageWritten += OnLogMessage;

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, UpdatePlaybackUi);
        _uiTimer.Start();
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

    private T Require<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"Required control '{name}' was not found.");

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
            player.PointerInput += input => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_player, player) && !_closing) HandlePlaybackPointerInput(input);
            }, DispatcherPriority.Input);
            await player.SetMasterVolumeAsync(_masterVolumeSlider.Value);
            await player.SetLoopingAsync(SettingsStore.Current.General.LoopByDefault);
            if (!ReferenceEquals(_player, player) || _closing) return;
            _uiTimer.Start();
            _openMenuItem.IsEnabled = _importPrompt.IsEnabled = true;
            UpdateImportPrompt();
            FocusPlayer();
            _fullscreenMasterVolumeSlider.IsEnabled = true;
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

    private void OnPlaybackPointerInput(PlaybackPointerEvent pointerEvent) =>
        Dispatcher.UIThread.Post(() => { if (!_closing && !_closed) HandlePlaybackPointerInput(pointerEvent); }, DispatcherPriority.Input);

    private void OnVideoHostPointerInput(PlaybackPointerEvent pointerEvent) =>
        OnPlaybackPointerInput(pointerEvent);

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
        _videoHost.IsVisible = !string.IsNullOrWhiteSpace(_currentFilePath);
        var show = IsVisible && !_closing && !_closed && !_preferencesOpen &&
                   !_isFullscreen && string.IsNullOrWhiteSpace(_currentFilePath) && _workspaceGrid.Bounds.Width > 0;
        var wasVisible = _importPrompt.IsVisible;
        _importPrompt.IsVisible = show;
        if (show && !wasVisible) FocusPlayer();
    }

    // A neutral focus target keeps playback keys out of buttons, sliders and native popups.
    private void FocusPlayer() => Dispatcher.UIThread.Post(() =>
    {
        if (IsActive && !_exportTabSelected && !_preferencesOpen && !_closing) _rootGrid.Focus();
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
            _audioTracksPanel.IsEnabled = false;
            _audioTracksPanel.Children.Clear();
            _muteAllButton.IsEnabled = false;
            var fullPath = System.IO.Path.GetFullPath(path);
            if (!restorePlayback) _resumeSnapshot = null;
            _currentFilePath = fullPath;
            _mediaNameText.Text = System.IO.Path.GetFileName(_currentFilePath);
            _errorBanner.IsVisible = false;
            HidePlayerQueueStatus();
            UpdateImportPrompt();
            SetMediaControlsEnabled(false);
            ResetTimeline();
            _cropToggle.IsChecked = false;
            _exportView.SetContext(new());
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
        if (!_playerAddToQueueButton.IsEnabled || _closing || _closed) return;
        if (_exportView.TryAddToQueue(CaptureExportContext(), out var message))
        {
            ShowPlayerQueueStatus(message);
            _errorBanner.IsVisible = false;
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
        _playerQueueStatusText.Text = message;
        _playerQueueStatusText.IsVisible = true;
        _playerQueueStatusText.Opacity = 1;
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
        _playerQueueStatusText.Opacity = 0;
        _playerQueueStatusTimer.Interval = PlayerQueueStatusFadeDuration;
        _playerQueueStatusTimer.Start();
    }

    private void HidePlayerQueueStatus()
    {
        _playerQueueStatusTimer.Stop();
        _isPlayerQueueStatusFading = false;
        _playerQueueStatusText.IsVisible = false;
        _playerQueueStatusText.Opacity = 0;
    }

    private ExportContext CaptureExportContext()
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath) || _durationSeconds <= 0)
            return new();
        return new ExportContext
        {
            SourcePath = _currentFilePath,
            CropEnabled = _cropToggle.IsChecked == true,
            Start = _cropToggle.IsChecked == true ? _cropStart : 0,
            End = _cropToggle.IsChecked == true ? _cropEnd : 0,
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
        try { _workspaceTabs.SelectedIndex = export ? 1 : 0; }
        finally { _updatingTabs = false; }
        _videoBorder.IsVisible = !export;
        _playerSidebar.IsVisible = !export && !_isFullscreen;
        _normalPlaybackArea.IsVisible = !export && !_isFullscreen;
        _exportView.IsVisible = export;
        if (export) _exportView.SetContext(CaptureExportContext());
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
        _playerAddToQueueButton.IsEnabled = !_closing && !_closed && !snapshot.IsStopping &&
            _player is not null && _playPauseButton.IsEnabled;
        var details = new List<string>();
        if (snapshot.Active is { } active) details.Add($"{active.Progress:0}%");
        if (snapshot.WaitingCount > 0) details.Add($"{snapshot.WaitingCount} waiting");
        if (snapshot.QueuePaused) details.Add("paused");
        if (snapshot.FailedCount > 0) details.Add($"{snapshot.FailedCount} failed");
        var text = details.Count == 0 ? "Export" : $"Export · {string.Join(" · ", details)}";
        if (!Equals(_exportTabHeader.Content, text)) _exportTabHeader.Content = text;
    }

    private async void PreferencesButtonOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _preferencesOpen = true;
        _importPrompt.IsVisible = false;
        try
        {
            if (await new PreferencesWindow().ShowDialog<bool>(this)) ApplyPreferences();
        }
        finally { _preferencesOpen = false; UpdateImportPrompt(); }
    }

    internal void ApplyPreferences()
    {
        var general = SettingsStore.Current.General;
        _diagnosticLogExpander.IsVisible = general.ShowDiagnosticLog;
        _playerAddToQueueButton.IsVisible = general.ShowPlayerQueueButton;
        ApplyCropTimeEditingPreference(general.EditableCropTimes);
        if (!general.ShowPlayerQueueButton) HidePlayerQueueStatus();
        if (!general.ShowDiagnosticLog) _diagnosticLogExpander.IsExpanded = false;
        RunPlayerAction(player => player.SetLoopingAsync(general.LoopByDefault));
    }

    private void ApplyCropTimeEditingPreference(bool editable)
    {
        foreach (var box in new[] { _cropStartBox, _cropEndBox })
        {
            if (!editable && box.IsFocused)
            {
                CommitCropTimeBox(box);
                _rootGrid.Focus();
            }
            box.IsReadOnly = !editable;
            // These local values override the player's non-focusable control styles.
            box.Focusable = box.IsTabStop = editable;
            box.IsVisible = editable;
        }
        _cropStartText.IsVisible = _cropEndText.IsVisible = !editable;
    }

    private void SetMediaControlsEnabled(bool enabled)
    {
        _playPauseButton.IsEnabled = _fullscreenPlayPauseButton.IsEnabled = enabled;
        _timelineCanvas.IsEnabled = _fullscreenTimelineSlider.IsEnabled = enabled;
        _fullscreenButton.IsEnabled = _cropToggle.IsEnabled = enabled;
        _skipBackButton.IsEnabled = _skipForwardButton.IsEnabled = enabled;
        _playerAddToQueueButton.IsEnabled = enabled && !_closing && !_exportManager.GetSnapshot().IsStopping;
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
                    _exportView.SetContext(CaptureExportContext());
                    _tabResumePlayback = !paused ? (player, generation) : null;
                    paused = true;
                }
                await player.SetPausedAsync(paused);
                if (!IsCurrentMedia(player, generation)) return;
                _preparingMedia = null;
                _audioTracksPanel.IsEnabled = true;
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
        _audioTracksPanel.Children.Clear();
        var tracks = _player?.AudioTracks ?? Array.Empty<AudioTrack>();
        _muteAllButton.IsEnabled = tracks.Count > 0;

        if (tracks.Count == 0)
        {
            _audioTracksPanel.Children.Add(new TextBlock
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
                QueueMixUpdate(immediate: false);
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
                QueueMixUpdate(immediate: false);
                QueueAudioPresetSave();
            };

            var volumeTooltipText = new TextBlock
            {
                Foreground = UiTheme.Resource<IBrush>("TextPrimary"),
                FontWeight = FontWeight.SemiBold
            };
            var tooltipOpenTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            var lastTooltipPointerPosition = new Point();
            var volumeTooltip = new Popup
            {
                PlacementTarget = volume,
                Placement = PlacementMode.Top,
                PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX,
                VerticalOffset = -4,
                Child = new Border
                {
                    Background = UiTheme.Resource<IBrush>("ControlBackground"),
                    BorderBrush = UiTheme.Resource<IBrush>("PanelBorder"),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(9, 5),
                    IsHitTestVisible = false,
                    Child = volumeTooltipText
                }
            };
            tooltipOpenTimer.Tick += (_, _) =>
            {
                tooltipOpenTimer.Stop();
                if (volume.IsPointerOver)
                    ShowVolumeTooltip(volume, volumeTooltip, volumeTooltipText, lastTooltipPointerPosition);
            };
            volume.DetachedFromVisualTree += (_, _) => { tooltipOpenTimer.Stop(); volumeTooltip.IsOpen = false; };
            volume.PointerEntered += (_, args) =>
            {
                lastTooltipPointerPosition = args.GetPosition(volume);
                UpdateVolumeTooltipValue(volume, volumeTooltipText, lastTooltipPointerPosition);
                tooltipOpenTimer.Stop();
                tooltipOpenTimer.Start();
            };
            volume.PointerMoved += (_, args) =>
            {
                lastTooltipPointerPosition = args.GetPosition(volume);
                if (volumeTooltip.IsOpen)
                    ShowVolumeTooltip(volume, volumeTooltip, volumeTooltipText, lastTooltipPointerPosition);
                else
                    UpdateVolumeTooltipValue(volume, volumeTooltipText, lastTooltipPointerPosition);
            };
            volume.PointerExited += (_, _) =>
            {
                tooltipOpenTimer.Stop();
                volumeTooltip.IsOpen = false;
            };

            ConfigureVolumeSliderDragging(volume);

            volume.AddHandler(PointerWheelChangedEvent, (_, args) =>
            {
                if (!volume.IsPointerOver)
                    return;

                var direction = Math.Sign(args.Delta.Y);
                if (direction == 0)
                    return;

                volume.Value = Math.Clamp(Math.Round(volume.Value) + direction, volume.Minimum, volume.Maximum);
                volumeTooltipText.Text = $"{volume.Value:0}%";
                args.Handled = true;
            }, RoutingStrategies.Bubble, handledEventsToo: true);

            var volumeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            volumeRow.Children.Add(volume);
            Grid.SetColumn(volumeValue, 1);
            volumeRow.Children.Add(volumeValue);

            var trackPanel = new StackPanel { Spacing = 3 };
            trackPanel.Children.Add(enabled);
            trackPanel.Children.Add(volumeRow);
            _audioTracksPanel.Children.Add(trackPanel);
        }
    }

    private static void ShowVolumeTooltip(Slider volume, Popup tooltip, TextBlock tooltipText, Point pointerPosition)
    {
        if (volume.Bounds.Width <= 0)
            return;

        UpdateVolumeTooltipValue(volume, tooltipText, pointerPosition);
        // Keep the popup anchored to the slider's bottom edge. Only its X position
        // follows the pointer, so small vertical pointer movements do not make the
        // preview jump around.
        tooltip.HorizontalOffset = pointerPosition.X - volume.Bounds.Width / 2;
        tooltip.IsOpen = true;
    }

    private static void UpdateVolumeTooltipValue(Slider volume, TextBlock tooltipText, Point pointerPosition) =>
        tooltipText.Text = $"{VolumeForPointer(volume, pointerPosition.X):0}%";

    private static double VolumeForPointer(Slider volume, double pointerX)
    {
        if (volume.Bounds.Width <= 0)
            return volume.Value;

        var ratio = Math.Clamp(pointerX / volume.Bounds.Width, 0, 1);
        var value = volume.Minimum + ratio * (volume.Maximum - volume.Minimum);
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static void ConfigureVolumeSliderDragging(Slider volume, Action<bool>? dragChanged = null)
    {
        IPointer? dragPointer = null;

        void EndDrag()
        {
            var pointer = dragPointer;
            if (pointer is null)
                return;

            dragPointer = null;
            if (ReferenceEquals(pointer.Captured, volume))
                pointer.Capture(null);
            dragChanged?.Invoke(false);
        }

        // Handle presses on both the track and thumb ourselves so a track click
        // can continue into a drag using the same percentage calculation.
        volume.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (dragPointer is not null || !args.GetCurrentPoint(volume).Properties.IsLeftButtonPressed)
                return;

            dragPointer = args.Pointer;
            args.Pointer.Capture(volume);
            volume.Value = VolumeForPointer(volume, args.GetPosition(volume).X);
            dragChanged?.Invoke(true);
            args.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        volume.AddHandler(PointerMovedEvent, (_, args) =>
        {
            if (!ReferenceEquals(dragPointer, args.Pointer))
                return;

            if (!args.GetCurrentPoint(volume).Properties.IsLeftButtonPressed)
            {
                EndDrag();
                return;
            }

            volume.Value = VolumeForPointer(volume, args.GetPosition(volume).X);
            dragChanged?.Invoke(true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        volume.AddHandler(PointerReleasedEvent, (_, args) =>
        {
            if (!ReferenceEquals(dragPointer, args.Pointer) || args.InitialPressMouseButton != MouseButton.Left)
                return;

            volume.Value = VolumeForPointer(volume, args.GetPosition(volume).X);
            EndDrag();
            args.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        volume.PointerCaptureLost += (_, args) =>
        {
            if (ReferenceEquals(dragPointer, args.Pointer))
                EndDrag();
        };
        volume.DetachedFromVisualTree += (_, _) => EndDrag();
    }

    private void ToggleMasterMute()
    {
        if (_player is null)
            return;

        _masterVolumeSlider.Value = _masterVolumeSlider.Value > 0 ? 0 : _lastPositiveMasterVolume;
    }

    private bool RestoreAudioPreset()
    {
        var tracks = _player?.AudioTracks;
        if (tracks is null || tracks.Count == 0)
            return false;

        var preset = SettingsStore.FindAudioTrackPreset(tracks.Count);
        if (preset is null)
            return false;

        if (preset.Tracks is null || preset.TrackCount != tracks.Count || preset.Tracks.Count != tracks.Count)
        {
            AppLog.Warn($"Ignoring audio preset for {tracks.Count} tracks because its settings are missing or have the wrong count.");
            return false;
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var savedTrack = preset.Tracks[index];
            if (savedTrack is null || !double.IsFinite(savedTrack.VolumePercent))
            {
                AppLog.Warn($"Ignoring audio preset for {tracks.Count} tracks because track {index + 1} has an invalid volume.");
                return false;
            }
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var savedTrack = preset.Tracks[index];
            tracks[index].Enabled = savedTrack.Enabled;
            tracks[index].VolumePercent = Math.Clamp(savedTrack.VolumePercent, 0, 150);
        }

        AppLog.Info($"Restored audio preset for {tracks.Count} tracks.");
        return true;
    }

    private void QueueAudioPresetSave()
    {
        _audioPresetTimer.Stop();
        if (_player?.AudioTracks.Count > 0)
            _audioPresetTimer.Start();
    }

    private void SavePendingAudioPreset(object? sender, EventArgs eventArgs)
    {
        _audioPresetTimer.Stop();
        SaveCurrentAudioPreset();
    }

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

    private void QueueMixUpdate(bool immediate)
    {
        _mixTimer.Stop();
        if (immediate)
            RunPlayerAction(player => player.ApplyAudioMixAsync());
        else
            _mixTimer.Start();
    }

    private void ApplyPendingMix(object? sender, EventArgs eventArgs)
    {
        _mixTimer.Stop();
        RunPlayerAction(player => player.ApplyAudioMixAsync());
    }

    private void ConfigureMasterVolumeSlider(Slider volume)
    {
        var volumeTooltipText = new TextBlock
        {
            Foreground = UiTheme.Resource<IBrush>("TextPrimary"),
            FontWeight = FontWeight.SemiBold
        };
        var tooltipOpenTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        var lastTooltipPointerPosition = new Point();
        var volumeTooltip = new Popup
        {
            PlacementTarget = volume,
            Placement = PlacementMode.Top,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX,
            VerticalOffset = -4,
            Child = new Border
            {
                Background = UiTheme.Resource<IBrush>("ControlBackground"),
                BorderBrush = UiTheme.Resource<IBrush>("PanelBorder"),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(9, 5),
                IsHitTestVisible = false,
                Child = volumeTooltipText
            }
        };

        void RefreshFullscreenControls()
        {
            if (_isFullscreen)
            {
                ShowFullscreenCursor();
                ShowFullscreenControls();
            }
        }

        tooltipOpenTimer.Tick += (_, _) =>
        {
            tooltipOpenTimer.Stop();
            if (volume.IsPointerOver)
                ShowVolumeTooltip(volume, volumeTooltip, volumeTooltipText, lastTooltipPointerPosition);
        };
        volume.DetachedFromVisualTree += (_, _) => { tooltipOpenTimer.Stop(); volumeTooltip.IsOpen = false; };
        volume.PointerEntered += (_, args) =>
        {
            lastTooltipPointerPosition = args.GetPosition(volume);
            UpdateVolumeTooltipValue(volume, volumeTooltipText, lastTooltipPointerPosition);
            tooltipOpenTimer.Stop();
            tooltipOpenTimer.Start();
            RefreshFullscreenControls();
        };
        volume.PointerMoved += (_, args) =>
        {
            lastTooltipPointerPosition = args.GetPosition(volume);
            if (volumeTooltip.IsOpen)
                ShowVolumeTooltip(volume, volumeTooltip, volumeTooltipText, lastTooltipPointerPosition);
            else
                UpdateVolumeTooltipValue(volume, volumeTooltipText, lastTooltipPointerPosition);
            RefreshFullscreenControls();
        };
        volume.PointerExited += (_, _) =>
        {
            tooltipOpenTimer.Stop();
            volumeTooltip.IsOpen = false;
        };

        ConfigureVolumeSliderDragging(volume, dragging =>
        {
            if (ReferenceEquals(volume, _fullscreenMasterVolumeSlider))
                _isFullscreenVolumePointerDown = dragging;
            RefreshFullscreenControls();
        });

        volume.AddHandler(PointerWheelChangedEvent, (_, args) =>
        {
            if (!volume.IsPointerOver)
                return;

            var direction = Math.Sign(args.Delta.Y);
            if (direction == 0)
                return;

            if (_isFullscreen)
            {
                _isPointerInFullscreenRevealZone = true;
                BeginFullscreenScrollControlsGrace();
            }
            volume.Value = Math.Clamp(Math.Round(volume.Value) + direction, volume.Minimum, volume.Maximum);
            volumeTooltipText.Text = $"{volume.Value:0}%";
            RefreshFullscreenControls();
            args.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void MasterVolumeOnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_isApplyingMasterVolume)
            return;

        var value = Math.Clamp(eventArgs.NewValue, _masterVolumeSlider.Minimum, _masterVolumeSlider.Maximum);
        _isApplyingMasterVolume = true;
        try
        {
            _masterVolumeSlider.Value = value;
            _fullscreenMasterVolumeSlider.Value = value;
        }
        finally
        {
            _isApplyingMasterVolume = false;
        }

        _masterVolumeText.Text = $"{value:0}%";
        _fullscreenMasterVolumeText.Text = $"{value:0}%";
        if (value > 0) _lastPositiveMasterVolume = value;
        _muteAllButton.IsChecked = value == 0;
        var muteAction = value == 0 ? $"Restore master volume ({_lastPositiveMasterVolume:0}%)" : "Mute master volume";
        ToolTip.SetTip(_muteAllButton, muteAction);
        AutomationProperties.SetName(_muteAllButton, muteAction);
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
        if (_importPrompt.IsVisible)
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
        else if (_cropStartBox.IsFocused || _cropEndBox.IsFocused)
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
        if (_fullscreenMasterVolumeSlider.IsPointerOver)
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

        _masterVolumeSlider.Value = Math.Clamp(
            Math.Round(_masterVolumeSlider.Value) + direction,
            _masterVolumeSlider.Minimum,
            _masterVolumeSlider.Maximum);
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
            _videoHostCursorBeforeFullscreen = _videoHost.Cursor;
            _fullscreenControlsBarCursorBeforeFullscreen = _fullscreenControlsBar.Cursor;
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
        _fullscreenControlsPopup.IsOpen = false;
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
        _rootGrid.Margin = fullscreen ? new Thickness(0) : NormalRootMargin;
        _headerGrid.IsVisible = !fullscreen;
        _workspaceTabs.IsVisible = !fullscreen;
        _normalPlaybackArea.IsVisible = !fullscreen && !_exportTabSelected;
        _playerSidebar.IsVisible = !fullscreen && !_exportTabSelected;
        _workspaceGrid.ColumnSpacing = fullscreen ? 0 : 12;
        _workspaceGrid.RowSpacing = fullscreen ? 0 : 12;
        _workspaceGrid.ColumnDefinitions[1].Width = fullscreen ? new GridLength(0) : new GridLength(320);
        _videoBorder.BorderThickness = fullscreen ? new Thickness(0) : new Thickness(1);
        _videoBorder.CornerRadius = fullscreen ? new CornerRadius(0) : UiTheme.Resource<CornerRadius>("PanelRadius");
        Grid.SetColumnSpan(_videoBorder, fullscreen ? 2 : 1);
        UpdateImportPrompt();
        if (fullscreen) Dispatcher.UIThread.Post(UpdateFullscreenPopupGeometry);
    }

    private void ShowFullscreenControls()
    {
        if (!_isFullscreen)
            return;

        _fullscreenControlsPopup.IsOpen = true;
        UpdateFullscreenPopupGeometry();
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsTimer.Start();
    }

    private void HideFullscreenControls()
    {
        if (!_isFullscreen || _isFullscreenVolumePointerDown)
            return;

        _fullscreenControlsTimer.Stop();
        _fullscreenControlsPopup.IsOpen = false;
    }

    private void FullscreenControlsTimerOnTick(object? sender, EventArgs eventArgs)
    {
        if (_isFullscreen && (_fullscreenControlsBar.IsPointerOver || _isFullscreenTimelinePointerDown || _isFullscreenVolumePointerDown))
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

        if (_fullscreenControlsBar.IsPointerOver || _isFullscreenTimelinePointerDown || _isFullscreenVolumePointerDown)
        {
            _fullscreenCursorTimer.Stop();
            _fullscreenCursorTimer.Start();
            return;
        }

        Cursor = FullscreenHiddenCursor;
        _videoHost.Cursor = FullscreenHiddenCursor;
        _fullscreenControlsBar.Cursor = FullscreenHiddenCursor;
        _videoHost.SetNativeCursorVisible(false);
    }

    private void ShowFullscreenCursor()
    {
        if (!_isFullscreen)
            return;

        Cursor = _cursorBeforeFullscreen;
        _videoHost.Cursor = _videoHostCursorBeforeFullscreen;
        _fullscreenControlsBar.Cursor = _fullscreenControlsBarCursorBeforeFullscreen;
        _videoHost.SetNativeCursorVisible(true);
        _fullscreenCursorTimer.Stop();
        _fullscreenCursorTimer.Start();
    }

    private void RestoreFullscreenCursor()
    {
        _fullscreenCursorTimer.Stop();
        Cursor = _cursorBeforeFullscreen;
        _videoHost.Cursor = _videoHostCursorBeforeFullscreen;
        _fullscreenControlsBar.Cursor = _fullscreenControlsBarCursorBeforeFullscreen;
        _videoHost.SetNativeCursorVisible(true);
        _cursorBeforeFullscreen = null;
        _videoHostCursorBeforeFullscreen = null;
        _fullscreenControlsBarCursorBeforeFullscreen = null;
    }

    private void UpdateFullscreenPopupGeometry()
    {
        if (!_isFullscreen || _videoBorder.Bounds.Width <= 0)
            return;

        var availableWidth = Math.Max(0, _videoBorder.Bounds.Width - 32);
        var popupWidth = Math.Min(availableWidth,
            Math.Max(FullscreenControlMinimumWidth, availableWidth * FullscreenControlWidthRatio));

        _fullscreenControlsBar.Width = popupWidth;
        _fullscreenControlsBar.Height = FullscreenControlBarHeight;
        _fullscreenControlsPopup.VerticalOffset = -(FullscreenControlBarHeight + FullscreenControlBottomMargin);
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
                _fullscreenButton.IsEnabled = true;
                _fullscreenPlayPauseButton.IsEnabled = true;
            }

            _currentPos = Math.Clamp(snapshot.PositionSeconds, 0, Math.Max(0, _durationSeconds));
            _positionText.Text = FormatTime(snapshot.PositionSeconds);
            _speedText.Text = $"{snapshot.PlaybackSpeed:0.##}×";
            SetPlayPauseIcon(_playPauseButton, _playPauseIcon, snapshot.IsPaused);
            _fullscreenPositionText.Text = FormatTime(snapshot.PositionSeconds);
            _fullscreenDurationText.Text = FormatTime(snapshot.DurationSeconds);
            _fullscreenSpeedText.Text = $"{snapshot.PlaybackSpeed:0.##}×";
            SetPlayPauseIcon(_fullscreenPlayPauseButton, _fullscreenPlayPauseIcon, snapshot.IsPaused);
            if (!_isFullscreenTimelinePointerDown)
            {
                _isApplyingFullscreenSliderValue = true;
                _fullscreenTimelineSlider.Value = Math.Clamp(snapshot.PositionSeconds, 0, Math.Max(0, _durationSeconds));
                _isApplyingFullscreenSliderValue = false;
            }
            _playhead?.Let(playhead => Canvas.SetLeft(playhead, TimeToX(_currentPos)));
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
        _timelineCanvas.Width = 0;
        _timelineCanvas.Children.Clear();
        _timelineMarkers.Clear();
        _playhead = null;
        _cropHighlight = null;
        _cropStartThumb = null;
        _cropEndThumb = null;
        SetCropValues(0, 0);
        _timelineStartLabel.Text = "0:00";
        _timelineEndLabel.Text = "0:00";
        _positionText.Text = "00:00";
        _speedText.Text = "1×";
        _fullscreenSpeedText.Text = "1×";
        _fullscreenButton.IsEnabled = false;
        ResetFullscreenTimeline();
    }

    private void SetTimelineDuration(double duration)
    {
        _durationSeconds = Math.Max(0, duration);
        _timelineSized = _durationSeconds > 0;
        _timelineCanvas.Children.Clear();
        _timelineMarkers.Clear();
        _playhead = null;
        _cropHighlight = null;
        _cropStartThumb = null;
        _cropEndThumb = null;
        _timelineCanvas.Width = Math.Max(_durationSeconds * PixelsPerSecond, Math.Max(0, _timelineScroller.Viewport.Width - 100));
        _timelineStartLabel.Text = FormatTime(0);
        _timelineEndLabel.Text = FormatTime(_durationSeconds);
        SetFullscreenTimelineDuration(_durationSeconds);
        EnsurePlayhead();
        DrawMinuteMarkers();
        if (_cropToggle.IsChecked == true)
        {
            EnsureCropVisuals();
            UpdateCropVisuals();
        }
    }

    private void ResetFullscreenTimeline()
    {
        _isFullscreenTimelinePointerDown = false;
        _isApplyingFullscreenSliderValue = true;
        _fullscreenTimelineSlider.IsEnabled = false;
        _fullscreenTimelineSlider.Maximum = 1;
        _fullscreenTimelineSlider.Value = 0;
        _isApplyingFullscreenSliderValue = false;
        _fullscreenPositionText.Text = "00:00";
        _fullscreenDurationText.Text = "00:00";
        _fullscreenPlayPauseButton.IsEnabled = false;
    }

    private void SetFullscreenTimelineDuration(double duration)
    {
        _isApplyingFullscreenSliderValue = true;
        _fullscreenTimelineSlider.Maximum = Math.Max(1, duration);
        _fullscreenTimelineSlider.IsEnabled = duration > 0;
        _fullscreenTimelineSlider.Value = Math.Clamp(_currentPos, 0, Math.Max(0, duration));
        _isApplyingFullscreenSliderValue = false;
        _fullscreenDurationText.Text = FormatTime(duration);
        _fullscreenPlayPauseButton.IsEnabled = duration > 0;
    }

    private void TimelineScroller_SizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (_durationSeconds > 0)
        {
            _timelineCanvas.Width = Math.Max(_durationSeconds * PixelsPerSecond, Math.Max(0, eventArgs.NewSize.Width - 100));
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
            Height = _timelineCanvas.Height,
            Fill = UiTheme.Resource<IBrush>("TextPrimary"),
            IsHitTestVisible = false,
            ZIndex = 10
        };
        _timelineCanvas.Children.Add(_playhead);
        Canvas.SetLeft(_playhead, TimeToX(_currentPos));
    }

    private double TimeToX(double seconds) =>
        _durationSeconds <= 0 ? 0 : Math.Clamp(seconds, 0, _durationSeconds) / _durationSeconds * _timelineCanvas.Width;

    private double XToTime(double x) =>
        _timelineCanvas.Width <= 0 ? 0 : Math.Clamp(x, 0, _timelineCanvas.Width) / _timelineCanvas.Width * _durationSeconds;

    private void DrawMinuteMarkers()
    {
        var (first, last) = TimelineViewport.VisibleMinutes(_durationSeconds, _timelineCanvas.Width,
            _timelineScroller.Viewport.Width, _timelineScroller.Offset.X);
        foreach (var minute in _timelineMarkers.Keys.Where(m => m < first || m > last).ToArray())
        {
            var marker = _timelineMarkers[minute];
            _timelineCanvas.Children.Remove(marker.Line);
            _timelineCanvas.Children.Remove(marker.Label);
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
                _timelineCanvas.Children.Add(marker.Line);
                _timelineCanvas.Children.Add(marker.Label);
                marker.Label.Text = FormatTime(minute * 60d);
                marker.Label.IsVisible = minute != 0;
                Canvas.SetTop(marker.Label, 2);
            }
            var x = TimeToX(minute * 60d);
            marker.Line.Height = _timelineCanvas.Height;
            Canvas.SetLeft(marker.Line, x);
            Canvas.SetLeft(marker.Label, x + 3);
            if (minute == int.MaxValue) break;
        }
    }

    private void TimelineCanvas_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (_player is null || !eventArgs.GetCurrentPoint(_timelineCanvas).Properties.IsLeftButtonPressed)
            return;
        RunPlayerAction(player => player.SeekAsync(XToTime(eventArgs.GetPosition(_timelineCanvas).X)));
    }

    private void EnsureCropVisuals()
    {
        if (_cropHighlight is not null || _durationSeconds <= 0)
            return;

        _cropHighlight = new Rectangle
        {
            Height = _timelineCanvas.Height,
            Fill = UiTheme.Resource<IBrush>("CropSelectionBrush"),
            IsHitTestVisible = false,
            ZIndex = 4
        };
        _timelineCanvas.Children.Add(_cropHighlight);
        _cropStartThumb = MakeCropHandle();
        _cropEndThumb = MakeCropHandle();
        _cropStartThumb.DragDelta += CropStartThumbOnDragDelta;
        _cropEndThumb.DragDelta += CropEndThumbOnDragDelta;
        _timelineCanvas.Children.Add(_cropStartThumb);
        _timelineCanvas.Children.Add(_cropEndThumb);
    }

    private Thumb MakeCropHandle() => new()
    {
        Width = 8,
        Height = _timelineCanvas.Height,
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
        var visible = _cropToggle.IsChecked == true;
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
        _cropStartText.Text = _cropStartBox.Text = FormatTimeCode(_cropStart);
        _cropEndText.Text = _cropEndBox.Text = FormatTimeCode(_cropEnd);
        UpdateCropVisuals();
    }

    private void CropTimeBoxOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (sender is not TextBox box || box.IsReadOnly) return;
        if (eventArgs.Key == Key.Enter)
        {
            CommitCropTimeBox(box);
            _rootGrid.Focus();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            box.Text = FormatTimeCode(ReferenceEquals(box, _cropStartBox) ? _cropStart : _cropEnd);
            _rootGrid.Focus();
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
            if (ReferenceEquals(box, _cropStartBox)) SetCropValues(value, _cropEnd);
            else SetCropValues(_cropStart, value);
        }
        else box.Text = FormatTimeCode(ReferenceEquals(box, _cropStartBox) ? _cropStart : _cropEnd);
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

        var maximumOffset = Math.Max(0, _timelineScroller.Extent.Width - _timelineScroller.Viewport.Width);
        if (maximumOffset <= 0)
            return;

        var target = Math.Clamp(_timelineScroller.Offset.X + delta.Y * TimelineWheelPixelsPerStep,
            0, maximumOffset);
        _timelineScroller.Offset = new Vector(target, _timelineScroller.Offset.Y);
        eventArgs.Handled = true;
    }

    private void UpdatePlayheadIndicators()
    {
        if (_playhead is null || _durationSeconds <= 0)
        {
            _playheadLeftIndicator.IsVisible = false;
            _playheadRightIndicator.IsVisible = false;
            return;
        }

        var headX = 50 + TimeToX(_currentPos);
        var viewLeft = _timelineScroller.Offset.X;
        var viewRight = viewLeft + _timelineScroller.Viewport.Width;
        _playheadLeftIndicator.IsVisible = headX < viewLeft;
        _playheadRightIndicator.IsVisible = headX > viewRight;
    }

    private void PlayheadIndicator_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var target = Math.Max(0, 50 + TimeToX(_currentPos) - _timelineScroller.Viewport.Width / 2);
        _timelineScroller.Offset = new Vector(target, _timelineScroller.Offset.Y);
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
        _errorText.Text = message;
        _errorBanner.IsVisible = true;
        if (_durationSeconds <= 0)
        {
            _currentFilePath = null;
            _mediaNameText.Text = "Dino Player";
            UpdateImportPrompt();
        }
    }

    private void DismissErrorOnClick(object? sender, RoutedEventArgs eventArgs) => _errorBanner.IsVisible = false;

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
        _openMenuItem.IsEnabled = _importPrompt.IsEnabled = false;
        SetMediaControlsEnabled(false);
        _fullscreenMasterVolumeSlider.IsEnabled = false;
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
        _importPrompt.IsVisible = false;
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
        _importPrompt.IsVisible = false;
        _exportView.Dispose();
        AppLog.MessageWritten -= OnLogMessage;
        _logPump.Dispose();
        base.OnClosed(e);
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private static string FormatTimeCode(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
    }


}

internal static class ControlExtensions
{
    public static void Let<T>(this T? value, Action<T> action) where T : class
    {
        if (value is not null)
            action(value);
    }
}
