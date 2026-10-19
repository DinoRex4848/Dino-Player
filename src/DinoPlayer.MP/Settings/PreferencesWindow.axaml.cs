using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace DinoPlayer.MP.Settings;

public sealed partial class PreferencesWindow : Window
{
    private const string ManualOption = "— Manual —";
    private readonly ObservableCollection<string> _presetNames = new();
    private readonly InfoTooltip _defaultExportPresetTooltip;
    private readonly InfoTooltip _defaultOutputFolderTooltip;
    private readonly InfoTooltip _speedControlsTooltip;
    private readonly InfoTooltip _playbackEndTooltip;
    private readonly InfoTooltip _showDiagnosticLogTooltip;
    private readonly InfoTooltip _showPlayerQueueButtonTooltip;
    private readonly InfoTooltip _editableCropTimesTooltip;
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
        PlaybackEndCombo.SelectedIndex = SettingsStore.Current.General.LoopByDefault ? 1 : 0;
        ShowDiagnosticLogCheck.IsChecked = SettingsStore.Current.General.ShowDiagnosticLog;
        ShowPlayerQueueButtonCheck.IsChecked = SettingsStore.Current.General.ShowPlayerQueueButton;
        EditableCropTimesCheck.IsChecked = SettingsStore.Current.General.EditableCropTimes;
        var shortcuts = SettingsStore.GetPlaybackShortcuts();
        _speedUpKey = shortcuts.SpeedUpKey;
        _speedUpModifiers = shortcuts.SpeedUpModifiers;
        _speedDownKey = shortcuts.SpeedDownKey;
        _speedDownModifiers = shortcuts.SpeedDownModifiers;
        RefreshShortcutText();
        SpeedUpShortcutBox.KeyDown += SpeedUpShortcutBoxOnKeyDown;
        SpeedDownShortcutBox.KeyDown += SpeedDownShortcutBoxOnKeyDown;

        _defaultExportPresetTooltip = new InfoTooltip(
            DefaultExportPresetInfo,
            "Select a saved preset to initialize export settings on the first Export visit or first use of the player's Add to queue button. Your encoding choices are kept between clips. Choose Manual to use the export defaults.");
        _defaultOutputFolderTooltip = new InfoTooltip(
            DefaultOutputFolderInfo,
            "New exports use this folder by default. If it is empty or unavailable, the source video's folder is used instead.");
        _speedControlsTooltip = new InfoTooltip(
            SpeedControlsInfo,
            "Set the keyboard shortcuts used to speed up or slow down playback. The default keys are = / + and - / _." );
        _playbackEndTooltip = new InfoTooltip(PlaybackEndInfo,
            "Stop pauses playback at the end of the video. Loop repeats the whole video, regardless of the crop selection. Saving applies this immediately.");
        _showDiagnosticLogTooltip = new InfoTooltip(ShowDiagnosticLogInfo,
            "Show a collapsible diagnostic log below the player timeline. File logging and FFmpeg export logs remain active when it is hidden.");
        _showPlayerQueueButtonTooltip = new InfoTooltip(ShowPlayerQueueButtonInfo,
            "Show Add to queue at the bottom left below the player timeline. Each click saves the current crop, audio choices, and export settings currently selected in Export. With Crop off, the whole video is queued. You stay in the player tab.");
        _editableCropTimesTooltip = new InfoTooltip(EditableCropTimesInfo,
            "Replace the crop time labels with editable fields. Enter a timecode as HH:MM:SS or HH:MM:SS.mmm. Enter applies it; Escape cancels the edit. Playback shortcuts leave typing alone while a field has focus. Saving applies this immediately.");
    }

    private sealed class InfoTooltip
    {
        private readonly TextBlock _target;
        private readonly DispatcherTimer _openTimer;
        private readonly Popup _popup;

        public InfoTooltip(TextBlock target, string message)
        {
            _target = target;
            _popup = new Popup
            {
                PlacementTarget = target,
                Placement = PlacementMode.Top,
                PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX,
                VerticalOffset = -4,
                Child = new Border
                {
                    Background = UiTheme.Resource<IBrush>("ControlBackground"),
                    BorderBrush = UiTheme.Resource<IBrush>("PanelBorder"),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(9, 5),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = message,
                        Foreground = UiTheme.Resource<IBrush>("TextPrimary"),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 300
                    }
                }
            };
            _openTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _openTimer.Tick += (_, _) =>
            {
                _openTimer.Stop();
                if (_target.IsPointerOver)
                    Show();
            };
            _target.DetachedFromVisualTree += (_, _) => { _openTimer.Stop(); _popup.IsOpen = false; };
            _target.PointerEntered += (_, _) =>
            {
                _openTimer.Stop();
                _openTimer.Start();
            };
            _target.PointerExited += (_, _) =>
            {
                _openTimer.Stop();
                _popup.IsOpen = false;
            };
        }

        private void Show()
        {
            if (_target.Bounds.Width > 0)
                _popup.IsOpen = true;
        }
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
            if (IsReserved(_speedUpKey) || IsReserved(_speedDownKey))
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

    private static bool IsReserved(string keyName)
    {
        if (!Enum.TryParse<PhysicalKey>(keyName, true, out var key))
            return true;
        return key is PhysicalKey.F11 or PhysicalKey.Escape or PhysicalKey.Space or PhysicalKey.ArrowLeft or PhysicalKey.ArrowRight;
    }
}
