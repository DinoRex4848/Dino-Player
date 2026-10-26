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

    private void CropStartThumbOnDragDelta(object? sender, VectorEventArgs eventArgs) =>
        SetCropValues(XToTime(TimeToX(_cropStart) + eventArgs.Vector.X), _cropEnd);

    private void CropEndThumbOnDragDelta(object? sender, VectorEventArgs eventArgs) =>
        SetCropValues(_cropStart, XToTime(TimeToX(_cropEnd) + eventArgs.Vector.X));

    private void SetCropStartToCurrent()
    {
        if (_currentPos <= _cropEnd)
            SetCropValues(_currentPos, _cropEnd);
        else
            SetCropValues(Math.Min(_currentPos, _durationSeconds), Math.Min(_currentPos + DefaultCropSeconds, _durationSeconds));
    }

    private void SetCropEndToCurrent()
    {
        if (_currentPos >= _cropStart)
            SetCropValues(_cropStart, _currentPos);
        else
            SetCropValues(Math.Max(0, _currentPos - DefaultCropSeconds), _currentPos);
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
