using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DinoPlayer.MP.Playback;

namespace DinoPlayer.MP;

// Opening videos: the picker, drag and drop, the import prompt and media preparation.
public sealed partial class MainWindow
{
    private async void OpenButtonOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await PickVideoAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a video", AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video and media")
                    {
                        Patterns = new[] { "*.mkv", "*.mp4", "*.webm", "*.mov", "*.avi", "*.m4v", "*.ts" },
                        MimeTypes = new[] { "video/*", "application/x-matroska", "application/octet-stream" }
                    },
                    FilePickerFileTypes.All
                }
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        });
    }

    // One picker at a time; keep the import prompt in place while browsing.
    internal async Task PickVideoAsync(Func<Task<string?>> pickFile)
    {
        if (_player is null || _pickingMedia || _closing) return;
        _pickingMedia = true;
        try
        {
            if (await pickFile() is { } path) LoadVideo(path);
        }
        catch (Exception exception) { ShowError($"Could not browse for a video: {exception.Message}"); }
        finally { _pickingMedia = false; UpdateImportPrompt(); }
    }

    private void UpdateImportPrompt()
    {
        // Visibility does not detach the native host or recreate its player.
        VideoHost.IsVisible = !string.IsNullOrWhiteSpace(_currentFilePath);
        var show = IsVisible && !_closing && !_closed && !_preferencesOpen &&
                   !_isFullscreen && string.IsNullOrWhiteSpace(_currentFilePath) && WorkspaceGrid.Bounds.Width > 0;
        var wasVisible = ImportPrompt.IsVisible;
        ImportPrompt.IsVisible = show;
        if (show && !wasVisible) FocusPlayer();
    }

    private void LoadVideo(string path) => LoadVideoCore(path, restorePlayback: false);

    private async void LoadVideoCore(string path, bool restorePlayback)
    {
        if (_player is null)
            return;

        _tabResumePlayback = null;
        await SelectWorkspaceTabAsync(false);
        var player = _player;
        long generation = player.Generation;
        try
        {
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("The selected media file does not exist.", path);
            FlushAudioPreset();
            _mixTimer.Stop();
            AudioTracksPanel.IsEnabled = false;
            AudioTracksPanel.Children.Clear();
            MuteAllButton.IsEnabled = false;
            var fullPath = System.IO.Path.GetFullPath(path);
            if (!restorePlayback) _resumeSnapshot = null;
            _currentFilePath = fullPath;
            MediaNameText.Text = System.IO.Path.GetFileName(_currentFilePath);
            ErrorBanner.IsVisible = false;
            HidePlayerQueueStatus();
            UpdateImportPrompt();
            SetMediaControlsEnabled(false);
            ResetTimeline();
            CropToggle.IsChecked = false;
            ExportPanel.SetContext(new());
            var snapshot = player.GetSnapshot();
            var paused = restorePlayback && _resumeSnapshot is { } resume ? resume.IsPaused :
                IsPreparingMedia(player) ? _preparingMedia!.Value.Paused : snapshot.IsLoaded && snapshot.IsPaused;
            var opening = player.OpenAsync(_currentFilePath, startPaused: true);
            generation = player.Generation;
            _preparingMedia = (player, generation, paused);
            await opening;
        }
        catch (Exception exception)
        {
            if (IsCurrentMedia(player, generation))
            {
                _preparingMedia = null;
                AppLog.Error($"Could not open media: {exception}");
                ShowError(exception.Message);
            }
        }
    }

    private void OnMediaLoaded(IPlaybackEngine player, long generation)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsCurrentMedia(player, generation)) return;
            try
            {
                var paused = IsPreparingMedia(player) ? _preparingMedia!.Value.Paused : player.GetSnapshot().IsPaused;
                RestoreAudioPreset();
                BuildAudioTrackControls();
                UpdatePlaybackUi();
                await player.ApplyAudioMixAsync();
                if (!IsCurrentMedia(player, generation)) return;
                var resume = _resumeSnapshot;
                _resumeSnapshot = null;
                // Audio reconfiguration may rebuild mpv's playback pipeline.
                // Rewind only after it is applied, while the import is paused.
                await player.SeekAndWaitAsync(resume?.PositionSeconds ?? 0);
                if (!IsCurrentMedia(player, generation)) return;
                paused = resume?.IsPaused ?? paused;
                if (_exportTabSelected)
                {
                    ExportPanel.SetContext(CaptureExportContext());
                    _tabResumePlayback = !paused ? (player, generation) : null;
                    paused = true;
                }
                await player.SetPausedAsync(paused);
                if (!IsCurrentMedia(player, generation)) return;
                _preparingMedia = null;
                AudioTracksPanel.IsEnabled = true;
                UpdatePlaybackUi();
                SetMediaControlsEnabled(_durationSeconds > 0);
                UpdateImportPrompt();
            }
            catch (Exception exception)
            {
                if (IsCurrentMedia(player, generation))
                {
                    _preparingMedia = null;
                    AppLog.Error($"Could not finish preparing media: {exception}");
                    ShowError(exception.Message);
                }
            }
        });
    }

    private void Window_DragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = eventArgs.DataTransfer.TryGetFiles()?.Any() == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void Window_Drop(object? sender, DragEventArgs eventArgs)
    {
        if (eventArgs.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            eventArgs.Handled = true;
            LoadVideo(path);
        }
    }
}
