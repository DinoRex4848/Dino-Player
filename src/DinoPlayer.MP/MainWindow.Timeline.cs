using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DinoPlayer.MP.Playback;

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

            SpeedText.Text = $"{snapshot.PlaybackSpeed:0.##}×";
            SetPlayPauseIcon(PlayPauseButton, PlayPauseIcon, snapshot.IsPaused);
            FullscreenDurationText.Text = TimeFormat.Clock(snapshot.DurationSeconds);
            FullscreenSpeedText.Text = $"{snapshot.PlaybackSpeed:0.##}×";
            SetPlayPauseIcon(FullscreenPlayPauseButton, FullscreenPlayPauseIcon, snapshot.IsPaused);
            ShowPosition(snapshot, Stopwatch.GetTimestamp());
            RequestPlayheadFrame(snapshot);

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

    // Moves the clock, playhead and fullscreen slider to the position shown now:
    // a drag in progress, a seek the player has not caught up with yet, or the
    // last sample advanced by the time since it was taken.
    private void ShowPosition(PlaybackSnapshot snapshot, long now, bool animationFrame = false)
    {
        var position = DisplayedPosition(snapshot, now);
        _currentPos = Math.Clamp(position, 0, Math.Max(0, _durationSeconds));
        var clock = TimeFormat.Clock(position);
        if (PositionText.Text != clock) PositionText.Text = FullscreenPositionText.Text = clock;
        // Frames only move the slider while it can be seen, by at least half a pixel.
        var slider = FullscreenTimelineSlider;
        if (!_isFullscreenTimelinePointerDown && (!animationFrame || (_isFullscreen &&
            Math.Abs(_currentPos - slider.Value) * slider.Bounds.Width >= 0.5 * Math.Max(1, slider.Maximum))))
        {
            _isApplyingFullscreenSliderValue = true;
            slider.Value = _currentPos;
            _isApplyingFullscreenSliderValue = false;
        }
        if (_playhead is not null && _playheadShift is not null)
        {
            var x = TimeToX(_currentPos);
            var left = Canvas.GetLeft(_playhead);
            if (animationFrame && !double.IsNaN(left))
            {
                // Between regular updates the playhead slides with a render
                // transform: moving Canvas.Left every frame costs a layout pass.
                if (Math.Abs(x - left - _playheadShift.X) >= 0.25) _playheadShift.X = x - left;
            }
            else
            {
                if (double.IsNaN(left) || Math.Abs(left - x) >= 0.25) Canvas.SetLeft(_playhead, x);
                _playheadShift.X = 0;
            }
        }
        UpdatePlayheadIndicators();
    }

    private double DisplayedPosition(PlaybackSnapshot snapshot, long now)
    {
        if (_scrubPosition is { } scrub) return scrub;
        if (_seekDisplayHold is { } hold)
        {
            // Wait for a sample taken after the seek that landed near its target.
            var caughtUp = snapshot.SampleTimestamp > hold.Since && !snapshot.IsSeeking &&
                           Math.Abs(snapshot.PositionSeconds - hold.Target) < SeekDisplayTolerance;
            if (!caughtUp && Stopwatch.GetElapsedTime(hold.Since, now) < SeekDisplayTimeout) return hold.Target;
            _seekDisplayHold = null;
        }
        return snapshot.EstimatePosition(now);
    }

    // While playing, redraw the playhead on every display frame instead of only
    // when the 200 ms timer or a native event refreshes the UI.
    private void RequestPlayheadFrame(PlaybackSnapshot snapshot)
    {
        if (!ShouldAnimatePlayhead(snapshot)) return;
        // A request that never ran (for example while minimized) is replaced.
        if (_playheadFrameRequestedAt != 0 &&
            Stopwatch.GetElapsedTime(_playheadFrameRequestedAt) < PlayheadFrameWatchdog) return;
        var token = ++_playheadFrameToken;
        _playheadFrameRequestedAt = Stopwatch.GetTimestamp();
        RequestAnimationFrame(_ => OnPlayheadFrame(token));
    }

    private void OnPlayheadFrame(int token)
    {
        if (token != _playheadFrameToken) return;
        _playheadFrameRequestedAt = 0;
        if (_player is not { } player) return;
        var snapshot = player.GetSnapshot();
        if (snapshot.Generation != player.Generation || !ShouldAnimatePlayhead(snapshot)) return;
        ShowPosition(snapshot, Stopwatch.GetTimestamp(), animationFrame: true);
        RequestPlayheadFrame(snapshot);
    }

    private bool ShouldAnimatePlayhead(PlaybackSnapshot snapshot) =>
        !_closing && !_closed && !_exportTabSelected && WindowState != WindowState.Minimized &&
        snapshot.IsLoaded && !snapshot.IsPaused && !snapshot.IsSeeking && _durationSeconds > 0 &&
        _scrubPosition is null;

    private void ResetTimeline()
    {
        _durationSeconds = 0;
        _currentPos = 0;
        _timelineSized = false;
        EndScrub();
        _seekDisplayHold = null;
        TimelineCanvas.Width = 0;
        TimelineCanvas.Children.Clear();
        _timelineMarkers.Clear();
        _playhead = null;
        _playheadShift = null;
        _cropHighlight = null;
        _cropStartThumb = null;
        _cropEndThumb = null;
        SetCropValues(0, 0);
        TimelineStartLabel.Text = TimelineEndLabel.Text = TimeFormat.Clock(0);
        PositionText.Text = "00:00";
        SpeedText.Text = "1×";
        FullscreenSpeedText.Text = "1×";
        FullscreenButton.IsEnabled = false;
        ResetFullscreenTimeline();
    }

    // Updates the existing timeline in place, so a changing duration estimate
    // does not rebuild the canvas or interrupt a crop handle drag.
    private void SetTimelineDuration(double duration)
    {
        _durationSeconds = Math.Max(0, duration);
        _timelineSized = _durationSeconds > 0;
        TimelineStartLabel.Text = TimeFormat.Clock(0);
        TimelineEndLabel.Text = TimeFormat.Clock(_durationSeconds);
        SetFullscreenTimelineDuration(_durationSeconds);
        EnsurePlayhead();
        if (CropToggle.IsChecked == true)
            EnsureCropVisuals();
        FitTimeline(TimelineScroller.Viewport.Width);
    }

    private void TimelineScroller_SizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (_durationSeconds > 0)
            FitTimeline(eventArgs.NewSize.Width);
        UpdatePlayheadIndicators();
    }

    // Sizes the canvas for the duration (at least the visible width) and moves
    // the minute markers, playhead and crop handles to match.
    private void FitTimeline(double viewportWidth)
    {
        TimelineCanvas.Width = Math.Max(_durationSeconds * PixelsPerSecond,
            Math.Max(0, viewportWidth - 2 * TimelineViewport.LabelGutter));
        DrawMinuteMarkers();
        if (_playhead is not null)
            Canvas.SetLeft(_playhead, TimeToX(_currentPos));
        if (_playheadShift is not null)
            _playheadShift.X = 0;
        UpdateCropVisuals();
    }

    private void EnsurePlayhead()
    {
        if (_playhead is not null)
            return;

        _playheadShift = new TranslateTransform();
        _playhead = new Rectangle
        {
            Width = 2,
            Height = TimelineCanvas.Height,
            Fill = UiTheme.Resource<IBrush>("TextPrimary"),
            IsHitTestVisible = false,
            ZIndex = 10,
            RenderTransform = _playheadShift
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

    // An exact seek from the UI: show its target at once and keep showing it
    // until the player reports a position there.
    private void SeekToPosition(double seconds)
    {
        if (_player is not { } player || _durationSeconds <= 0) return;
        var target = Math.Clamp(seconds, 0, _durationSeconds);
        var now = Stopwatch.GetTimestamp();
        _seekDisplayHold = (target, now);
        ShowPosition(player.GetSnapshot(), now);
        RunPlayerAction(engine => engine.SeekAsync(target));
    }

    // Dragging previews with fast keyframe seeks, at most one per
    // ScrubSeekInterval, then CompleteScrub lands exactly where the drag ended.
    private void UpdateScrub(double seconds)
    {
        if (_player is not { } player || _durationSeconds <= 0) return;
        var target = Math.Clamp(seconds, 0, _durationSeconds);
        _seekDisplayHold = null;
        _scrubPosition = target;
        _scrubSeekPending = target;
        ShowPosition(player.GetSnapshot(), Stopwatch.GetTimestamp());
        if (!_scrubSeekTimer.IsEnabled) SendScrubSeek();
    }

    private void SendScrubSeek()
    {
        _scrubSeekTimer.Stop();
        if (_scrubSeekPending is not { } target || target == _scrubSeekSent) return;
        _scrubSeekSent = target;
        RunPlayerAction(player => player.SeekPreviewAsync(target));
        _scrubSeekTimer.Start();
    }

    private void ScrubSeekTimerOnTick(object? sender, EventArgs eventArgs) => SendScrubSeek();

    private void CompleteScrub()
    {
        if (_scrubPosition is not { } target) return;
        EndScrub();
        SeekToPosition(target);
    }

    private void EndScrub()
    {
        _scrubSeekTimer.Stop();
        _scrubPosition = _scrubSeekPending = _scrubSeekSent = null;
    }

    // A click seeks exactly at once; moving a few pixels turns it into a scrub.
    private void TimelineCanvas_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (_player is null || _durationSeconds <= 0 || !eventArgs.GetCurrentPoint(TimelineCanvas).Properties.IsLeftButtonPressed)
            return;
        var x = eventArgs.GetPosition(TimelineCanvas).X;
        _timelinePressX = x;
        _isTimelinePointerDown = true;
        eventArgs.Pointer.Capture(TimelineCanvas);
        SeekToPosition(XToTime(x));
        eventArgs.Handled = true;
    }

    private void TimelineCanvas_PointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_isTimelinePointerDown) return;
        var x = eventArgs.GetPosition(TimelineCanvas).X;
        if (_scrubPosition is null && Math.Abs(x - _timelinePressX) < TimelineDragThreshold) return;
        UpdateScrub(XToTime(x));
    }

    private void TimelineCanvas_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_isTimelinePointerDown) return;
        _isTimelinePointerDown = false;
        eventArgs.Pointer.Capture(null);
        CompleteScrub();
    }

    private void TimelineCanvas_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        if (!_isTimelinePointerDown) return;
        _isTimelinePointerDown = false;
        CompleteScrub();
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
