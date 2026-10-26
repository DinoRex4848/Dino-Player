using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DinoPlayer.MP.Playback;
using DinoPlayer.MP.Settings;

namespace DinoPlayer.MP;

// Keyboard shortcuts and pointer input from the native video surface.
public sealed partial class MainWindow
{
    // Pointer input arrives from the Windows host bridge or the mpv Lua bridge,
    // off the UI thread. Input from a replaced engine is ignored.
    private void PostPointerInput(PlaybackPointerEvent pointerEvent, IPlaybackEngine? source = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || _closed || (source is not null && !ReferenceEquals(_player, source))) return;
            HandlePlaybackPointerInput(pointerEvent);
        }, DispatcherPriority.Input);

    private void HandlePlaybackPointerInput(PlaybackPointerEvent pointerEvent)
    {
        if (_exportTabSelected) return;
        if (pointerEvent.Kind == PlaybackPointerEventKind.DoubleClick)
        {
            var canToggleFullscreen = CanUseFullscreen || _isFullscreen;
            AppLog.Info($"Video double-click received: fullscreen={_isFullscreen}; fullscreen-available={canToggleFullscreen}");
            if (canToggleFullscreen)
                ToggleFullscreen();
            return;
        }

        // The native mpv surface owns pointer input, so a wheel event here
        // means the pointer is over the video play area in normal mode.
        if (!_isFullscreen && pointerEvent.Kind == PlaybackPointerEventKind.Wheel)
        {
            AdjustMasterVolume(pointerEvent.WheelDelta);
            return;
        }

        if (!_isFullscreen)
            return;

        if (pointerEvent.Kind is PlaybackPointerEventKind.Move or PlaybackPointerEventKind.Enter or PlaybackPointerEventKind.Wheel)
            ShowFullscreenCursor();

        if (pointerEvent.Kind == PlaybackPointerEventKind.Wheel)
        {
            UpdateFullscreenPointerRevealZone(pointerEvent.Y, pointerEvent.SurfaceHeight);
            BeginFullscreenScrollControlsGrace();
            AdjustMasterVolume(pointerEvent.WheelDelta);
            ShowFullscreenControls();
            return;
        }

        if (pointerEvent.Kind == PlaybackPointerEventKind.RightClick)
        {
            ShowFullscreenCursor();
            ShowFullscreenControls();
            RunPlayerAction(player => player.TogglePauseAsync());
            return;
        }

        if (pointerEvent.Kind is not (PlaybackPointerEventKind.Move or PlaybackPointerEventKind.Enter))
            return;

        var isInRevealZone = pointerEvent.SurfaceHeight > 0 &&
                             pointerEvent.Y >= pointerEvent.SurfaceHeight * (1 - FullscreenPointerRevealZoneRatio);
        _isPointerInFullscreenRevealZone = isInRevealZone;
        if (_fullscreenScrollGraceActive)
        {
            if (isInRevealZone)
                ShowFullscreenControls();
            return;
        }

        if (isInRevealZone)
            ShowFullscreenControls();
        else
            HideFullscreenControls();
    }

    private void Skip(double seconds)
    {
        if (_player is null)
            return;

        if (_isFullscreen)
        {
            ShowFullscreenControls();
            BeginFullscreenScrollControlsGrace();
        }
        RunPlayerAction(player => player.SeekRelativeAsync(seconds));
    }

    private void MainWindowOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        // The popup's event route can also pass through the main window.
        if (eventArgs.Handled) return;
        if (ImportPrompt.IsVisible)
        {
            if (eventArgs.Key is Key.Enter or Key.Space)
            {
                eventArgs.Handled = true;
                OpenButtonOnClick(sender, new RoutedEventArgs());
            }
            return;
        }
        if (_exportTabSelected) return;
        var shortcuts = SettingsStore.GetPlaybackShortcuts();
        if (eventArgs.Key == Key.F11)
        {
            ToggleFullscreen();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape && _isFullscreen)
        {
            SetFullscreen(false);
            eventArgs.Handled = true;
        }
        else if (_player is { } player && IsPreparingMedia(player))
        {
            return;
        }
        else if (CropStartBox.IsFocused || CropEndBox.IsFocused)
        {
            return;
        }
        else if (PlaybackShortcut.Matches(shortcuts.SpeedUpKey,
                     shortcuts.SpeedUpModifiers, eventArgs))
        {
            if (_isFullscreen)
                ShowFullscreenControls();
            RunPlayerAction(player => player.AdjustPlaybackSpeedAsync(1));
            eventArgs.Handled = true;
        }
        else if (PlaybackShortcut.Matches(shortcuts.SpeedDownKey,
                     shortcuts.SpeedDownModifiers, eventArgs))
        {
            if (_isFullscreen)
                ShowFullscreenControls();
            RunPlayerAction(player => player.AdjustPlaybackSpeedAsync(-1));
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Space)
        {
            if (_isFullscreen)
                ShowFullscreenControls();
            RunPlayerAction(player => player.TogglePauseAsync());
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Left)
        {
            Skip(-5);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Right)
        {
            Skip(5);
            eventArgs.Handled = true;
        }
    }
}
