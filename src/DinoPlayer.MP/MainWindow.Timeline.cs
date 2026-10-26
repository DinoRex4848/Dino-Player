using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace DinoPlayer.MP;

// Playback position display and the scrollable timeline canvas.
public sealed partial class MainWindow
{
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
}
