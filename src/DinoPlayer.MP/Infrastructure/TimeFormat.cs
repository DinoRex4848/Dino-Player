namespace DinoPlayer.MP;

internal static class TimeFormat
{
    /// <summary>Player clock: 05:07, or 1:05:07 from one hour.</summary>
    public static string Clock(double seconds)
    {
        var time = ToTimeSpan(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    /// <summary>Clock with any milliseconds, trailing zeros removed: 05:07.25.</summary>
    public static string ClockWithFraction(double seconds)
    {
        var time = ToTimeSpan(seconds);
        var fraction = time.Milliseconds == 0 ? "" : $".{time.Milliseconds:000}".TrimEnd('0');
        return Clock(seconds) + fraction;
    }

    /// <summary>Editable crop timecode: 00:05:07.250.</summary>
    public static string TimeCode(double seconds)
    {
        var time = ToTimeSpan(seconds);
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
    }

    private static TimeSpan ToTimeSpan(double seconds) =>
        TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
}
