using Avalonia.Input;

namespace DinoPlayer.MP.Settings;

public static class PlaybackShortcut
{
    public static string Format(string keyName, string modifiers)
    {
        var modifierText = string.Equals(modifiers, "None", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : modifiers;
        return modifierText + (modifierText.Length > 0 ? "+" : string.Empty) + FormatKey(keyName);
    }

    public static string Format(PhysicalKey key, KeyModifiers modifiers)
    {
        var modifierText = FormatModifiers(modifiers);
        return modifierText.Length == 0 ? FormatKey(key.ToString()) : $"{modifierText}+{FormatKey(key.ToString())}";
    }

    public static bool TryParse(string keyName, string modifierText, out PhysicalKey key, out KeyModifiers modifiers)
    {
        if (!Enum.TryParse(keyName, ignoreCase: true, out key) || !Enum.IsDefined(key) || key == PhysicalKey.None)
        {
            key = PhysicalKey.None;
            modifiers = KeyModifiers.None;
            return false;
        }

        modifiers = ParseModifiers(modifierText);
        return string.IsNullOrWhiteSpace(modifierText) || modifierText.Split('+', StringSplitOptions.TrimEntries)
            .All(part => part.ToUpperInvariant() is "NONE" or "SHIFT" or "CTRL" or "CONTROL" or "ALT" or "META" or "WIN" or "CMD");
    }

    public static bool Matches(string keyName, string modifierText, KeyEventArgs eventArgs)
    {
        if (!TryParse(keyName, modifierText, out var key, out var modifiers) || eventArgs.PhysicalKey != key)
            return false;

        var actual = eventArgs.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (modifiers == KeyModifiers.None && (key == PhysicalKey.Equal || key == PhysicalKey.Minus))
            return actual == KeyModifiers.None || actual == KeyModifiers.Shift;
        return actual == modifiers;
    }

    public static bool Conflicts(string keyNameA, string modifierTextA, string keyNameB, string modifierTextB)
    {
        if (!TryParse(keyNameA, modifierTextA, out var keyA, out var modifiersA) ||
            !TryParse(keyNameB, modifierTextB, out var keyB, out var modifiersB) || keyA != keyB)
            return false;

        if (modifiersA == modifiersB)
            return true;
        if (keyA is PhysicalKey.Equal or PhysicalKey.Minus)
            return modifiersA == KeyModifiers.None || modifiersB == KeyModifiers.None
                ? (modifiersA == KeyModifiers.None && (modifiersB == KeyModifiers.None || modifiersB == KeyModifiers.Shift)) ||
                  (modifiersB == KeyModifiers.None && (modifiersA == KeyModifiers.None || modifiersA == KeyModifiers.Shift))
                : false;
        return false;
    }

    public static string KeyName(PhysicalKey key) => key.ToString();

    public static string ModifiersName(KeyModifiers modifiers) => FormatModifiers(modifiers);

    private static KeyModifiers ParseModifiers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "None", StringComparison.OrdinalIgnoreCase))
            return KeyModifiers.None;

        var modifiers = KeyModifiers.None;
        foreach (var item in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            modifiers |= item.ToUpperInvariant() switch
            {
                "SHIFT" => KeyModifiers.Shift,
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "ALT" => KeyModifiers.Alt,
                "META" or "WIN" or "CMD" => KeyModifiers.Meta,
                _ => KeyModifiers.None
            };
        }
        return modifiers;
    }

    private static string FormatModifiers(KeyModifiers modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & KeyModifiers.Meta) != 0) parts.Add("Meta");
        return string.Join('+', parts);
    }

    private static string FormatKey(string keyName) => keyName switch
    {
        "Equal" => "= / +",
        "Minus" => "- / _",
        "Space" => "Space",
        "Left" => "Left Arrow",
        "Right" => "Right Arrow",
        "Up" => "Up Arrow",
        "Down" => "Down Arrow",
        _ => keyName
    };
}
