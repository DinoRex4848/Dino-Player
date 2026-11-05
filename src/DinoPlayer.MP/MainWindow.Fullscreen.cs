using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;

namespace DinoPlayer.MP;

// Fullscreen layout, the auto-hiding control bar and cursor.
public sealed partial class MainWindow
{
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
        CompleteScrub();
        if (_isFullscreen)
            ShowFullscreenControls();
    }

    private void FullscreenTimelineSliderOnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        _isFullscreenTimelinePointerDown = false;
        CompleteScrub();
    }

    // Clicks seek exactly; drags preview and land exactly on release.
    private void FullscreenTimelineSliderOnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_isApplyingFullscreenSliderValue || !_isFullscreen || _durationSeconds <= 0)
            return;

        ShowFullscreenControls();
        if (_isFullscreenTimelinePointerDown) UpdateScrub(eventArgs.NewValue);
        else SeekToPosition(eventArgs.NewValue);
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
        if (_isFullscreenTimelinePointerDown)
        {
            _isFullscreenTimelinePointerDown = false;
            CompleteScrub();
        }
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
}
