using System.Text;

namespace DinoPlayer.MP;

internal sealed class BoundedLogBuffer
{
    internal const int MaxLines = 2000;
    internal const int MaxBytes = 256 * 1024;
    private readonly object _sync = new();
    private readonly Queue<(string Text, int Bytes)> _lines = new();
    private int _bytes;
    private bool _dirty;
    private bool _stopped;

    public void Append(string message)
    {
        lock (_sync)
        {
            if (_stopped) return;
            using var reader = new StringReader(message);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > MaxBytes / 4) line = line[..(MaxBytes / 4)] + " [truncated]";
                line += Environment.NewLine;
                var size = Math.Max(Encoding.UTF8.GetByteCount(line), line.Length * sizeof(char));
                while (_lines.Count > 0 && (_lines.Count >= MaxLines || _bytes + size > MaxBytes))
                    _bytes -= _lines.Dequeue().Bytes;
                _lines.Enqueue((line, size));
                _bytes += size;
                _dirty = true;
            }
        }
    }

    public string? TakeSnapshot()
    {
        lock (_sync)
        {
            if (!_dirty || _stopped) return null;
            _dirty = false;
            return string.Concat(_lines.Select(line => line.Text));
        }
    }

    public void Clear()
    {
        lock (_sync) { _lines.Clear(); _bytes = 0; _dirty = true; }
    }

    public void Stop()
    {
        lock (_sync) { _stopped = true; _lines.Clear(); _bytes = 0; }
    }
}
