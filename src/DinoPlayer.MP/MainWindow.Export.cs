using Avalonia.Interactivity;
using DinoPlayer.MP.Export;

namespace DinoPlayer.MP;

// Workspace tabs and handing the current clip to the Export tab or queue.
public sealed partial class MainWindow
{
    private async void ExportButtonOnClick(object? sender, RoutedEventArgs eventArgs) =>
        await SelectWorkspaceTabAsync(true);

    private void PlayerAddToQueueOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!PlayerAddToQueueButton.IsEnabled || _closing || _closed) return;
        if (ExportPanel.TryAddToQueue(CaptureExportContext(), out var message))
        {
            ShowPlayerQueueStatus(message);
            ErrorBanner.IsVisible = false;
        }
        else
        {
            HidePlayerQueueStatus();
            ShowError(message);
        }
    }

    private void ShowPlayerQueueStatus(string message)
    {
        _playerQueueStatusTimer.Stop();
        _isPlayerQueueStatusFading = false;
        PlayerQueueStatusText.Text = message;
        PlayerQueueStatusText.IsVisible = true;
        PlayerQueueStatusText.Opacity = 1;
        _playerQueueStatusTimer.Interval = PlayerQueueStatusHoldDuration;
        _playerQueueStatusTimer.Start();
    }

    private void PlayerQueueStatusTimerOnTick(object? sender, EventArgs eventArgs)
    {
        _playerQueueStatusTimer.Stop();
        if (_isPlayerQueueStatusFading)
        {
            HidePlayerQueueStatus();
            return;
        }

        _isPlayerQueueStatusFading = true;
        PlayerQueueStatusText.Opacity = 0;
        _playerQueueStatusTimer.Interval = PlayerQueueStatusFadeDuration;
        _playerQueueStatusTimer.Start();
    }

    private void HidePlayerQueueStatus()
    {
        _playerQueueStatusTimer.Stop();
        _isPlayerQueueStatusFading = false;
        PlayerQueueStatusText.IsVisible = false;
        PlayerQueueStatusText.Opacity = 0;
    }

    private ExportContext CaptureExportContext()
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath) || _durationSeconds <= 0)
            return new();
        return new ExportContext
        {
            SourcePath = _currentFilePath,
            CropEnabled = CropToggle.IsChecked == true,
            Start = CropToggle.IsChecked == true ? _cropStart : 0,
            End = CropToggle.IsChecked == true ? _cropEnd : 0,
            Duration = _durationSeconds,
            PlaybackSpeed = _player.GetSnapshot().PlaybackSpeed,
            Tracks = _player.AudioTracks.ToArray()
        };
    }

    private async Task SelectWorkspaceTabAsync(bool export)
    {
        if (_closing || _closed) return;
        if (export && _isFullscreen) SetFullscreen(false);
        var changed = _exportTabSelected != export;
        _exportTabSelected = export;
        _updatingTabs = true;
        try { WorkspaceTabs.SelectedIndex = export ? 1 : 0; }
        finally { _updatingTabs = false; }
        VideoBorder.IsVisible = !export;
        PlayerSidebar.IsVisible = !export && !_isFullscreen;
        NormalPlaybackArea.IsVisible = !export && !_isFullscreen;
        ExportPanel.IsVisible = export;
        if (export) ExportPanel.SetContext(CaptureExportContext());
        else FocusPlayer();
        if (!changed) return;
        try
        {
            if (export && _player is { } player)
            {
                var snapshot = player.GetSnapshot();
                _tabResumePlayback = !snapshot.IsPaused && snapshot.IsLoaded ? (player, player.Generation) : null;
                await player.SetPausedAsync(true);
            }
            else if (!export && _tabResumePlayback is { } resume)
            {
                _tabResumePlayback = null;
                if (IsCurrentMedia(resume.Player, resume.Generation)) await resume.Player.SetPausedAsync(false);
            }
        }
        catch (Exception exception)
        {
            if (!_closing) AppLog.Warn($"Could not change playback state when switching tabs: {exception.Message}");
        }
    }

    private void UpdateExportTabHeader(ExportManagerSnapshot snapshot)
    {
        PlayerAddToQueueButton.IsEnabled = !_closing && !_closed && !snapshot.IsStopping &&
            _player is not null && PlayPauseButton.IsEnabled;
        var details = new List<string>();
        if (snapshot.Active is { } active) details.Add($"{active.Progress:0}%");
        if (snapshot.WaitingCount > 0) details.Add($"{snapshot.WaitingCount} waiting");
        if (snapshot.QueuePaused) details.Add("paused");
        if (snapshot.FailedCount > 0) details.Add($"{snapshot.FailedCount} failed");
        var text = details.Count == 0 ? "Export" : $"Export · {string.Join(" · ", details)}";
        if (!Equals(ExportTabHeader.Content, text)) ExportTabHeader.Content = text;
    }
}
