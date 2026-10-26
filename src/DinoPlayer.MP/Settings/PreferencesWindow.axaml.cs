using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DinoPlayer.MP.Controls;

namespace DinoPlayer.MP.Settings;

public sealed partial class PreferencesWindow : Window
{
    private const string ManualOption = "— Manual —";
    private readonly ObservableCollection<string> _presetNames = new();
    private string _speedUpKey = "Equal";
    private string _speedUpModifiers = "None";
    private string _speedDownKey = "Minus";
    private string _speedDownModifiers = "None";

    public PreferencesWindow()
    {
        InitializeComponent();

        DefaultExportPresetCombo.ItemsSource = _presetNames;
        RefreshPresetNames();
        DefaultOutputFolderBox.Text = SettingsStore.GetDefaultOutputFolder();
        PlaybackEndCombo.ItemsSource = new[] { "Stop", "Loop" };
        var general = SettingsStore.Current.General;
        PlaybackEndCombo.SelectedIndex = general.LoopByDefault ? 1 : 0;
        ShowDiagnosticLogCheck.IsChecked = general.ShowDiagnosticLog;
        ShowPlayerQueueButtonCheck.IsChecked = general.ShowPlayerQueueButton;
        EditableCropTimesCheck.IsChecked = general.EditableCropTimes;
        var shortcuts = SettingsStore.GetPlaybackShortcuts();
        _speedUpKey = shortcuts.SpeedUpKey;
        _speedUpModifiers = shortcuts.SpeedUpModifiers;
        _speedDownKey = shortcuts.SpeedDownKey;
        _speedDownModifiers = shortcuts.SpeedDownModifiers;
        RefreshShortcutText();
        SpeedUpShortcutBox.KeyDown += SpeedUpShortcutBoxOnKeyDown;
        SpeedDownShortcutBox.KeyDown += SpeedDownShortcutBoxOnKeyDown;

        HoverPopup.ForText(DefaultExportPresetInfo,
            "Select a saved preset to initialize export settings on the first Export visit or first use of the player's Add to queue button. Your encoding choices are kept between clips. Choose Manual to use the export defaults.");
        HoverPopup.ForText(DefaultOutputFolderInfo,
            "New exports use this folder by default. If it is empty or unavailable, the source video's folder is used instead.");
        HoverPopup.ForText(SpeedControlsInfo,
            "Set the keyboard shortcuts used to speed up or slow down playback. The default keys are = / + and - / _." );
        HoverPopup.ForText(PlaybackEndInfo,
            "Stop pauses playback at the end of the video. Loop repeats the whole video, regardless of the crop selection. Saving applies this immediately.");
        HoverPopup.ForText(ShowDiagnosticLogInfo,
            "Show a collapsible diagnostic log below the player timeline. File logging and FFmpeg export logs remain active when it is hidden.");
        HoverPopup.ForText(ShowPlayerQueueButtonInfo,
            "Show Add to queue at the bottom left below the player timeline. Each click saves the current crop, audio choices, and export settings currently selected in Export. With Crop off, the whole video is queued. You stay in the player tab.");
        HoverPopup.ForText(EditableCropTimesInfo,
            "Replace the crop time labels with editable fields. Enter a timecode as HH:MM:SS or HH:MM:SS.mmm. Enter applies it; Escape cancels the edit. Playback shortcuts leave typing alone while a field has focus. Saving applies this immediately.");
    }

    private void RefreshPresetNames()
    {
        _presetNames.Clear();
        _presetNames.Add(ManualOption);

        foreach (var preset in SettingsStore.Current.ExportPresets)
        {
            if (!string.IsNullOrWhiteSpace(preset.Name))
                _presetNames.Add(preset.Name);
        }

        var defaultName = SettingsStore.GetDefaultExportPresetName();
        var selectedIndex = string.IsNullOrWhiteSpace(defaultName)
            ? 0
            : _presetNames.IndexOf(defaultName);
        DefaultExportPresetCombo.SelectedIndex = selectedIndex < 0 ? 0 : selectedIndex;
    }

    private void SaveOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var selectedName = DefaultExportPresetCombo.SelectedItem as string;
        var presetName = string.Equals(selectedName, ManualOption, StringComparison.Ordinal)
            ? null
            : selectedName;

        try
        {
            if (PlaybackShortcut.Conflicts(_speedUpKey, _speedUpModifiers, _speedDownKey, _speedDownModifiers))
                throw new InvalidOperationException("Speed up and slow down shortcuts must be different.");
            if (PlaybackShortcut.IsReserved(_speedUpKey) || PlaybackShortcut.IsReserved(_speedDownKey))
                throw new InvalidOperationException("That key is already used for playback or window navigation.");
            SettingsStore.SavePreferences(presetName, DefaultOutputFolderBox.Text, new PlaybackShortcutSettings
            {
                SpeedUpKey = _speedUpKey,
                SpeedUpModifiers = _speedUpModifiers,
                SpeedDownKey = _speedDownKey,
                SpeedDownModifiers = _speedDownModifiers
            }, PlaybackEndCombo.SelectedIndex == 1, ShowDiagnosticLogCheck.IsChecked == true,
                ShowPlayerQueueButtonCheck.IsChecked == true, EditableCropTimesCheck.IsChecked == true);
            Close(true);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save preferences: {exception.Message}";
            StatusText.IsVisible = true;
        }
    }

    private async void BrowseDefaultOutputFolderOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose default export folder",
            AllowMultiple = false
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            DefaultOutputFolderBox.Text = path;
    }

    private void CancelOnClick(object? sender, RoutedEventArgs eventArgs) => Close(false);

    private void SpeedUpShortcutBoxOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        CaptureShortcut(eventArgs, up: true);
        eventArgs.Handled = true;
    }

    private void SpeedDownShortcutBoxOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        CaptureShortcut(eventArgs, up: false);
        eventArgs.Handled = true;
    }

    private void CaptureShortcut(KeyEventArgs eventArgs, bool up)
    {
        if (eventArgs.PhysicalKey == PhysicalKey.None ||
            eventArgs.PhysicalKey.ToString().Contains("Shift", StringComparison.Ordinal) ||
            eventArgs.PhysicalKey.ToString().Contains("Control", StringComparison.Ordinal) ||
            eventArgs.PhysicalKey.ToString().Contains("Alt", StringComparison.Ordinal) ||
            eventArgs.PhysicalKey.ToString().Contains("Meta", StringComparison.Ordinal))
            return;

        var modifiers = eventArgs.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (up)
        {
            _speedUpKey = PlaybackShortcut.KeyName(eventArgs.PhysicalKey);
            _speedUpModifiers = PlaybackShortcut.ModifiersName(modifiers);
        }
        else
        {
            _speedDownKey = PlaybackShortcut.KeyName(eventArgs.PhysicalKey);
            _speedDownModifiers = PlaybackShortcut.ModifiersName(modifiers);
        }
        RefreshShortcutText();
    }

    private void RefreshShortcutText()
    {
        SpeedUpShortcutBox.Text = PlaybackShortcut.Format(_speedUpKey, _speedUpModifiers);
        SpeedDownShortcutBox.Text = PlaybackShortcut.Format(_speedDownKey, _speedDownModifiers);
    }

    private void ResetSpeedShortcutsOnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _speedUpKey = "Equal";
        _speedUpModifiers = "None";
        _speedDownKey = "Minus";
        _speedDownModifiers = "None";
        RefreshShortcutText();
    }
}
