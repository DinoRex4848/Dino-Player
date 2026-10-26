using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DinoPlayer.MP.Controls;

// Shared input for every volume slider: a percentage preview above the pointer,
// press-and-drag positioning that uses the same percentage as the preview, and
// one-percent wheel steps.
internal static class VolumeSliderBehavior
{
    /// <param name="pointerActivity">Called when the pointer enters or moves over the slider.</param>
    /// <param name="dragChanged">Called with true while dragging and false when the drag ends.</param>
    /// <param name="wheelChanged">Called after a wheel step changes the value.</param>
    public static void Attach(Slider volume, Action? pointerActivity = null,
        Action<bool>? dragChanged = null, Action? wheelChanged = null)
    {
        var previewText = new TextBlock
        {
            Foreground = UiTheme.Resource<IBrush>("TextPrimary"),
            FontWeight = FontWeight.SemiBold
        };
        var preview = new HoverPopup(volume, previewText);
        var pointer = new Point();

        void UpdatePreviewText() => previewText.Text = $"{ValueAt(volume, pointer.X):0}%";

        // Only the X position follows the pointer, so small vertical movements
        // do not make the preview jump around.
        void PositionPreview()
        {
            UpdatePreviewText();
            preview.HorizontalOffset = pointer.X - volume.Bounds.Width / 2;
        }

        preview.Opening += PositionPreview;
        volume.PointerEntered += (_, args) =>
        {
            pointer = args.GetPosition(volume);
            UpdatePreviewText();
            pointerActivity?.Invoke();
        };
        volume.PointerMoved += (_, args) =>
        {
            pointer = args.GetPosition(volume);
            if (preview.IsOpen)
            {
                PositionPreview();
                preview.Open();
            }
            else UpdatePreviewText();
            pointerActivity?.Invoke();
        };

        AttachDragging(volume, dragChanged);

        volume.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) =>
        {
            if (!volume.IsPointerOver)
                return;

            var direction = Math.Sign(args.Delta.Y);
            if (direction == 0)
                return;

            volume.Value = Math.Clamp(Math.Round(volume.Value) + direction, volume.Minimum, volume.Maximum);
            previewText.Text = $"{volume.Value:0}%";
            wheelChanged?.Invoke();
            args.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal static double ValueAt(Slider volume, double pointerX)
    {
        if (volume.Bounds.Width <= 0)
            return volume.Value;

        var ratio = Math.Clamp(pointerX / volume.Bounds.Width, 0, 1);
        var value = volume.Minimum + ratio * (volume.Maximum - volume.Minimum);
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static void AttachDragging(Slider volume, Action<bool>? dragChanged)
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

        // Handle presses on both the track and thumb so a track click can
        // continue into a drag using the same percentage calculation.
        volume.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (dragPointer is not null || !args.GetCurrentPoint(volume).Properties.IsLeftButtonPressed)
                return;

            dragPointer = args.Pointer;
            args.Pointer.Capture(volume);
            volume.Value = ValueAt(volume, args.GetPosition(volume).X);
            dragChanged?.Invoke(true);
            args.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        volume.AddHandler(InputElement.PointerMovedEvent, (_, args) =>
        {
            if (!ReferenceEquals(dragPointer, args.Pointer))
                return;

            if (!args.GetCurrentPoint(volume).Properties.IsLeftButtonPressed)
            {
                EndDrag();
                return;
            }

            volume.Value = ValueAt(volume, args.GetPosition(volume).X);
            dragChanged?.Invoke(true);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        volume.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (!ReferenceEquals(dragPointer, args.Pointer) || args.InitialPressMouseButton != MouseButton.Left)
                return;

            volume.Value = ValueAt(volume, args.GetPosition(volume).X);
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
}
