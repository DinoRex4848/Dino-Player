using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DinoPlayer.MP;
using DinoPlayer.MP.Controls;
using DinoPlayer.MP.Export;
using DinoPlayer.MP.Playback;
using DinoPlayer.MP.Settings;

internal static class UiTests
{
    public static int Run(string source)
    {
        var result = 1;
        var thread = new Thread(() => result = RunOnUiThread(source));
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return result;
    }

    private static int RunOnUiThread(string source)
    {
        Directory.CreateDirectory(Program.Temp);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Program.Temp);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Program.Temp);
        SettingsStore.Initialize();
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        using var done = new CancellationTokenSource();
        var exitCode = 1;
        MainWindow? main = null;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var runner = new ExportManagerTests.ControlledRunner();
                main = new MainWindow(runner.Run);
                main.Show();
                main.Activate();
                await Until(() => Control<MenuItem>(main, "OpenMenuItem").IsEnabled);
                await CheckImport(main, source);
                await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled);
                var player = Field<IPlaybackEngine>(main, "_player");
                await player.SetMasterVolumeAsync(0); // Keep automated desktop tests silent.
                await player.SetPausedAsync(true);
                await Until(() => player.GetSnapshot().IsPaused);
                await CheckPreparedImports(main, player, source);
                var masterVolume = Control<Slider>(main, "MasterVolumeSlider");
                var fullscreenVolume = Control<Slider>(main, "FullscreenMasterVolumeSlider");
                CheckMasterMute(main, player);
                CheckVolumeDragging(masterVolume);
                Program.Check(fullscreenVolume.Value == masterVolume.Value, "Master volume sliders lost synchronization while dragging.");
                var trackVolumes = Control<StackPanel>(main, "AudioTracksPanel").GetVisualDescendants().OfType<Slider>().ToArray();
                Program.Check(trackVolumes.Length == player.AudioTracks.Count && trackVolumes.Length > 0, "Track sliders were not discovered.");
                for (var i = 0; i < trackVolumes.Length; i++)
                {
                    CheckVolumeDragging(trackVolumes[i]);
                    Program.Check(player.AudioTracks[i].VolumePercent == trackVolumes[i].Value, "Dragging did not update track volume.");
                }
                masterVolume.Value = 0;
                Console.WriteLine("PASS UI master and per-track click-and-drag volume, bounds, release and capture loss");
                await CheckPlayerLayout(main);
                await CheckTrackScrolling(main);
                await CheckPreferences(main, player);
                await CheckCropTimeEditing(main, player);
                await CheckCropToggleMemory(main, player);
                await CheckPlaybackFocus(main, player);
                await CheckRapidSeeking(main, player, source);
                var manager = Field<ExportManager>(main, "_exportManager");
                var export = Control<ExportView>(main, "ExportPanel");
                SettingsStore.AddOrUpdateExportPreset(new ExportPreset { Name = "First visit", VideoBitrate = "7" });
                SettingsStore.SavePreferences("First visit", Program.Temp, new());
                SettingsStore.SavePreferences("First visit", Program.Temp, new(), true, false, true);
                main.ApplyPreferences();
                await CheckPlayerQueueShortcut(main, player, manager, export);
                Control<ToggleButton>(main, "CropToggle").IsChecked = true;
                Call(main, "SetCropValues", 1d, 1d);
                await SwitchTab(main, true);
                await Until(() => export.IsVisible);
                CheckExportFocus(main, export);
                Program.Check(Control<TextBox>(export, "VideoBitrateBox").Text == "7", "First Export visit did not apply the latest configured default preset.");
                Click(export, "StartExportButton");
                Program.Check(Control<TextBlock>(export, "StatusText").Text!.Contains("crop end"), "An empty crop was accepted by the UI.");
                await SwitchTab(main, false);
                Program.Check(player.GetSnapshot().IsPaused, "Returning from Export resumed an already paused video.");
                Control<ToggleButton>(main, "CropToggle").IsChecked = false;
                Console.WriteLine("PASS UI empty crop rejected and paused playback preserved");

                await SwitchTab(main, true);
                SettingsStore.AddOrUpdateExportPreset(new ExportPreset { Name = "UI test" });
                Call(export, "RefreshPresetNames", "UI test");
                var presets = Control<ComboBox>(export, "PresetCombo");
                presets.SelectedItem = "UI test";
                Control<ComboBox>(export, "ResolutionCombo").SelectedItem = "720p";
                Program.Check(presets.SelectedIndex == 0, "Resolution did not clear the active preset.");
                presets.SelectedItem = "UI test";
                Control<TextBox>(export, "VideoBitrateBox").Text = "6";
                Program.Check(presets.SelectedIndex == 0, "Bitrate did not clear the active preset.");
                Control<TextBox>(export, "VideoAdvancedBox").Text = "-c:v copy";
                Program.Check(Control<CheckBox>(export, "CopyPlaybackSpeedCheck").IsEnabled, "Inactive advanced flags disabled speed export.");
                Control<ToggleButton>(export, "VideoAdvancedToggle").IsChecked = true;
                Program.Check(!Control<CheckBox>(export, "CopyPlaybackSpeedCheck").IsEnabled, "Active copy flags allowed speed export.");
                Control<ToggleButton>(export, "VideoAdvancedToggle").IsChecked = false;
                await CheckQualityControls(export);
                Console.WriteLine("PASS UI preset dirty state, quality controls and active export settings");
                CheckAutomaticExportFolder(export);

                await SwitchTab(main, false);
                await player.SeekAsync(.25);
                await player.SetPausedAsync(false);
                await Until(() => !player.GetSnapshot().IsPaused);
                var hostGeneration = Field<long>(main, "_hostGeneration");
                await SwitchTab(main, true);
                await Until(() => player.GetSnapshot().IsPaused);
                Program.Check(Control<TextBox>(export, "VideoBitrateBox").Text == "6", "Encoding settings did not survive a tab switch.");
                Control<TextBox>(export, "OutputFolderBox").Text = Program.Temp;
                Control<TextBox>(export, "OutputNameBox").Text = "background-test";
                var folderBeforeCancel = Control<TextBox>(export, "OutputFolderBox").Text;
                export.ApplyOutputFolderSelection(null);
                Program.Check(Control<TextBox>(export, "OutputFolderBox").Text == folderBeforeCancel, "Cancelling folder selection changed the export folder.");
                Control<TextBox>(export, "OutputNameBox").Text = "background-test.mp4";
                Control<ComboBox>(export, "ContainerCombo").SelectedItem = "MKV";
                Click(export, "StartExportButton");
                var execution = await runner.Next();
                await CheckExportLog(export, execution);
                Program.Check(manager.GetSnapshot().Active!.Definition.Options.OutputPath == Path.Combine(Program.Temp, "background-test.mkv"), "Export did not assemble the filename, folder and container.");
                Control<ComboBox>(export, "ContainerCombo").SelectedItem = "MP4";
                var immediate = manager.GetSnapshot().Active!.Id;
                await SwitchTab(main, false);
                await Until(() => !export.IsVisible && !player.GetSnapshot().IsPaused);
                await player.SetPausedAsync(true);
                await Until(() => player.GetSnapshot().IsPaused);
                await player.SeekAsync(1.2);
                await Until(() => Math.Abs(player.GetSnapshot().PositionSeconds - 1.2) < .3);
                await player.SetPausedAsync(false);
                await Until(() => !player.GetSnapshot().IsPaused);
                await player.AdjustPlaybackSpeedAsync(1);
                Program.Check(manager.GetSnapshot().Active?.Id == immediate && ReferenceEquals(player, Field<IPlaybackEngine>(main, "_player")), "Background mode cancelled the export or replaced the playback engine.");
                for (var i = 0; i < 3; i++) { await SwitchTab(main, true); await SwitchTab(main, false); }
                Program.Check(Field<long>(main, "_hostGeneration") == hostGeneration, "Tab switching recreated the native host.");
                Console.WriteLine("PASS UI background playback, seeking, state restoration and persistent native host");

                Control<ToggleButton>(main, "CropToggle").IsChecked = true;
                Call(main, "SetCropValues", .1d, .7d);
                await SwitchTab(main, true);
                Click(export, "AddToQueueButton");
                var firstQueued = manager.GetSnapshot().Jobs[^1].Id;
                await SwitchTab(main, false);
                Call(main, "SetCropValues", .9d, 1.6d);
                await SwitchTab(main, true);
                Click(export, "AddToQueueButton");
                var secondQueued = manager.GetSnapshot().Jobs[^1].Id;
                Program.Check(runner.StartedCount == 1 && manager.GetSnapshot().WaitingCount == 2, "Adding during a standalone export started saved clips.");
                var list = Control<ListBox>(export, "QueueList");
                list.SelectedIndex = 0;
                Program.Check(Control<TextBox>(export, "VideoBitrateBox").Text == "6", "Selecting a queued job replaced form settings.");
                Program.Check(!Control<Button>(export, "RemoveJobButton").IsEnabled, "Running clip can be removed.");
                list.SelectedIndex = 2;
                Click(export, "MoveUpButton");
                await Task.Delay(250); // Let layout and the bounded progress/log pump render.
                await CheckExportLayout(main, export);
                await CheckCompactExportLayout(main, export);
                await CheckLongQueue(main, export, manager);
                Capture(main, "export-tab.png");
                main.Width = 900;
                main.Height = 650;
                await Task.Delay(250);
                Program.Check(export.Bounds.Width <= main.ClientSize.Width && Control<Button>(export, "StartExportButton").Bounds.Width > 0,
                    "Export layout does not fit the minimum window size.");
                Capture(main, "export-tab-minimum.png");
                main.Width = 1280;
                main.Height = 860;
                Click(export, "StartQueueButton");
                await Until(() => Control<Button>(export, "PauseQueueButton").IsEnabled);
                Click(export, "PauseQueueButton");
                execution.Succeed();
                await manager.WhenIdleAsync();
                await Until(() => Control<Button>(export, "ResumeQueueButton").IsVisible);
                Program.Check(manager.GetSnapshot().QueuePaused && runner.StartedCount == 1, "Pause started another clip.");
                Click(export, "ResumeQueueButton");
                execution = await runner.Next();
                Program.Check(manager.GetSnapshot().Active?.Id == secondQueued, "Reordered clip did not start next.");
                await Until(() => Control<Button>(export, "CancelButton").IsEnabled);
                Click(export, "CancelButton");
                execution = await runner.Next();
                Program.Check(manager.GetSnapshot().Active?.Id == firstQueued, "Cancel current did not continue the queue.");
                execution.Succeed();
                await manager.WhenIdleAsync();
                Program.Check(ExportManagerTests.Job(manager, firstQueued).Definition.Options.Start == .1 && ExportManagerTests.Job(manager, secondQueued).Definition.Options.Start == .9, "Queued crop snapshots changed.");
                Console.WriteLine("PASS UI queue add, captured crops, selection, reorder, pause, resume and cancellation");

                Click(export, "StartExportButton");
                execution = await runner.Next();
                var failed = manager.GetSnapshot().Active!.Id;
                execution.Fail("test encoder failure");
                await manager.WhenIdleAsync();
                await Until(() => Control<Button>(export, "RetryJobButton").IsEnabled);
                Program.Check(Control<TextBox>(export, "LogBox").Text!.Contains("test encoder failure"), "Selected failure log is missing.");
                Click(export, "RetryJobButton");
                Click(export, "StartQueueButton");
                execution = await runner.Next();
                Program.Check(manager.GetSnapshot().Active?.Id == failed, "Retry changed the job identity.");
                await manager.CancelAllAsync();
                Console.WriteLine("PASS UI failure log, retry and cancel all");

                await SwitchTab(main, false);
                Control<ToggleButton>(main, "CropToggle").IsChecked = false;
                await player.SetPausedAsync(false);
                await Until(() => !player.GetSnapshot().IsPaused);
                await SwitchTab(main, true);
                Click(export, "StartExportButton");
                execution = await runner.Next();
                var frozenSource = manager.GetSnapshot().Active!.Definition.Options.SourcePath;
                var volumeBeforeSourceChange = Field<double>(main, "_lastPositiveMasterVolume");
                var otherSource = Path.Combine(Program.Temp, "another-source.mkv");
                File.Copy(source, otherSource);
                Call(main, "LoadVideo", otherSource);
                await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled && !export.IsVisible && player.GetSnapshot().Generation == player.Generation);
                await player.SetPausedAsync(true);
                await Until(() => player.GetSnapshot().IsPaused);
                Program.Check(masterVolume.Value == 0 && Control<ToggleButton>(main, "MuteAllButton").IsChecked == true &&
                              Field<double>(main, "_lastPositiveMasterVolume") == volumeBeforeSourceChange,
                    "Changing media lost master mute or its restoration volume.");
                await SwitchTab(main, true);
                Program.Check(Control<TextBox>(export, "OutputNameBox").Text == "another-source_export" && Control<TextBox>(export, "OutputFolderBox").Text == Program.Temp, "Source change lost the chosen output folder or failed to refresh the filename.");
                await SwitchTab(main, false);
                Program.Check(player.GetSnapshot().IsPaused && manager.GetSnapshot().Active!.Definition.Options.SourcePath == frozenSource, "Opening a file restored an old resume intent or changed an export source.");
                await manager.CancelAllAsync();
                Call(main, "LoadVideo", source);
                await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled && player.GetSnapshot().Generation == player.Generation);
                await player.SetPausedAsync(true);
                Console.WriteLine("PASS UI opening another file during export preserves job snapshots and clears resume intent");

                await Until(() => Control<Grid>(main, "RootGrid").IsFocused);
                if (OperatingSystem.IsLinux())
                {
                    main.Activate();
                    await Until(() => main.IsActive && NativeKeyboard.HasFocus(main));
                    NativeKeyboard.Press(main, "F11");
                }
                else Program.Check(PressKey(Control<Grid>(main, "RootGrid"), Key.F11).Handled, "F11 did not reach the fullscreen shortcut handler.");
                await Task.Delay(300);
                Program.Check(main.WindowState == WindowState.FullScreen, "Fullscreen was not entered.");
                Call(main, "HandlePlaybackPointerInput", new PlaybackPointerEvent(PlaybackPointerEventKind.Move, 20, 1000, 1080, 0));
                Program.Check(Control<Popup>(main, "FullscreenControlsPopup").IsOpen, "Pointer movement did not reveal controls.");
                CheckVolumeDragging(fullscreenVolume, () =>
                {
                    Call(main, "HideFullscreenControls");
                    Call(main, "FullscreenControlsTimerOnTick", null, EventArgs.Empty);
                    Program.Check(Control<Popup>(main, "FullscreenControlsPopup").IsOpen, "Fullscreen controls hid during a volume drag.");
                });
                Program.Check(masterVolume.Value == fullscreenVolume.Value, "Fullscreen volume drag did not update master volume.");
                masterVolume.Value = 0;
                await CheckPlaybackFocus(main, player, fullscreen: true);
                Console.WriteLine("PASS UI fullscreen volume dragging keeps controls visible");
                Call(main, "SetFullscreen", false);
                main.Width = 1000;
                await Task.Delay(200);
                Call(main, "SetTimelineDuration", 86400d);
                var canvas = Control<Canvas>(main, "TimelineCanvas");
                Program.Check(canvas.Children.Count < 100, "A long timeline created all minute markers.");
                Control<ScrollViewer>(main, "TimelineScroller").Offset = new Vector(80000, 0);
                await Task.Delay(200);
                Program.Check(canvas.Children.Count < 100, "Scrolling grew the marker count.");
                Console.WriteLine("PASS UI fullscreen, pointer reveal, resizing and long timeline");

                var border = Control<Border>(main, "VideoBorder");
                var host = (MpvVideoHost)border.Child!;
                await player.SetPausedAsync(true);
                await player.SeekAsync(1.2);
                await Until(() => player.GetSnapshot().IsPaused && Math.Abs(player.GetSnapshot().PositionSeconds - 1.2) < .1);
                masterVolume.Value = 36.5;
                Click(main, "MuteAllButton");
                border.Child = null;
                await Until(() => typeof(MainWindow).GetField("_player", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main) is null);
                border.Child = host;
                await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled);
                var recreated = Field<IPlaybackEngine>(main, "_player");
                Program.Check(!ReferenceEquals(player, recreated), "Native host recreation did not replace the engine.");
                await Until(() => recreated.GetSnapshot().IsPaused && Math.Abs(recreated.GetSnapshot().PositionSeconds - 1.2) < .1);
                var rememberedVolume = Field<double>(main, "_lastPositiveMasterVolume");
                Click(main, "MuteAllButton");
                Program.Check(masterVolume.Value == rememberedVolume && fullscreenVolume.Value == rememberedVolume,
                    "Recreating the media player lost the volume saved before mute.");
                masterVolume.Value = 0;
                Program.Check(Field<DispatcherTimer>(main, "_uiTimer").IsEnabled, "UI timer was not restored.");
                AppLog.Info("UI log after native host recreation");
                await Task.Delay(300);
                Program.Check(Control<TextBox>(main, "LogTextBox").Text!.Contains("UI log after native host recreation"), "Log subscription was not restored.");
                Console.WriteLine("PASS UI native host recreation restores playback, timer and logs");
                await SwitchTab(main, true);
                Control<TextBox>(export, "OutputNameBox").Text = "owner-close";
                Control<TextBox>(export, "OutputFolderBox").Text = Program.Temp;
                Click(export, "StartExportButton");
                await runner.Next();
                Click(export, "AddToQueueButton");
                Control<MenuItem>(main, "ExitMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => main.OwnedWindows.OfType<ConfirmDialog>().Any());
                var warning = main.OwnedWindows.OfType<ConfirmDialog>().Single();
                Program.Check(Control<Button>(warning, "NoButton").IsDefault && Control<Button>(warning, "NoButton").Content?.ToString() == "Keep open", "Close warning does not default to Keep open.");
                Click(warning, "NoButton");
                await Until(() => !Field<bool>(main, "_confirmingClose"));
                Program.Check(main.IsVisible && manager.GetSnapshot().Active is not null && manager.GetSnapshot().WaitingCount == 1, "Keep open changed the exports.");
                Control<MenuItem>(main, "ExitMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => main.OwnedWindows.OfType<ConfirmDialog>().Any());
                warning = main.OwnedWindows.OfType<ConfirmDialog>().Single();
                Click(warning, "YesButton");
                await Until(() => Field<bool>(main, "_closed"));
                Program.Check(!manager.GetSnapshot().HasUnfinished && manager.GetSnapshot().Jobs.All(job => !File.Exists(job.Definition.TemporaryPath)), "Closing left a running export or a partial file.");
                Program.Check(Field<Task>(main, "_playerShutdown").IsCompleted, "Closing did not finish playback shutdown.");
                exitCode = 0;
                Console.WriteLine("PASS UI close warning Keep open and confirmed shutdown stop active and waiting exports");
                Console.WriteLine($"RESULT: UI smoke tests passed. Artifacts: {Program.Temp}");
            }
            catch (Exception exception) { Console.Error.WriteLine($"FAIL UI smoke: {exception}"); }
            finally
            {
                if (main is not null)
                {
                    foreach (var dialog in main.OwnedWindows.OfType<ConfirmDialog>().ToArray()) dialog.Close(false);
                    try { await Field<ExportManager>(main, "_exportManager").StopAsync(); } catch { }
                    main.Close();
                    try { await Field<Task>(main, "_playerShutdown").WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                }
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return exitCode;
    }

    private static async Task CheckImport(MainWindow main, string source)
    {
        await Until(() => Control<Button>(main, "ImportPrompt").IsVisible);
        await Task.Delay(150);
        var prompt = Control<Button>(main, "ImportPrompt");
        Program.Check(TopLevel.GetTopLevel(prompt) == main && !Control<MpvVideoHost>(main, "VideoHost").IsVisible,
            "Import prompt is a separate native window or the empty video surface covers it.");
        Program.Check(!Control<Button>(main, "PlayerAddToQueueButton").IsVisible && !Control<Button>(main, "PlayerAddToQueueButton").IsEnabled,
            "The player queue shortcut is enabled by default or without media.");
        var engine = Field<IPlaybackEngine>(main, "_player");
        var other = new Window { Title = "Import stacking check", Width = 500, Height = 350, Content = new TextBox { Text = "Another window can receive input" } };
        other.Show();
        other.Activate();
        await Until(() => other.IsActive);
        Program.Check(!main.IsActive && TopLevel.GetTopLevel(prompt) == main, "Import prompt stole focus from another window.");
        other.Close();
        main.Activate();
        await Until(() => main.IsActive);
        main.WindowState = WindowState.Minimized;
        await Task.Delay(150);
        main.WindowState = WindowState.Normal;
        main.Activate();
        await Until(() => main.IsActive && prompt.IsVisible);
        Program.Check(ReferenceEquals(engine, Field<IPlaybackEngine>(main, "_player")), "Minimize/restore recreated the empty native player.");
        var workspace = Control<Grid>(main, "WorkspaceGrid");
        var overlayOrigin = prompt.PointToScreen(default);
        var workspaceOrigin = workspace.PointToScreen(default);
        Program.Check(Math.Abs(overlayOrigin.X - workspaceOrigin.X) <= 2 && Math.Abs(overlayOrigin.Y - workspaceOrigin.Y) <= 2 &&
                      !prompt.Focusable && Control<Grid>(main, "RootGrid").IsFocused,
            $"Import overlay origin={overlayOrigin}, workspace={workspaceOrigin}, focused={prompt.IsFocused}, focusable={prompt.Focusable}.");
        Program.Check(main.FindControl<Button>("OpenButton") is null && main.FindControl<Button>("ExportButton") is null &&
                      main.FindControl<ToggleButton>("LoopToggle") is null && main.FindControl<TextBlock>("BackendText") is null,
            "Removed toolbar controls remain in the player.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Control<Button>(main, "ImportPrompt").Bounds.Width, (int)Control<Button>(main, "ImportPrompt").Bounds.Height)))
        {
            bitmap.Render(Control<Button>(main, "ImportPrompt"));
            bitmap.Save(Path.Combine(Program.Temp, "import-prompt.png"), PngBitmapEncoderOptions.Default);
        }
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picking = main.PickVideoAsync(() => picker.Task);
        try
        {
            await Task.Delay(150);
            Program.Check(!picking.IsCompleted && prompt.IsVisible && !Control<MpvVideoHost>(main, "VideoHost").IsVisible,
                "Browsing for a video hid the import prompt or exposed an empty native surface.");
            var duplicatePickerOpened = false;
            await main.PickVideoAsync(() => { duplicatePickerOpened = true; return Task.FromResult<string?>(null); });
            Program.Check(!duplicatePickerOpened, "A second picker opened while browsing for a video.");
        }
        finally { picker.TrySetResult(null); await picking; }
        Program.Check(Control<Button>(main, "ImportPrompt").IsVisible, "Picker cancellation did not restore the import prompt.");
        Call(main, "LoadVideo", Path.Combine(Program.Temp, "missing-video.mkv"));
        await Until(() => Control<Border>(main, "ErrorBanner").IsVisible);
        Program.Check(!Control<Expander>(main, "DiagnosticLogExpander").IsVisible && Control<Button>(main, "ImportPrompt").IsVisible,
            "Import failure hid its error or prevented retry with diagnostics disabled.");
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile((await main.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(source))))!));
        var drop = new DragEventArgs(DragDrop.DropEvent, transfer, prompt, default, KeyModifiers.None);
        prompt.RaiseEvent(drop);
        await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled);
        Program.Check(drop.Handled, "Dropping a video did not reach the import handler.");
        var generation = Field<IPlaybackEngine>(main, "_player").Generation;
        await main.PickVideoAsync(() => Task.FromResult<string?>(source));
        await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled && Field<IPlaybackEngine>(main, "_player").GetSnapshot().Generation > generation);
        Program.Check(!Control<Button>(main, "ImportPrompt").IsVisible && !Control<Border>(main, "ErrorBanner").IsVisible,
            "Successful import retained its overlay or error.");
        Program.Check(Control<MpvVideoHost>(main, "VideoHost").IsVisible && ReferenceEquals(engine, Field<IPlaybackEngine>(main, "_player")),
            "Import did not restore the same native playback surface.");
        Program.Check(Control<TextBlock>(main, "MediaNameText").Text == Path.GetFileName(source), "The media title contains a path.");
        Console.WriteLine("PASS UI import startup, cancellation, errors, drag-and-drop, successful import and filename title");
    }

    private static async Task CheckPlayerQueueShortcut(MainWindow main, IPlaybackEngine player, ExportManager manager, ExportView export)
    {
        var shortcut = Control<Button>(main, "PlayerAddToQueueButton");
        var crop = Control<ToggleButton>(main, "CropToggle");
        var paused = player.GetSnapshot().IsPaused;
        var tracks = player.AudioTracks.Select(track => (track.Enabled, track.VolumePercent)).ToArray();
        Program.Check(shortcut.IsVisible && shortcut.IsEnabled && !export.IsVisible && !Field<bool>(export, "_defaultsInitialized"),
            "The shortcut cannot be used before the first Export visit.");
        crop.IsChecked = true;
        Call(main, "SetCropValues", 1d, 1d);
        Click(main, "PlayerAddToQueueButton");
        Program.Check(manager.GetSnapshot().Jobs.Length == 0 && Control<TextBlock>(main, "ErrorText").Text!.Contains("crop end"),
            "Shortcut accepted an empty crop or hid its validation feedback.");
        Call(main, "SetCropValues", .1d, .7d);
        Click(main, "PlayerAddToQueueButton");
        var first = manager.GetSnapshot().Jobs.Single();
        Program.Check(first.Definition.Options.VideoBitrate == "7" && first.Definition.Options.Start == .1 && first.Definition.Options.End == .7,
            "First shortcut submission did not apply the configured preset or current crop.");
        Program.Check(Control<TextBlock>(main, "PlayerQueueStatusText").Text!.StartsWith("Added to queue:") && !Control<Border>(main, "ErrorBanner").IsVisible,
            "Shortcut success feedback is missing.");
        Control<ComboBox>(export, "VideoRateControlCombo").SelectedIndex = 1;
        Control<TextBox>(export, "VideoBitrateBox").Text = "19";
        await player.AdjustPlaybackSpeedAsync(1);
        var speed = player.GetSnapshot().PlaybackSpeed;
        Control<CheckBox>(export, "CopyPlaybackSpeedCheck").IsChecked = true;
        Control<TextBox>(export, "OutputNameBox").Text = "shortcut-custom";
        Control<ComboBox>(export, "ContainerCombo").SelectedItem = "MKV";
        player.AudioTracks[0].VolumePercent = 71;
        player.AudioTracks[0].Enabled = false;
        Call(main, "SetCropValues", .9d, 1.6d);
        Click(main, "PlayerAddToQueueButton");
        var second = manager.GetSnapshot().Jobs[^1];
        Program.Check(second.Definition.Options.VideoRateControl == "Quality" && second.Definition.Options.VideoQuality == "19" &&
                      second.Definition.Options.Start == .9 && !second.Definition.Options.Tracks[0].Enabled && second.Definition.Options.Tracks[0].VolumePercent == 71,
            "Shortcut reused a stale crop, audio snapshot or quality setting.");
        Program.Check(second.Definition.Options.CopyPlaybackSpeed && Math.Abs(second.Definition.Options.PlaybackSpeed - speed) < .001 &&
                      second.Definition.Options.OutputPath == Path.Combine(Program.Temp, "shortcut-custom.mkv") &&
                      Math.Abs(second.Definition.Command.DurationSeconds - .7 / speed) < .001,
            "Shortcut did not capture playback speed, destination or output format.");
        Program.Check(first.Definition.Options.VideoRateControl == "Bitrate" && first.Definition.Options.Tracks[0].Enabled == tracks[0].Enabled &&
                      first.Definition.Options.Tracks[0].VolumePercent == tracks[0].VolumePercent,
            "Editing player/export settings changed an already queued snapshot.");
        Control<TextBox>(export, "VideoBitrateBox").Text = "999";
        Click(main, "PlayerAddToQueueButton");
        Program.Check(manager.GetSnapshot().Jobs.Length == 2 && Control<TextBlock>(main, "ErrorText").Text!.Contains("CRF quality"),
            "Invalid shortcut quality was submitted or its error was hidden.");
        Control<TextBox>(export, "VideoBitrateBox").Text = "23";
        crop.IsChecked = false;
        Click(main, "PlayerAddToQueueButton");
        Program.Check(!manager.GetSnapshot().Jobs[^1].Definition.Options.CropEnabled && !export.IsVisible &&
                      player.GetSnapshot().IsPaused == paused && manager.GetSnapshot().Jobs.All(job => job.State == ExportJobState.Waiting),
            "Full-video shortcut changed tabs/playback or started a waiting queue.");
        foreach (var job in manager.GetSnapshot().Jobs) manager.Remove(job.Id);
        for (var index = 0; index < tracks.Length; index++)
        {
            player.AudioTracks[index].Enabled = tracks[index].Enabled;
            player.AudioTracks[index].VolumePercent = tracks[index].VolumePercent;
        }
        Control<ComboBox>(export, "VideoRateControlCombo").SelectedIndex = 0;
        Control<CheckBox>(export, "CopyPlaybackSpeedCheck").IsChecked = false;
        Control<ComboBox>(export, "ContainerCombo").SelectedItem = "MP4";
        await player.AdjustPlaybackSpeedAsync(-1);
        SettingsStore.SavePreferences("First visit", Program.Temp, new(), showPlayerQueueButton: false);
        main.ApplyPreferences();
        Program.Check(!shortcut.IsVisible && !Control<TextBlock>(main, "PlayerQueueStatusText").IsVisible, "Disabling the shortcut retained its controls.");
        SettingsStore.SavePreferences("First visit", Program.Temp, new(), showPlayerQueueButton: true);
        main.ApplyPreferences();
        await Task.Delay(100);
        foreach (var size in new[] { (1280d, 860d), (900d, 650d) })
        {
            main.Width = size.Item1; main.Height = size.Item2;
            await Task.Delay(150);
            var controls = Control<Grid>(main, "PlaybackControlsRow");
            var transport = controls.Children.OfType<StackPanel>().Single();
            var point = shortcut.TranslatePoint(default, controls)!.Value;
            Program.Check(point.X + shortcut.Bounds.Width <= transport.Bounds.Left && point.Y + shortcut.Bounds.Height <= controls.Bounds.Height,
                "The queue shortcut overlaps playback controls at minimum size.");
            Capture(main, size.Item1 == 900 ? "player-queue-minimum.png" : "player-queue.png");
        }
        main.Width = 1280; main.Height = 860;
        await CheckPlayerQueueFeedback(main, manager);
        Console.WriteLine("PASS UI player queue shortcut first-use defaults, snapshots, validation, feedback, full video and preferences");
    }

    private static async Task CheckPlayerQueueFeedback(MainWindow main, ExportManager manager)
    {
        var shortcut = Control<Button>(main, "PlayerAddToQueueButton");
        var status = Control<TextBlock>(main, "PlayerQueueStatusText");
        var row = Control<Grid>(main, "PlaybackControlsRow");
        var names = new[] { "VideoBorder", "AudioPanel", "TimelineScroller", "NormalPlaybackArea" };
        var added = new List<Guid>();
        void Add()
        {
            Click(main, "PlayerAddToQueueButton");
            added.Add(manager.GetSnapshot().Jobs[^1].Id);
        }
        Point[] Positions() => names.Select(name => Control<Control>(main, name).TranslatePoint(default, main)!.Value).ToArray();
        void CheckPositions(Point[] expected)
        {
            var current = Positions();
            Program.Check(current.Select((point, index) => Math.Abs(point.X - expected[index].X) < 1 && Math.Abs(point.Y - expected[index].Y) < 1).All(equal => equal),
                "Showing or hiding player queue feedback shifted the UI.");
        }

        try
        {
            foreach (var size in new[] { (1280d, 860d), (900d, 650d) })
            {
                Call(main, "HidePlayerQueueStatus");
                main.Width = size.Item1; main.Height = size.Item2;
                await Task.Delay(150);
                var before = Positions();
                var rowHeight = row.Bounds.Height;
                Add();
                await Task.Delay(450);
                Program.Check(status.IsVisible && status.Opacity > .99 && Math.Abs(row.Bounds.Height - rowHeight) < 1,
                    "Player queue feedback did not appear or changed the control row's height.");
                var buttonPoint = shortcut.TranslatePoint(default, row)!.Value;
                var statusPoint = status.TranslatePoint(default, row)!.Value;
                var transport = row.Children.OfType<StackPanel>().Single();
                Program.Check(status.Bounds.Width > 0 && statusPoint.X >= buttonPoint.X + shortcut.Bounds.Width &&
                              statusPoint.X + status.Bounds.Width <= transport.Bounds.Left &&
                              Math.Abs(statusPoint.Y + status.Bounds.Height / 2 - buttonPoint.Y - shortcut.Bounds.Height / 2) < 1,
                    "Queue feedback is not beside its button or overlaps playback controls.");
                CheckPositions(before);
                Capture(main, size.Item1 == 900 ? "player-queue-feedback-minimum.png" : "player-queue-feedback.png");
                Call(main, "HidePlayerQueueStatus");
                await Task.Delay(50);
                CheckPositions(before);
            }

            main.Width = 1280; main.Height = 860;
            await Task.Delay(150);
            var baseline = Positions();
            Add();
            await Task.Delay(5000);
            Add();
            var latest = status.Text;
            await Task.Delay(3500);
            Program.Check(status.IsVisible && status.Opacity > .99 && status.Text == latest,
                "An older notification timer hid the latest queue confirmation before eight seconds.");
            await Until(() => status.IsVisible && status.Opacity > 0 && status.Opacity < .99);
            Program.Check(Field<bool>(main, "_isPlayerQueueStatusFading"), "Queue confirmation did not fade after its display period.");
            await Until(() => !status.IsVisible);
            Program.Check(!Field<DispatcherTimer>(main, "_playerQueueStatusTimer").IsEnabled, "Queue feedback timer kept running after fade-out.");
            CheckPositions(baseline);
        }
        finally
        {
            Call(main, "HidePlayerQueueStatus");
            foreach (var id in added) manager.Remove(id);
            main.Width = 1280; main.Height = 860;
        }
        Console.WriteLine("PASS UI inline queue confirmation, fixed layout, eight-second fade and restart on repeated clicks");
    }

    private static async Task CheckQualityControls(ExportView export)
    {
        var original = (ExportPreset)Call(export, "CapturePreset", "Restore")!;
        var mode = Control<ComboBox>(export, "VideoRateControlCombo");
        var value = Control<TextBox>(export, "VideoBitrateBox");
        mode.SelectedIndex = 1;
        value.Text = "29";
        mode.SelectedIndex = 0;
        Program.Check(value.Text == "6", "Changing rate mode lost the bitrate.");
        mode.SelectedIndex = 1;
        Program.Check(value.Text == "29" && Control<TextBlock>(export, "VideoRateLabel").Text == "Quality (CRF)", "Quality value or CPU label changed.");
        Control<CheckBox>(export, "VideoGpuCheck").IsChecked = true;
        Program.Check(value.Text == "29" && Control<TextBlock>(export, "VideoRateLabel").Text == "Quality (CQ)", "GPU quality value or label changed.");
        Control<ComboBox>(export, "VideoCodecCombo").SelectedItem = "Copy";
        Program.Check(!mode.IsEnabled && !value.IsEnabled, "Copy allows editing rate control.");
        Control<ComboBox>(export, "VideoCodecCombo").SelectedItem = "H.264";
        Program.Check(mode.IsEnabled && value.Text == "29", "Leaving Copy lost the quality value.");
        Control<ComboBox>(export, "ResolutionCombo").SelectedItem = "2160p (4K)";
        var preset = (ExportPreset)Call(export, "CapturePreset", "4K quality")!;
        SettingsStore.AddOrUpdateExportPreset(preset);
        Call(export, "ApplyPreset", SettingsStore.FindExportPreset(preset.Name));
        Program.Check(preset.Resolution == "3840x2160" && preset.VideoQuality == "29" &&
                      Control<ComboBox>(export, "ResolutionCombo").SelectedItem as string == "2160p (4K)",
            "4K label or quality preset round-trip failed.");
        await Task.Delay(100);
        Capture((Window)TopLevel.GetTopLevel(export)!, "export-quality.png");
        Call(export, "ApplyPreset", original);
        await Task.Delay(100);
    }

    private static async Task CheckExportLog(ExportView export, ExportManagerTests.ControlledRunner.Execution execution)
    {
        var log = Control<TextBox>(export, "LogBox");
        await Task.Delay(250);
        var height = log.Bounds.Height;
        for (var line = 0; line < 500; line++) execution.Log($"FFmpeg test line {line}: {new string('x', 180)}");
        await Until(() => log.Text!.Contains("test line 499"));
        await Task.Delay(100);
        var scroller = log.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Program.Check(Math.Abs(height - 130) < 1 && Math.Abs(log.Bounds.Height - height) < 1 && scroller.Extent.Height > scroller.Viewport.Height &&
                      scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 2,
            "FFmpeg log expanded, cannot scroll, or stopped following new output.");
        scroller.Offset = new Vector(0, 60);
        await Task.Delay(100);
        var oldOffset = scroller.Offset;
        execution.Log("manual scroll check");
        await Until(() => log.Text!.Contains("manual scroll check"));
        await Task.Delay(150);
        Program.Check(Math.Abs(scroller.Offset.Y - oldOffset.Y) < 2, "New log lines interrupted manual scrolling.");
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        execution.Log("follow resumed");
        await Until(() => log.Text!.Contains("follow resumed"));
        await Task.Delay(100);
        Program.Check(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 2, "Log did not resume following at the bottom.");
        Console.WriteLine("PASS UI fixed-height FFmpeg log, scrollback preservation and follow at bottom");
    }

    private static async Task CheckPreparedImports(MainWindow main, IPlaybackEngine player, string source)
    {
        using var probe = new NativePlaybackProbe(player);
        foreach (var paused in new[] { false, true })
        {
            await player.SetPausedAsync(paused);
            await Until(() => player.GetSnapshot().IsPaused == paused);
            await SeekTests.SeekTo(player, probe, 2, paused ? .1 : .5);
            // A fresh import must supersede any position saved for restoring
            // the native host, including when the same file is opened again.
            typeof(MainWindow).GetField("_resumeSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(main, player.GetSnapshot() with { PositionSeconds = 2, IsPaused = paused });
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var expectedGeneration = player.Generation + 1;
            void DelaySetup(long generation)
            {
                if (generation != expectedGeneration) return;
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("Import setup was not released by the UI test.");
            }
            player.MediaLoaded += DelaySetup;
            try
            {
                Call(main, "LoadVideo", source);
                await Until(() => entered.IsSet);
                await Task.Delay(600);
                Program.Check(probe.IsPaused && probe.Position < .08 && !Control<Button>(main, "PlayPauseButton").IsEnabled,
                    $"Import started before audio setup: position={probe.Position}; paused={probe.IsPaused}.");
                release.Set();
                await Until(() => Control<Button>(main, "PlayPauseButton").IsEnabled);
                Program.Check(probe.Position < .25 && probe.IsPaused == paused,
                    $"Prepared import did not start at zero with its intended pause state: position={probe.Position}; paused={probe.IsPaused}.");
            }
            finally
            {
                release.Set();
                player.MediaLoaded -= DelaySetup;
            }
        }
        await player.SetPausedAsync(true);
        await Until(() => player.GetSnapshot().IsPaused);
        Console.WriteLine("PASS UI slow multi-track imports stay at zero until audio is configured, preserving playback intent");
    }

    private static async Task CheckRapidSeeking(MainWindow main, IPlaybackEngine player, string originalSource)
    {
        var fixture = await SeekTests.CreateFixture(Program.Temp);
        var generation = player.Generation;
        Call(main, "LoadVideo", fixture);
        await Until(() => player.Generation > generation && player.GetSnapshot().IsLoaded &&
            Control<Button>(main, "SkipForwardButton").IsEnabled);
        using var probe = new NativePlaybackProbe(player);
        var uiTimer = Field<DispatcherTimer>(main, "_uiTimer");
        uiTimer.Stop();
        try
        {
            await player.SetLoopingAsync(false);
            await player.SetPausedAsync(true);
            var root = Control<Grid>(main, "RootGrid");
            foreach (var fullscreen in new[] { false, true })
            {
                if (fullscreen)
                {
                    Call(main, "SetFullscreen", true);
                    await Until(() => main.WindowState == WindowState.FullScreen && root.IsFocused);
                }
                Control keySource = fullscreen ? Control<Border>(main, "FullscreenControlsBar") : root;
                await SeekTests.SeekTo(player, probe, 20);
                await Until(() => TimelinesAt(20));
                var timelineClock = System.Diagnostics.Stopwatch.StartNew();
                await SeekTests.Settled(probe, () =>
                {
                    // One UI dispatch, with no timer tick or manual refresh
                    // between presses: all five inputs must reach mpv.
                    for (var i = 0; i < 5; i++)
                        Program.Check(PressKey(keySource, Key.Right).Handled, "Right did not reach the player.");
                    return Task.CompletedTask;
                }, 45);
                await Until(() => TimelinesAt(45));
                Console.WriteLine($"MEASURE: {(fullscreen ? "fullscreen" : "windowed")} seek plus both timeline updates: {timelineClock.Elapsed.TotalMilliseconds:0.00} ms (UI polling stopped).");
                await SeekTests.Settled(probe, () =>
                {
                    for (var i = 0; i < 5; i++) PressKey(keySource, Key.Left);
                    return Task.CompletedTask;
                }, 20);
                await Until(() => TimelinesAt(20));
                await SeekTests.Settled(probe, () =>
                {
                    for (var i = 0; i < 5; i++) Click(main, "SkipForwardButton");
                    return Task.CompletedTask;
                }, 45);
                await SeekTests.Settled(probe, () =>
                {
                    for (var i = 0; i < 5; i++) Click(main, "SkipBackButton");
                    return Task.CompletedTask;
                }, 20);
                Program.Check(probe.IsPaused, "Rapid arrows or skip buttons resumed playback.");

                var restarts = probe.Restarts;
                await SeekTests.Settled(probe, async () =>
                {
                    for (var i = 0; i < 12; i++)
                    {
                        PressKey(keySource, Key.Right);
                        await Task.Delay(35);
                    }
                    Program.Check(probe.Restarts > restarts, "Repeated keys never showed an updated frame.");
                }, 80, .7);
                await SeekTests.Settled(probe, () =>
                {
                    PressKey(keySource, Key.Left);
                    PressKey(keySource, Key.Left);
                    PressKey(keySource, Key.Right);
                    return Task.CompletedTask;
                }, 75, .7);
                await player.SetPausedAsync(false);
                await SeekTests.SeekTo(player, probe, 20, 1.5);
                await SeekTests.Settled(probe, () =>
                {
                    for (var i = 0; i < 5; i++) PressKey(keySource, Key.Right);
                    return Task.CompletedTask;
                }, 45, 1.5);
                Program.Check(!probe.IsPaused, "Rapid arrows paused playing media.");
                await player.SetPausedAsync(true);
            }
            Console.WriteLine("PASS UI rapid arrows, skip buttons, sustained repeat and direction changes in windowed and fullscreen playback");
            Console.WriteLine("PASS UI both timelines refresh from native seek events with polling disabled");
        }
        finally
        {
            Call(main, "SetFullscreen", false);
            main.Activate();
            await Until(() => main.WindowState != WindowState.FullScreen && main.IsActive &&
                Control<Grid>(main, "RootGrid").IsFocused);
            generation = player.Generation;
            Call(main, "LoadVideo", originalSource);
            await Until(() => player.Generation > generation && player.GetSnapshot().IsLoaded &&
                Control<Button>(main, "SkipForwardButton").IsEnabled);
            await player.SetPausedAsync(true);
            await player.SetLoopingAsync(SettingsStore.Current.General.LoopByDefault);
            uiTimer.Start();
        }

        bool TimelinesAt(double target)
        {
            var position = Field<double>(main, "_currentPos");
            var playhead = Field<Control>(main, "_playhead");
            var expectedX = (double)Call(main, "TimeToX", target)!;
            return Math.Abs(position - target) < .4 &&
                Math.Abs(Control<Slider>(main, "FullscreenTimelineSlider").Value - target) < .4 &&
                Math.Abs(Canvas.GetLeft(playhead) - expectedX) < 1;
        }
    }

    private static async Task CheckPlaybackFocus(MainWindow main, IPlaybackEngine player, bool fullscreen = false)
    {
        var root = Control<Grid>(main, "RootGrid");
        await Until(() => root.IsFocused);
        var expander = Control<Expander>(main, "DiagnosticLogExpander");
        expander.IsExpanded = true;
        await Task.Delay(100);
        foreach (var name in new[] { "HeaderGrid", "ErrorBanner", "WorkspaceTabs", "VideoBorder", "PlayerSidebar", "NormalPlaybackArea", "ImportPrompt", "FullscreenControlsBar" })
        {
            var surface = Control<Control>(main, name);
            var focusable = surface.GetSelfAndVisualDescendants().OfType<Control>().Where(control => control.Focusable).ToArray();
            Program.Check(focusable.Length == 0,
                $"{name} contains focusable controls: {string.Join(", ", focusable.Select(control => control.GetType().Name + "#" + control.Name))}.");
        }
        expander.IsExpanded = false;
        foreach (var name in new[] { "CropStartBox", "CropEndBox" })
            Program.Check(Control<TextBox>(main, name).IsReadOnly && !Control<TextBox>(main, name).IsVisible,
                "A crop time editor remains visible or editable with the preference off.");
        Program.Check(Control<TextBlock>(main, "CropStartText").IsVisible && Control<TextBlock>(main, "CropEndText").IsVisible,
            "Default crop times are not plain text.");

        var buttonName = fullscreen ? "FullscreenPlayPauseButton" : "SkipForwardButton";
        await player.SeekAsync(0);
        await Until(() => player.GetSnapshot().PositionSeconds < .25);
        Call(main, "UpdatePlaybackUi", null, null);
        var seekTarget = Math.Min(5, player.GetSnapshot().DurationSeconds);
        Program.Check(!Control<Button>(main, buttonName).Focus(), "A player button accepted focus.");
        var paused = player.GetSnapshot().IsPaused;
        Click(main, buttonName);
        await Until(() => fullscreen ? player.GetSnapshot().IsPaused != paused : player.GetSnapshot().PositionSeconds >= seekTarget - .3);
        Program.Check(root.IsFocused, "Clicking a player control took keyboard focus.");
        await player.SetPausedAsync(true);
        await Until(() => player.GetSnapshot().IsPaused);
        Control source = fullscreen ? Control<Border>(main, "FullscreenControlsBar") : root;
        Program.Check(PressKey(source, Key.Space).Handled, "Space did not reach the playback shortcut handler.");
        await Until(() => !player.GetSnapshot().IsPaused);
        PressKey(root, Key.Space);
        await Until(() => player.GetSnapshot().IsPaused);
        if (OperatingSystem.IsLinux())
        {
            NativeKeyboard.Press(main, "space");
            await Until(() => !player.GetSnapshot().IsPaused);
            NativeKeyboard.Press(main, "space");
            await Until(() => player.GetSnapshot().IsPaused);
        }

        await player.SeekAsync(0);
        await Until(() => player.GetSnapshot().PositionSeconds < .25);
        Call(main, "UpdatePlaybackUi", null, null);
        var volume = Control<Slider>(main, fullscreen ? "FullscreenMasterVolumeSlider" : "MasterVolumeSlider");
        var volumeBefore = volume.Value;
        Program.Check(!volume.Focus(), "A volume slider accepted focus.");
        PressKey(root, Key.Right);
        await Until(() => player.GetSnapshot().PositionSeconds >= seekTarget - .3);
        Call(main, "UpdatePlaybackUi", null, null);
        PressKey(root, Key.Left);
        await Until(() => player.GetSnapshot().PositionSeconds < .25);
        Program.Check(volume.Value == volumeBefore, "Arrow shortcuts changed volume instead of seeking.");
        var speed = player.GetSnapshot().PlaybackSpeed;
        PressKey(source, Key.OemPlus, PhysicalKey.Equal);
        await Until(() => player.GetSnapshot().PlaybackSpeed > speed);
        PressKey(source, Key.OemMinus, PhysicalKey.Minus);
        await Until(() => Math.Abs(player.GetSnapshot().PlaybackSpeed - speed) < .001);
        Program.Check(root.IsFocused, "Fullscreen controls or playback shortcuts took keyboard focus.");
        PressKey(root, Key.Tab);
        Program.Check(root.IsFocused, "Tab moved focus into a player control.");
        if (fullscreen)
        {
            Call(main, "HideFullscreenControls");
            if (OperatingSystem.IsLinux()) NativeKeyboard.Press(main, "Escape");
            else PressKey(root, Key.Escape);
            await Until(() => main.WindowState != WindowState.FullScreen && root.IsFocused);
        }
        Console.WriteLine($"PASS UI {(fullscreen ? "fullscreen" : "windowed")} non-focusable player controls and playback shortcuts");
    }

    private static void CheckExportFocus(MainWindow main, ExportView export)
    {
        Program.Check(export.GetVisualDescendants().OfType<Button>()
            .Where(button => button.GetType() == typeof(Button) || button.GetType() == typeof(ToggleButton) || button is RepeatButton)
            .All(button => !button.Focusable && !button.IsTabStop), "Export action buttons can take focus.");
        var name = Control<TextBox>(export, "OutputNameBox");
        Program.Check(name.Focus() && name.IsFocused && Control<TextBox>(export, "VideoBitrateBox").Focusable &&
                      Control<ComboBox>(export, "ResolutionCombo").Focusable && Control<CheckBox>(export, "OverwriteCheck").Focusable,
            "Export form inputs lost keyboard focus.");
        Program.Check(!Control<Button>(export, "BrowseOutputButton").Focus() && name.IsFocused,
            "An Export action button stole focus from the filename.");
        foreach (var key in new[] { Key.Space, Key.Left, Key.Right, Key.F11 })
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = name, Key = key };
            Call(main, "MainWindowOnKeyDown", main, args);
            Program.Check(!args.Handled, "A playback shortcut intercepted Export keyboard input.");
        }
        Console.WriteLine("PASS UI Export buttons preserve focus and form inputs remain focusable");
    }

    private static KeyEventArgs PressKey(Control source, Key key, PhysicalKey physicalKey = PhysicalKey.None)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physicalKey };
        source.RaiseEvent(args);
        return args;
    }

    private static async Task CheckPlayerLayout(MainWindow main)
    {
        Control<ToggleButton>(main, "CropToggle").IsChecked = true;
        Call(main, "SetCropValues", .1d, 1.6d);
        Control<Grid>(main, "RootGrid").Focus();
        Control<StackPanel>(main, "AudioTracksPanel").GetVisualAncestors().OfType<ScrollViewer>().First().Offset = default;
        foreach (var size in new[] { (1280d, 860d), (900d, 650d), (1920d, 1080d) })
        {
            main.Width = size.Item1; main.Height = size.Item2;
            await Task.Delay(200);
            var sidebar = Control<Grid>(main, "PlayerSidebar");
            var audio = Control<Border>(main, "AudioPanel");
            var crop = Control<Border>(main, "CropPanel");
            Program.Check(Math.Abs(sidebar.Bounds.Width - 320) < 1 && Math.Abs(crop.Bounds.Height - 120) < 1,
                "Sidebar width or crop panel height changed.");
            Program.Check(crop.Parent == sidebar && audio.Parent == sidebar && crop.Bounds.Top >= audio.Bounds.Bottom,
                "Crop and audio do not occupy separate panels.");
            var playback = Control<Grid>(main, "NormalPlaybackArea");
            var fullscreen = Control<Button>(main, "FullscreenButton").TranslatePoint(default, playback)!.Value;
            var scroller = Control<ScrollViewer>(main, "TimelineScroller");
            foreach (var name in new[] { "CropStartBox", "CropEndBox" })
                Program.Check(Control<TextBox>(main, name).VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center,
                    "Crop time text is not vertically centered.");
            Program.Check(scroller.GetVisualDescendants().OfType<RepeatButton>()
                .Where(button => button.Name is "PART_LineUpButton" or "PART_LineDownButton").All(button => !button.IsVisible),
                "Timeline scrollbar arrows remain visible.");
            Program.Check(fullscreen.Y >= scroller.Bounds.Bottom && playback.Bounds.Width <= main.ClientSize.Width,
                "Fullscreen is above the timeline or playback controls overflow.");
            Capture(main, size.Item1 == 900 ? "player-minimum.png" : size.Item1 == 1920 ? "player-large.png" : "player.png");
        }
        main.Width = 1280; main.Height = 860;
        await Task.Delay(200);
        var tabs = Control<TabStrip>(main, "WorkspaceTabs");
        var audioBefore = Control<Border>(main, "AudioPanel").Bounds.Height;
        var tabHeightBefore = tabs.Bounds.Height;
        var additional = new TabStripItem { Content = "Future tab" };
        tabs.Items.Add(additional);
        await Task.Delay(150);
        Program.Check(tabs.Bounds.Height > tabHeightBefore && Control<Border>(main, "AudioPanel").Bounds.Height < audioBefore &&
                      Math.Abs(Control<Border>(main, "CropPanel").Bounds.Height - 120) < 1,
            "Adding a tab failed to resize navigation and audio independently of crop.");
        tabs.Items.Remove(additional);
        Control<ToggleButton>(main, "CropToggle").IsChecked = false;
        Console.WriteLine("PASS UI player layout at normal, minimum and large sizes; dynamic tabs and fixed crop panel");
    }

    private static async Task CheckPreferences(MainWindow main, IPlaybackEngine player)
    {
        var preferences = new PreferencesWindow();
        var dialog = preferences.ShowDialog<bool>(main);
        Control<ComboBox>(preferences, "PlaybackEndCombo").SelectedIndex = 1;
        Control<CheckBox>(preferences, "ShowDiagnosticLogCheck").IsChecked = true;
        Control<CheckBox>(preferences, "ShowPlayerQueueButtonCheck").IsChecked = true;
        foreach (var name in new[] { "PlaybackEndInfo", "ShowDiagnosticLogInfo", "ShowPlayerQueueButtonInfo", "EditableCropTimesInfo" })
            Program.Check(Control<TextBlock>(preferences, name).Text == "ⓘ", "A preference info tooltip is missing.");
        await Task.Delay(150);
        Capture(preferences, "preferences.png");
        Call(preferences, "SaveOnClick", null, new RoutedEventArgs());
        Program.Check(await dialog, "Preferences did not save.");
        main.ApplyPreferences();
        Program.Check(Control<Button>(main, "PlayerAddToQueueButton").IsVisible && SettingsStore.Current.General.ShowPlayerQueueButton,
            "Saving did not apply the player queue preference.");
        preferences = new PreferencesWindow();
        dialog = preferences.ShowDialog<bool>(main);
        Control<CheckBox>(preferences, "ShowPlayerQueueButtonCheck").IsChecked = false;
        Call(preferences, "CancelOnClick", null, new RoutedEventArgs());
        Program.Check(!await dialog && SettingsStore.Current.General.ShowPlayerQueueButton, "Cancel changed the queue shortcut preference.");
        preferences = new PreferencesWindow();
        dialog = preferences.ShowDialog<bool>(main);
        var speedUp = Control<TextBox>(preferences, "SpeedUpShortcutBox");
        var savedShortcut = speedUp.Text;
        PressKey(speedUp, Key.Tab, PhysicalKey.Tab);
        Program.Check(speedUp.Text == savedShortcut, "Tab was captured as a speed shortcut.");
        PressKey(speedUp, Key.Q, PhysicalKey.Q);
        Program.Check(speedUp.Text == "Q", "A speed shortcut key was not captured.");
        PressKey(speedUp, Key.Escape, PhysicalKey.Escape);
        Program.Check(!await dialog.WaitAsync(TimeSpan.FromSeconds(5)) && SettingsStore.GetPlaybackShortcuts().SpeedUpKey == "Equal",
            "Escape did not cancel Preferences or was captured as a shortcut.");
        var expander = Control<Expander>(main, "DiagnosticLogExpander");
        Program.Check(expander.IsVisible && !expander.IsExpanded, "Diagnostics was not enabled as a collapsed expander.");
        // A three-second fixture must stay loaded and play again after its end.
        await player.SetMasterVolumeAsync(0);
        await player.SeekAsync(Math.Max(0, player.GetSnapshot().DurationSeconds - .2));
        await player.SetPausedAsync(false);
        await Task.Delay(600);
        Program.Check(!player.GetSnapshot().IsPaused && player.GetSnapshot().PositionSeconds < 1.5, "Saved loop preference was not applied immediately.");
        await player.SetPausedAsync(true);
        SettingsStore.SavePreferences(null, Program.Temp, new(), false, true);
        main.ApplyPreferences();
        await player.SeekAsync(Math.Max(0, player.GetSnapshot().DurationSeconds - .2));
        await player.SetPausedAsync(false);
        await Until(() => !player.GetSnapshot().IsPaused);
        await Until(() => player.GetSnapshot().IsPaused);
        Program.Check(player.GetSnapshot().PositionSeconds > player.GetSnapshot().DurationSeconds - .5, "Stop preference continued looping.");
        expander.IsExpanded = true;
        SettingsStore.SavePreferences(null, Program.Temp, new(), true, false);
        main.ApplyPreferences();
        Program.Check(!expander.IsVisible && !expander.IsExpanded, "Disabling diagnostics retained its expander.");
        AppLog.Info("Hidden diagnostic test");
        await Task.Delay(250);
        Program.Check(Control<TextBox>(main, "LogTextBox").Text!.Contains("Hidden diagnostic test"), "Hiding diagnostics stopped log collection.");
        await player.SeekAsync(.25);
        Console.WriteLine("PASS UI Preferences save, immediate looping, collapsed diagnostics and logging while hidden");
    }

    private static async Task CheckCropToggleMemory(MainWindow main, IPlaybackEngine player)
    {
        var crop = Control<ToggleButton>(main, "CropToggle");
        var duration = player.GetSnapshot().DurationSeconds;
        crop.IsChecked = false;
        Call(main, "SetCropValues", 0d, 0d);
        await player.SeekAsync(duration);
        await Until(() => Field<double>(main, "_currentPos") > duration - .5);
        crop.IsChecked = true;
        Program.Check(Math.Abs(Field<double>(main, "_cropEnd") - duration) < .01 &&
                      Math.Abs(Field<double>(main, "_cropStart") - Math.Max(0, duration - 10)) < .01,
            "Turning Crop on at the end did not select the last ten seconds.");
        Call(main, "SetCropValues", .3d, .9d);
        crop.IsChecked = false;
        await player.SeekAsync(0);
        crop.IsChecked = true;
        Program.Check(Field<double>(main, "_cropStart") == .3 && Field<double>(main, "_cropEnd") == .9,
            "Turning Crop off and on lost the selection.");
        crop.IsChecked = false;
        Call(main, "SetCropValues", 0d, 0d);
        await Until(() => Field<double>(main, "_currentPos") < .5);
        Console.WriteLine("PASS UI crop keeps its selection and starts a full selection near the end");
    }

    private static void CheckAutomaticExportFolder(ExportView export)
    {
        var general = SettingsStore.Current.General;
        var shortcuts = SettingsStore.GetPlaybackShortcuts();
        var folderBox = Control<TextBox>(export, "OutputFolderBox");
        var first = Directory.CreateDirectory(Path.Combine(Program.Temp, "folder-a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(Program.Temp, "folder-b")).FullName;
        try
        {
            SettingsStore.SavePreferences(general.DefaultExportPresetName, null, shortcuts);
            folderBox.Text = "";
            export.SetContext(new ExportContext { SourcePath = Path.Combine(first, "a.mkv"), Duration = 5 });
            Program.Check(folderBox.Text == first, "The export folder did not start in the source video's folder.");
            export.SetContext(new ExportContext { SourcePath = Path.Combine(second, "b.mkv"), Duration = 5 });
            Program.Check(folderBox.Text == second, "The automatic export folder did not follow the next video.");
            export.ApplyOutputFolderSelection(second);
            export.SetContext(new ExportContext { SourcePath = Path.Combine(first, "c.mkv"), Duration = 5 });
            Program.Check(folderBox.Text == second, "A browsed export folder was replaced by the next video's folder.");
            folderBox.Text = "";
            SettingsStore.SavePreferences(general.DefaultExportPresetName, first, shortcuts);
            export.SetContext(new ExportContext { SourcePath = Path.Combine(second, "d.mkv"), Duration = 5 });
            Program.Check(folderBox.Text == first, "The Preferences export folder was not used.");
            SettingsStore.SavePreferences(general.DefaultExportPresetName, second, shortcuts);
            export.ApplyDefaultOutputFolder();
            Program.Check(folderBox.Text == second, "Changing the Preferences export folder did not apply.");
        }
        finally { SettingsStore.SavePreferences(general.DefaultExportPresetName, general.DefaultOutputFolder, shortcuts); }
        Console.WriteLine("PASS UI export folder follows each video unless chosen, and follows Preferences");
    }

    private static async Task CheckCropTimeEditing(MainWindow main, IPlaybackEngine player)
    {
        var root = Control<Grid>(main, "RootGrid");
        var start = Control<TextBox>(main, "CropStartBox");
        var end = Control<TextBox>(main, "CropEndBox");
        var startText = Control<TextBlock>(main, "CropStartText");
        var endText = Control<TextBlock>(main, "CropEndText");
        Control<ToggleButton>(main, "CropToggle").IsChecked = true;
        Call(main, "SetCropValues", .1d, 2d);
        Program.Check(startText.IsVisible && endText.IsVisible && !start.IsVisible && !end.IsVisible &&
                      startText.Text == "00:00:00.100" && endText.Text == "00:00:02.000",
            "Default crop times are not synchronized plain text.");

        var preferences = new PreferencesWindow();
        var dialog = preferences.ShowDialog<bool>(main);
        Program.Check(Control<CheckBox>(preferences, "EditableCropTimesCheck").IsChecked == false,
            "Crop time editing is enabled by default.");
        Control<CheckBox>(preferences, "EditableCropTimesCheck").IsChecked = true;
        Call(preferences, "SaveOnClick", null, new RoutedEventArgs());
        Program.Check(await dialog, "The crop time preference did not save.");
        main.ApplyPreferences();
        await Until(() => main.IsActive);
        await Task.Delay(100); // Allow activation's neutral-focus request to finish.
        Program.Check(start.IsVisible && end.IsVisible && !startText.IsVisible && !endText.IsVisible &&
                      !start.IsReadOnly && !end.IsReadOnly && start.Focusable && end.Focusable && start.IsTabStop && end.IsTabStop,
            "Saving did not replace crop labels with editable fields.");
        var focusable = Control<Grid>(main, "PlayerSidebar").GetVisualDescendants().OfType<Control>().Where(control => control.Focusable).ToArray();
        Program.Check(focusable.Length == 2 && focusable.Contains(start) && focusable.Contains(end),
            "Enabling crop editing made other player controls focusable.");
        await player.SetPausedAsync(true);
        await Until(() => player.GetSnapshot().IsPaused);
        Program.Check(start.Focus(), "The enabled crop editor cannot take focus.");
        var snapshot = player.GetSnapshot();
        foreach (var (key, physicalKey) in new[] { (Key.Space, PhysicalKey.Space), (Key.Left, PhysicalKey.ArrowLeft),
                     (Key.Right, PhysicalKey.ArrowRight), (Key.OemPlus, PhysicalKey.Equal), (Key.OemMinus, PhysicalKey.Minus) })
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = start, Key = key, PhysicalKey = physicalKey };
            Call(main, "MainWindowOnKeyDown", main, args);
            Program.Check(!args.Handled, "A playback shortcut intercepted crop time editing.");
        }
        if (OperatingSystem.IsLinux())
        {
            start.CaretIndex = start.Text!.Length;
            NativeKeyboard.Press(main, "Left");
            await Until(() => start.CaretIndex < start.Text.Length);
        }
        Program.Check(player.GetSnapshot().IsPaused && player.GetSnapshot().PlaybackSpeed == snapshot.PlaybackSpeed &&
                      Math.Abs(player.GetSnapshot().PositionSeconds - snapshot.PositionSeconds) < .01,
            "Editing a crop time changed playback.");
        start.SelectAll();
        start.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "00:00:00.500" });
        Program.Check(start.Text == "00:00:00.500", "Crop time typing or text selection failed.");
        PressKey(start, Key.Enter);
        Program.Check(Field<double>(main, "_cropStart") == .5 && startText.Text == start.Text && root.IsFocused,
            "Enter did not apply the crop time and return focus to the player.");

        end.Focus();
        end.Text = "00:00:01";
        start.Focus(); // Leaving a field must apply its pending timecode.
        Program.Check(Field<double>(main, "_cropEnd") == 1 && end.Text == "00:00:01.000" && endText.Text == end.Text,
            "Leaving an editor did not apply an integer-second timecode.");
        foreach (var invalid in new[] { "oops", "00:60:00", "00:00:60", "-1:00:00", "00:00:NaN" })
        {
            start.Focus();
            start.Text = invalid;
            PressKey(start, Key.Enter);
            Program.Check(Field<double>(main, "_cropStart") == .5 && start.Text == "00:00:00.500",
                "An invalid timecode changed the crop or remained in the editor.");
        }
        start.Focus();
        start.Text = "00:00:00.750";
        PressKey(start, Key.Escape);
        Program.Check(Field<double>(main, "_cropStart") == .5 && start.Text == startText.Text && root.IsFocused,
            "Escape did not cancel the crop time edit.");

        preferences = new PreferencesWindow();
        dialog = preferences.ShowDialog<bool>(main);
        Program.Check(Control<CheckBox>(preferences, "EditableCropTimesCheck").IsChecked == true,
            "Reopening Preferences lost the crop time editing setting.");
        Control<CheckBox>(preferences, "EditableCropTimesCheck").IsChecked = false;
        Call(preferences, "CancelOnClick", null, new RoutedEventArgs());
        Program.Check(!await dialog && SettingsStore.Current.General.EditableCropTimes && start.IsVisible,
            "Cancelling Preferences changed crop time editing.");
        preferences = new PreferencesWindow();
        dialog = preferences.ShowDialog<bool>(main);
        Control<CheckBox>(preferences, "EditableCropTimesCheck").IsChecked = false;
        Call(preferences, "SaveOnClick", null, new RoutedEventArgs());
        Program.Check(await dialog, "Disabling crop time editing did not save.");
        main.ApplyPreferences();
        Program.Check(startText.IsVisible && endText.IsVisible && !start.IsVisible && !end.IsVisible &&
                      !start.Focusable && !end.Focusable && startText.Text == "00:00:00.500" && endText.Text == "00:00:01.000",
            "Disabling editing lost crop times or retained focusable editors.");
        Control<ToggleButton>(main, "CropToggle").IsChecked = false;
        Console.WriteLine("PASS UI plain crop times, editable preference save/cancel, typing, shortcut isolation and timecode commits");
    }

    private static async Task CheckExportLayout(MainWindow main, ExportView export)
    {
        // The desktop may cap a requested window size below the available
        // space needed for both cards. Exercise a large layout independently
        // of that cap, then let the owner arrange the live page again.
        var available = new Size(1000, 1500);
        export.Measure(available);
        export.Arrange(new Rect(available));
        export.InvalidateMeasure();
        export.Measure(available);
        export.Arrange(new Rect(available));
        var video = Control<Border>(export, "VideoSettingsPanel");
        var audio = Control<Border>(export, "AudioSettingsPanel");
        var encoding = Control<ScrollViewer>(export, "EncodingScroller");
        Program.Check(video.Bounds.Height > video.MinHeight && audio.Bounds.Height > audio.MinHeight &&
                      Math.Abs(audio.Bounds.Bottom - encoding.Viewport.Height) < 2,
            $"Export panels do not stretch: viewport={encoding.Viewport.Height}, video={video.Bounds.Height}, audio={audio.Bounds}.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(1000, 1500)))
        {
            bitmap.Render(export);
            bitmap.Save(Path.Combine(Program.Temp, "export-large.png"), PngBitmapEncoderOptions.Default);
        }
        export.InvalidateMeasure();
        main.Width = 1280; main.Height = 860;
        await Task.Delay(200);
    }

    private static void CheckMasterMute(MainWindow main, IPlaybackEngine player)
    {
        var master = Control<Slider>(main, "MasterVolumeSlider");
        var fullscreen = Control<Slider>(main, "FullscreenMasterVolumeSlider");
        var mute = Control<ToggleButton>(main, "MuteAllButton");
        var tracks = player.AudioTracks.Select(track => (track.Enabled, track.VolumePercent)).ToArray();
        void CheckVolume(double value)
        {
            Program.Check(master.Value == value && fullscreen.Value == value && mute.IsChecked == (value == 0),
                "Mute state and master sliders disagree.");
            Program.Check(Control<TextBlock>(main, "MasterVolumeText").Text == $"{value:0}%" &&
                          Control<TextBlock>(main, "FullscreenMasterVolumeText").Text == $"{value:0}%",
                "Mute did not synchronize the master volume readouts.");
            Program.Check(player.AudioTracks.Select(track => (track.Enabled, track.VolumePercent)).SequenceEqual(tracks),
                "Master mute changed track selection or individual volume.");
        }

        master.Value = 0;
        Click(main, "MuteAllButton");
        CheckVolume(100);
        master.Value = 63.5;
        Click(main, "MuteAllButton");
        CheckVolume(0);
        Program.Check(AutomationProperties.GetName(mute)?.Contains("Restore") == true && ToolTip.GetTip(mute)?.ToString()?.Contains("64%") == true,
            "Muted toggle does not describe the restore action.");
        Click(main, "MuteAllButton");
        CheckVolume(63.5);
        fullscreen.Value = 27;
        Click(main, "MuteAllButton");
        CheckVolume(0);
        fullscreen.Value = 41;
        CheckVolume(41);
        master.Value = 0;
        Click(main, "MuteAllButton");
        CheckVolume(41);
        master.Value = 0;
        Console.WriteLine("PASS UI master mute restores previous volume, follows both sliders and preserves tracks");
    }

    private static async Task CheckTrackScrolling(MainWindow main)
    {
        main.Width = 900; main.Height = 730;
        await Task.Delay(200);
        var viewer = Control<ScrollViewer>(main, "AudioTracksScroller");
        var tracks = Control<StackPanel>(main, "AudioTracksPanel");
        var scrollbar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
        Program.Check(viewer.Extent.Height > viewer.Viewport.Height && scrollbar.IsVisible && Math.Abs(scrollbar.Bounds.Width - 10) < 1,
            "Track scrollbar is missing or does not match the timeline size.");
        Program.Check(scrollbar.GetVisualDescendants().OfType<RepeatButton>()
            .Where(button => button.Name is "PART_LineUpButton" or "PART_LineDownButton").All(button => !button.IsVisible),
            "Track scrollbar arrows remain visible.");
        var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var readouts = tracks.Children.OfType<StackPanel>().Select(panel => panel.Children.OfType<Grid>().Single().Children.OfType<TextBlock>().Single()).ToArray();
        var before = readouts.Select(text => text.TranslatePoint(default, viewer)!.Value.X).ToArray();
        using var pointer = new Avalonia.Input.Pointer(2, PointerType.Mouse, true);
        var root = TopLevel.GetTopLevel(viewer)!;
        var position = scrollbar.TranslatePoint(new Point(5, 5), root)!.Value;
        scrollbar.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, scrollbar, pointer, root, position, 0, new(), KeyModifiers.None));
        await Until(() => !scrollbar.IsExpanded);
        scrollbar.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, scrollbar, pointer, root, position, 0, new(), KeyModifiers.None));
        await Until(() => scrollbar.IsExpanded);
        await Task.Delay(120);
        var thumb = scrollbar.GetVisualDescendants().OfType<Thumb>().Single();
        Program.Check(thumb.RenderTransform is null || Math.Abs(thumb.RenderTransform.Value.M11 - 1) < 0.01,
            "Hovered track scrollbar thumb did not expand to full width.");
        Program.Check(presenter.Bounds.Right <= scrollbar.Bounds.Left && readouts.Select((text, index) =>
        {
            var point = text.TranslatePoint(default, viewer)!.Value;
            return Math.Abs(point.X - before[index]) < 1 && point.X + text.Bounds.Width + 7 <= scrollbar.Bounds.Left;
        }).All(clear => clear), "Expanded scrollbar covers track percentages or shifts the rows.");
        Capture(main, "track-scrollbar-expanded.png");
        viewer.Offset = default;
        Wheel(viewer);
        await Until(() => viewer.Offset.Y > 0);
        viewer.Offset = default;
        DragScrollThumb(viewer, scrollbar);
        await Until(() => viewer.Offset.Y > 0);
        viewer.Offset = default;
        main.Width = 1280; main.Height = 860;
        await Task.Delay(150);
        Console.WriteLine("PASS UI track scrollbar hover, reserved readout space, wheel and thumb dragging");
    }

    private static async Task CheckCompactExportLayout(MainWindow main, ExportView export)
    {
        // Reserve 40 pixels for desktop chrome on a 1080p display.
        main.Width = 1920; main.Height = 1040;
        var mode = Control<ComboBox>(export, "VideoRateControlCombo");
        var originalMode = mode.SelectedIndex;
        var folder = Control<TextBox>(export, "OutputFolderBox");
        var originalFolder = folder.Text;
        folder.Text = Path.Combine(Program.Temp, new string('x', 200), "Long export folder");
        foreach (var index in new[] { 0, 1 })
        {
            mode.SelectedIndex = index;
            await Task.Delay(200);
            var encoding = Control<ScrollViewer>(export, "EncodingScroller");
            Program.Check(encoding.Extent.Height <= encoding.Viewport.Height + 2,
                $"Default Export settings scroll on 1080p: extent={encoding.Extent.Height}, viewport={encoding.Viewport.Height}.");
            foreach (var name in new[] { "VideoPresetCombo", "VideoBitrateBox", "VideoRateControlCombo", "KeepTracksSeparateCheck", "IgnoreVolumeCheck", "OutputFolderBox", "BrowseOutputButton", "OverwriteCheck", "StartExportButton" })
                CheckInside(Control<Control>(export, name), export);
            Capture(main, index == 0 ? "export-1080p-bitrate.png" : "export-1080p-quality.png");
        }
        var splitter = Control<GridSplitter>(export, "ExportPaneSplitter");
        var columns = ((Grid)splitter.Parent!).ColumnDefinitions;
        columns[0].Width = new GridLength(380);
        await Task.Delay(200);
        Program.Check(Grid.GetRow(Control<Grid>(export, "VideoRateField")) == 1 && Grid.GetRow(Control<CheckBox>(export, "OverwriteCheck")) == 1,
            "Narrow export pane did not reflow paired fields.");
        foreach (var name in new[] { "VideoBitrateBox", "VideoRateControlCombo", "OutputFolderBox", "BrowseOutputButton", "OverwriteCheck" })
            CheckInside(Control<Control>(export, name), export);
        foreach (var name in new[] { "VideoAdvancedToggle", "AudioAdvancedToggle" }) Control<ToggleButton>(export, name).IsChecked = true;
        await Task.Delay(100);
        Program.Check(Control<TextBox>(export, "VideoAdvancedBox").IsVisible && Control<TextBox>(export, "AudioAdvancedBox").IsVisible,
            "Advanced export fields became inaccessible.");
        columns[0].Width = new GridLength(1, GridUnitType.Star);
        foreach (var name in new[] { "VideoAdvancedToggle", "AudioAdvancedToggle" }) Control<ToggleButton>(export, name).IsChecked = false;
        mode.SelectedIndex = originalMode;
        folder.Text = originalFolder;
        await Task.Delay(200);
        Program.Check(Grid.GetRow(Control<Grid>(export, "VideoRateField")) == 0 && Grid.GetRow(Control<CheckBox>(export, "OverwriteCheck")) == 0,
            "Wide export pane did not restore combined rows.");
        Console.WriteLine("PASS UI compact 1080p export in both rate modes, long folders, narrow divider and Advanced fields");
    }

    private static async Task CheckLongQueue(MainWindow main, ExportView export, ExportManager manager)
    {
        var list = Control<ListBox>(export, "QueueList");
        var outer = Control<ScrollViewer>(export, "QueueScroller");
        var names = new[] { "StartQueueButton", "RemoveJobButton", "SelectedJobText", "LogBox" };
        var positions = names.Select(name => Control<Control>(export, name).TranslatePoint(default, export)!.Value).ToArray();
        var listHeight = list.Bounds.Height;
        var options = manager.GetSnapshot().Jobs[0].Definition.Options;
        var added = new List<Guid>();
        try
        {
            for (var index = 0; index < 250; index++)
                added.Add(manager.AddToQueue(options with { OutputPath = Path.Combine(Program.Temp, $"scroll-job-{index}.mp4") }));
            Call(export, "RefreshQueue");
            await Task.Delay(250);
            var inner = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Program.Check(inner.Extent.Height > inner.Viewport.Height && Math.Abs(list.Bounds.Height - listHeight) < 1 && outer.Extent.Height <= outer.Viewport.Height + 2,
                "Long queue expanded its viewport or made the whole queue panel scroll.");
            for (var index = 0; index < names.Length; index++)
            {
                var control = Control<Control>(export, names[index]);
                var point = control.TranslatePoint(default, export)!.Value;
                Program.Check(Math.Abs(point.X - positions[index].X) < 1 && Math.Abs(point.Y - positions[index].Y) < 1,
                    $"Adding jobs moved {names[index]}.");
                CheckInside(control, export);
            }
            inner.Offset = default;
            Wheel(inner);
            await Until(() => inner.Offset.Y > 0);
            var scrollbar = inner.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
            inner.Offset = default;
            DragScrollThumb(inner, scrollbar);
            await Until(() => inner.Offset.Y > 0);
            list.ScrollIntoView(list.Items[^1]!);
            await Task.Delay(100);
            Program.Check(inner.Offset.Y > 0 && outer.Offset.Y == 0, "Scrolling to the last job moved the queue's outer controls.");
            Capture(main, "export-long-queue.png");
            main.Width = 900; main.Height = 650;
            await Task.Delay(200);
            Program.Check(list.Bounds.Height >= list.MinHeight && outer.Extent.Height > outer.Viewport.Height,
                "Small window lost the queue viewport or surrounding-controls fallback.");
            outer.Offset = new Vector(0, outer.Extent.Height - outer.Viewport.Height);
            await Task.Delay(100);
            CheckInside(Control<TextBox>(export, "LogBox"), export);
        }
        finally
        {
            foreach (var id in added) manager.Remove(id);
            Call(export, "RefreshQueue");
            outer.Offset = default;
            main.Width = 1280; main.Height = 860;
            await Task.Delay(200);
        }
        Console.WriteLine("PASS UI 250-job queue internal wheel/drag scrolling, fixed controls and small-window fallback");
    }

    private static void CheckInside(Control control, Control container)
    {
        var point = control.TranslatePoint(default, container)!.Value;
        Program.Check(control.Bounds.Width > 0 && control.Bounds.Height > 0 && point.X >= -1 && point.Y >= -1 &&
                      point.X + control.Bounds.Width <= container.Bounds.Width + 1 && point.Y + control.Bounds.Height <= container.Bounds.Height + 1,
            $"{control.Name} is outside its container: position={point}, bounds={control.Bounds}, container={container.Bounds}.");
    }

    private static void Wheel(ScrollViewer viewer)
    {
        var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        using var pointer = new Avalonia.Input.Pointer(3, PointerType.Mouse, true);
        var root = TopLevel.GetTopLevel(viewer)!;
        var position = presenter.TranslatePoint(new Point(5, 5), root)!.Value;
        presenter.RaiseEvent(new PointerWheelEventArgs(presenter, pointer, root, position, 0, new(), KeyModifiers.None, new Vector(0, -1)));
    }

    private static void DragScrollThumb(ScrollViewer viewer, ScrollBar scrollbar)
    {
        var thumb = scrollbar.GetVisualDescendants().OfType<Thumb>().Single();
        using var pointer = new Avalonia.Input.Pointer(4, PointerType.Mouse, true);
        var root = TopLevel.GetTopLevel(viewer)!;
        var position = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), root)!.Value;
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        thumb.RaiseEvent(new PointerPressedEventArgs(thumb, pointer, root, position, 0, down, KeyModifiers.None, 1));
        thumb.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, thumb, pointer, root, position + new Vector(0, 20), 1, down, KeyModifiers.None));
        thumb.RaiseEvent(new PointerReleasedEventArgs(thumb, pointer, root, position + new Vector(0, 20), 2, new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
    }

    private static void CheckVolumeDragging(Slider slider, Action? whileDragging = null)
    {
        Program.Check(slider.Bounds.Width > 0, "Volume slider has no width.");
        using var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
        var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        var root = TopLevel.GetTopLevel(slider)!;
        Point Position(double ratio) => slider.TranslatePoint(new Point(slider.Bounds.Width * ratio, slider.Bounds.Height / 2), root)!.Value;
        double Value(double ratio) => Math.Round(slider.Minimum + Math.Clamp(ratio, 0, 1) * (slider.Maximum - slider.Minimum), MidpointRounding.AwayFromZero);
        void Press(Control source, double ratio) => source.RaiseEvent(new PointerPressedEventArgs(source, pointer, root, Position(ratio), 0, down, KeyModifiers.None, 1));
        void Move(double ratio, PointerPointProperties properties) => slider.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, slider, pointer, root, Position(ratio), 0, properties, KeyModifiers.None));
        void Release(double ratio) => slider.RaiseEvent(new PointerReleasedEventArgs(slider, pointer, root, Position(ratio), 0, up, KeyModifiers.None, MouseButton.Left));

        Press(slider, .3);
        Program.Check(!slider.IsFocused && !slider.Focusable, "Dragging a volume slider took keyboard focus.");
        Program.Check(slider.Value == Value(.3) && ReferenceEquals(pointer.Captured, slider), $"Track press did not set and capture volume: value={slider.Value}, expected={Value(.3)}, capture={pointer.Captured?.GetType().Name ?? "none"}.");
        Move(.7, down);
        Program.Check(slider.Value == Value(.7), $"Volume did not follow the drag: value={slider.Value}, expected={Value(.7)}.");
        whileDragging?.Invoke();
        Move(1.2, down);
        Program.Check(slider.Value == slider.Maximum, "Dragging beyond the right edge did not clamp volume.");
        Move(-.2, down);
        Program.Check(slider.Value == slider.Minimum, "Dragging beyond the left edge did not clamp volume.");
        Release(.6);
        Program.Check(slider.Value == Value(.6) && pointer.Captured is null, "Release did not finalize volume and release capture.");
        Move(.9, up);
        Program.Check(slider.Value == Value(.6), "Volume changed after release.");

        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        Press(thumb, .6);
        Move(.4, down);
        Program.Check(slider.Value == Value(.4) && ReferenceEquals(pointer.Captured, slider), "Dragging from the thumb did not update volume.");
        pointer.Capture(null);
        Move(.9, down);
        Program.Check(slider.Value == Value(.4), "Volume continued dragging after capture loss.");

        Press(slider, .2);
        Move(.8, up);
        Program.Check(slider.Value == Value(.2) && pointer.Captured is null, "A missing release left the volume drag active.");
    }

    private static T Control<T>(Control window, string name) where T : Control => window.FindControl<T>(name)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Click(Control window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task SwitchTab(MainWindow main, bool export) => (Task)Call(main, "SelectWorkspaceTabAsync", export)!;
    private static void Capture(Window window, string filename)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(Program.Temp, filename), PngBitmapEncoderOptions.Default);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI state did not settle.");
            await Task.Delay(25);
        }
    }
}
