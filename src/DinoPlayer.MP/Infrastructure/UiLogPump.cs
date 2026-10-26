using Avalonia.Controls;
using Avalonia.Threading;

namespace DinoPlayer.MP;

// Collects log lines continuously and copies them into the diagnostic TextBox
// only while it is shown. Rebuilding up to 256 KB of text for a hidden control
// would allocate large strings on every refresh.
internal sealed class UiLogPump : IDisposable
{
    private readonly BoundedLogBuffer _buffer = new();
    private readonly DispatcherTimer _timer;
    private readonly TextBox _target;
    private bool _isShown;

    public UiLogPump(TextBox target)
    {
        _target = target;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Flush())
        {
            IsEnabled = false
        };
    }

    /// <summary>Whether the log is visible. Showing it refreshes the text immediately.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value) return;
            _isShown = value;
            _timer.IsEnabled = value;
            if (value) Flush();
        }
    }

    public void Append(string line) => _buffer.Append(line);
    public void Clear() { _buffer.Clear(); Flush(); }

    public void Flush()
    {
        if (!_isShown || _buffer.TakeSnapshot() is not { } text) return;
        _target.Text = text;
        _target.CaretIndex = text.Length;
    }

    public void Dispose() { _timer.Stop(); _buffer.Stop(); }
}
