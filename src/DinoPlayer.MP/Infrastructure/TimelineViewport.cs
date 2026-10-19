namespace DinoPlayer.MP;

internal static class TimelineViewport
{
    // The timeline begins after the 50-pixel start label. Include one viewport
    // on either side so scrolling reuses markers without visible pop-in.
    public static (int First, int Last) VisibleMinutes(double duration, double canvasWidth,
        double viewportWidth, double offset)
    {
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(canvasWidth) || canvasWidth <= 0 ||
            !double.IsFinite(viewportWidth) || viewportWidth <= 0 || !double.IsFinite(offset)) return (0, -1);
        var left = Math.Clamp(offset - viewportWidth - 50, 0, canvasWidth);
        var right = Math.Clamp(offset + viewportWidth * 2 - 50, 0, canvasWidth);
        var first = (int)Math.Min(int.MaxValue, Math.Floor(left / canvasWidth * duration / 60));
        var last = (int)Math.Min(int.MaxValue, Math.Floor(right / canvasWidth * duration / 60));
        return (first, last);
    }
}
