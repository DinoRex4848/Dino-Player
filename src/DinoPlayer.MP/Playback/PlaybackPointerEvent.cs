namespace DinoPlayer.MP.Playback;

public enum PlaybackPointerEventKind
{
    Move,
    Enter,
    Leave,
    DoubleClick,
    Wheel,
    RightClick
}

public readonly record struct PlaybackPointerEvent(
    PlaybackPointerEventKind Kind,
    double X,
    double Y,
    double SurfaceHeight,
    double WheelDelta = 0);
