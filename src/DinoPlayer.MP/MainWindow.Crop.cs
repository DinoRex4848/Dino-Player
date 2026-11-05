using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DinoPlayer.MP;

// Crop selection: timeline handles, Set Start/End and editable timecodes.
public sealed partial class MainWindow
{
    private const double DefaultCropSeconds = 10;

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
        _cropStartThumb.DragCompleted += CropThumbOnDragCompleted;
        _cropEndThumb.DragCompleted += CropThumbOnDragCompleted;
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
        // Keep the earlier selection for this video. Otherwise select ten
        // seconds from the playhead, starting earlier if the video ends sooner.
        if (_cropEnd <= _cropStart)
        {
            var start = Math.Clamp(_currentPos, 0, Math.Max(0, _durationSeconds - DefaultCropSeconds));
            SetCropValues(start, Math.Min(start + DefaultCropSeconds, _durationSeconds));
        }
        SetCropVisualsVisible(true);
        UpdateCropVisuals();
    }

    private void SetCropVisualsVisible(bool visible)
    {
        if (_cropHighlight is not null) _cropHighlight.IsVisible = visible;
        if (_cropStartThumb is not null) _cropStartThumb.IsVisible = visible;
        if (_cropEndThumb is not null) _cropEndThumb.IsVisible = visible;
    }

    // Dragging a handle shows the frame at that boundary.
    private void CropStartThumbOnDragDelta(object? sender, VectorEventArgs eventArgs)
    {
        SetCropValues(XToTime(TimeToX(_cropStart) + eventArgs.Vector.X), _cropEnd);
        UpdateScrub(_cropStart);
    }

    private void CropEndThumbOnDragDelta(object? sender, VectorEventArgs eventArgs)
    {
        SetCropValues(_cropStart, XToTime(TimeToX(_cropEnd) + eventArgs.Vector.X));
        UpdateScrub(_cropEnd);
    }

    private void CropThumbOnDragCompleted(object? sender, VectorEventArgs eventArgs) => CompleteScrub();

    private async void SetCropStartToCurrent()
    {
        if (await ReadCurrentPositionAsync() is { } position) SetCropStart(position);
    }

    private async void SetCropEndToCurrent()
    {
        if (await ReadCurrentPositionAsync() is { } position) SetCropEnd(position);
    }

    private void SetCropStart(double position)
    {
        if (position <= _cropEnd)
            SetCropValues(position, _cropEnd);
        else
            SetCropValues(Math.Min(position, _durationSeconds), Math.Min(position + DefaultCropSeconds, _durationSeconds));
    }

    private void SetCropEnd(double position)
    {
        if (position >= _cropStart)
            SetCropValues(_cropStart, position);
        else
            SetCropValues(Math.Max(0, position - DefaultCropSeconds), position);
    }

    // I and O: mark the start or end, turning Crop on first. With no earlier
    // selection, the first mark selects ten seconds after a start or before an end.
    private async void MarkCropFromShortcut(bool start)
    {
        if (_durationSeconds <= 0 || !CropToggle.IsEnabled) return;
        if (await ReadCurrentPositionAsync() is not { } position || _durationSeconds <= 0) return;
        var hadSelection = _cropEnd > _cropStart;
        CropToggle.IsChecked = true;
        if (!hadSelection)
            SetCropValues(start ? position : Math.Max(0, position - DefaultCropSeconds),
                start ? Math.Min(position + DefaultCropSeconds, _durationSeconds) : position);
        else if (start) SetCropStart(position);
        else SetCropEnd(position);
    }

    // The cached position can be a few hundred milliseconds old while playing,
    // so crop points ask the player. A drag in progress wins.
    private async Task<double?> ReadCurrentPositionAsync()
    {
        if (_scrubPosition is { } scrub) return scrub;
        if (_player is not { } player || _durationSeconds <= 0) return null;
        var generation = player.Generation;
        double position;
        try { position = await player.GetPositionAsync(); }
        catch (Exception exception)
        {
            AppLog.Warn($"Could not read the playback position: {exception.Message}");
            position = _currentPos;
        }
        return IsCurrentMedia(player, generation) && _durationSeconds > 0
            ? Math.Clamp(position, 0, _durationSeconds)
            : null;
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
}
