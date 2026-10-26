using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Media;
using Avalonia.Threading;

namespace DinoPlayer.MP.Controls;

// A small styled popup that opens above a control after the pointer rests on it,
// and closes when the pointer leaves. Unlike ToolTip, callers can move it to
// follow the pointer and update its text while it is open.
internal sealed class HoverPopup
{
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(500);
    private readonly Control _target;
    private readonly Popup _popup;
    private readonly DispatcherTimer _openTimer = new() { Interval = OpenDelay };

    public HoverPopup(Control target, Control content)
    {
        _target = target;
        _popup = new Popup
        {
            PlacementTarget = target,
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
                Child = content
            }
        };
        _openTimer.Tick += (_, _) =>
        {
            _openTimer.Stop();
            if (!_target.IsPointerOver) return;
            Opening?.Invoke();
            Open();
        };
        target.PointerEntered += (_, _) =>
        {
            _openTimer.Stop();
            _openTimer.Start();
        };
        target.PointerExited += (_, _) => Close();
        target.DetachedFromVisualTree += (_, _) => Close();
    }

    /// <summary>Raised when the hover delay elapses, just before the popup opens.</summary>
    public event Action? Opening;

    public bool IsOpen => _popup.IsOpen;

    public double HorizontalOffset
    {
        get => _popup.HorizontalOffset;
        set => _popup.HorizontalOffset = value;
    }

    public void Open()
    {
        if (_target.Bounds.Width > 0)
            _popup.IsOpen = true;
    }

    public void Close()
    {
        _openTimer.Stop();
        _popup.IsOpen = false;
    }

    public static HoverPopup ForText(Control target, string message) => new(target, new TextBlock
    {
        Text = message,
        Foreground = UiTheme.Resource<IBrush>("TextPrimary"),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 300
    });
}
