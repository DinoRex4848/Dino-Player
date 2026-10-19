using Avalonia.Controls;
using Avalonia.Threading;

namespace DinoPlayer.MP;

internal sealed class UiLogPump : IDisposable
{
    private readonly BoundedLogBuffer _buffer = new();
    private readonly DispatcherTimer _timer;
    private readonly TextBox _target;

    public UiLogPump(TextBox target)
    {
        _target = target;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Flush());
        _timer.Start();
    }

    public void Append(string line) => _buffer.Append(line);
    public void Clear() { _buffer.Clear(); Flush(); }
    public void Flush()
    {
        if (_buffer.TakeSnapshot() is not { } text) return;
        _target.Text = text;
        _target.CaretIndex = text.Length;
    }
    public void Dispose() { _timer.Stop(); _buffer.Stop(); }
}
