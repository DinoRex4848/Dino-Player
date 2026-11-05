using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using DinoPlayer.MP.Controls;
using DinoPlayer.MP.Playback;
using DinoPlayer.MP.Settings;

namespace DinoPlayer.MP;

// Audio track controls, saved per-track-count presets and master volume.
public sealed partial class MainWindow
{
    private void BuildAudioTrackControls()
    {
        AudioTracksPanel.Children.Clear();
        var tracks = _player?.AudioTracks ?? Array.Empty<AudioTrack>();
        MuteAllButton.IsEnabled = tracks.Count > 0;

        if (tracks.Count == 0)
        {
            AudioTracksPanel.Children.Add(new TextBlock
            {
                Text = "No audio tracks discovered. Video remains available and audio is muted.",
                Foreground = UiTheme.Resource<IBrush>("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var track in tracks)
        {
            var enabled = new CheckBox { Content = track.Label, IsChecked = track.Enabled };
            enabled.IsCheckedChanged += (_, _) =>
            {
                track.Enabled = enabled.IsChecked == true;
                QueueMixUpdate();
                QueueAudioPresetSave();
            };

            var volumeValue = new TextBlock
            {
                Text = $"{track.VolumePercent:0}%",
                Width = 42,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var volume = new Slider { Minimum = 0, Maximum = 150, Value = track.VolumePercent, TickFrequency = 5 };
            volume.ValueChanged += (_, args) =>
            {
                track.VolumePercent = args.NewValue;
                volumeValue.Text = $"{track.VolumePercent:0}%";
                QueueMixUpdate();
                QueueAudioPresetSave();
            };

            VolumeSliderBehavior.Attach(volume);

            var volumeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            volumeRow.Children.Add(volume);
            Grid.SetColumn(volumeValue, 1);
            volumeRow.Children.Add(volumeValue);

            var trackPanel = new StackPanel { Spacing = 3 };
            trackPanel.Children.Add(enabled);
            trackPanel.Children.Add(volumeRow);
            AudioTracksPanel.Children.Add(trackPanel);
        }
    }

    private void ToggleMasterMute()
    {
        if (_player is null)
            return;

        MasterVolumeSlider.Value = MasterVolumeSlider.Value > 0 ? 0 : _lastPositiveMasterVolume;
    }

    private void RestoreAudioPreset()
    {
        var tracks = _player?.AudioTracks;
        if (tracks is null || tracks.Count == 0)
            return;

        var preset = SettingsStore.FindAudioTrackPreset(tracks.Count);
        if (preset is null)
            return;

        if (preset.Tracks is null || preset.TrackCount != tracks.Count || preset.Tracks.Count != tracks.Count)
        {
            AppLog.Warn($"Ignoring audio preset for {tracks.Count} tracks because its settings are missing or have the wrong count.");
            return;
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var savedTrack = preset.Tracks[index];
            if (savedTrack is null || !double.IsFinite(savedTrack.VolumePercent))
            {
                AppLog.Warn($"Ignoring audio preset for {tracks.Count} tracks because track {index + 1} has an invalid volume.");
                return;
            }
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var savedTrack = preset.Tracks[index];
            tracks[index].Enabled = savedTrack.Enabled;
            tracks[index].VolumePercent = Math.Clamp(savedTrack.VolumePercent, 0, 150);
        }

        AppLog.Info($"Restored audio preset for {tracks.Count} tracks.");
    }

    private void QueueAudioPresetSave()
    {
        _audioPresetTimer.Stop();
        if (_player?.AudioTracks.Count > 0)
        {
            _audioPresetChanged = true;
            _audioPresetTimer.Start();
        }
    }

    private void SavePendingAudioPreset(object? sender, EventArgs eventArgs) => FlushAudioPreset();

    // Writes settings.json only when a track setting changed since the last save.
    private void FlushAudioPreset()
    {
        _audioPresetTimer.Stop();
        if (!_audioPresetChanged) return;
        _audioPresetChanged = false;
        SaveCurrentAudioPreset();
    }

    private void SaveCurrentAudioPreset()
    {
        var tracks = _player?.AudioTracks;
        if (tracks is null || tracks.Count == 0)
            return;

        try
        {
            SettingsStore.AddOrUpdateAudioTrackPreset(new AudioTrackPreset
            {
                TrackCount = tracks.Count,
                Tracks = tracks.Select(track => new AudioTrackSetting
                {
                    Enabled = track.Enabled,
                    VolumePercent = Math.Clamp(track.VolumePercent, 0, 150)
                }).ToList()
            });
            AppLog.Info($"Saved audio preset for {tracks.Count} tracks.");
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Could not save audio preset: {exception.Message}");
        }
    }

    // Sends the latest track settings at most once per interval, also while a
    // slider is still moving: volume changes apply live without a graph rebuild.
    private void QueueMixUpdate()
    {
        if (!_mixTimer.IsEnabled) _mixTimer.Start();
    }

    private void ApplyPendingMix(object? sender, EventArgs eventArgs)
    {
        _mixTimer.Stop();
        RunPlayerAction(player => player.ApplyAudioMixAsync());
    }

    private void ConfigureMasterVolumeSlider(Slider volume)
    {
        void RefreshFullscreenControls()
        {
            if (_isFullscreen)
            {
                ShowFullscreenCursor();
                ShowFullscreenControls();
            }
        }

        VolumeSliderBehavior.Attach(volume,
            pointerActivity: RefreshFullscreenControls,
            dragChanged: dragging =>
            {
                if (ReferenceEquals(volume, FullscreenMasterVolumeSlider))
                    _isFullscreenVolumePointerDown = dragging;
                RefreshFullscreenControls();
            },
            wheelChanged: () =>
            {
                if (_isFullscreen)
                {
                    _isPointerInFullscreenRevealZone = true;
                    BeginFullscreenScrollControlsGrace();
                }
                RefreshFullscreenControls();
            });
    }

    private void MasterVolumeOnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_isApplyingMasterVolume)
            return;

        var value = Math.Clamp(eventArgs.NewValue, MasterVolumeSlider.Minimum, MasterVolumeSlider.Maximum);
        _isApplyingMasterVolume = true;
        try
        {
            MasterVolumeSlider.Value = value;
            FullscreenMasterVolumeSlider.Value = value;
        }
        finally
        {
            _isApplyingMasterVolume = false;
        }

        MasterVolumeText.Text = $"{value:0}%";
        FullscreenMasterVolumeText.Text = $"{value:0}%";
        if (value > 0) _lastPositiveMasterVolume = value;
        MuteAllButton.IsChecked = value == 0;
        var muteAction = value == 0 ? $"Restore master volume ({_lastPositiveMasterVolume:0}%)" : "Mute master volume";
        ToolTip.SetTip(MuteAllButton, muteAction);
        AutomationProperties.SetName(MuteAllButton, muteAction);
        RunPlayerAction(player => player.SetMasterVolumeAsync(value));
    }

    private void AdjustMasterVolume(double delta, double step = 1)
    {
        var direction = Math.Sign(delta);
        if (direction == 0)
            return;

        MasterVolumeSlider.Value = Math.Clamp(
            Math.Round(MasterVolumeSlider.Value) + direction * step,
            MasterVolumeSlider.Minimum,
            MasterVolumeSlider.Maximum);
    }
}
