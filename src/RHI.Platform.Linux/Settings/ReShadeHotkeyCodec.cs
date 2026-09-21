namespace RHI.Platform.Linux.Settings;

/// <summary>
/// Converts the ReShade four-integer shortcut representation
/// (virtual key, Ctrl, Shift, Alt) into a user-facing binding.
/// </summary>
public static class ReShadeHotkeyCodec
{
    public sealed record KeyChoice(int KeyCode, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public readonly record struct Binding(int KeyCode, bool Ctrl, bool Shift, bool Alt)
    {
        public string ToConfigValue() => $"{KeyCode},{(Ctrl ? 1 : 0)},{(Shift ? 1 : 0)},{(Alt ? 1 : 0)}";

        public string ToDisplayText()
        {
            var key = FindChoice(KeyCode)?.DisplayName ?? "Tecla existente (não reconhecida)";
            var parts = new List<string>(4);
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            parts.Add(key);
            return string.Join(" + ", parts);
        }
    }

    public static IReadOnlyList<KeyChoice> CommonKeys { get; } = BuildCommonKeys();

    public static Binding ParseOrDefault(string? value, string fallback)
    {
        if (TryParse(value, out var binding)) return binding;
        if (TryParse(fallback, out binding)) return binding;
        return new Binding(36, false, false, false);
    }

    public static bool TryParse(string? value, out Binding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var keyCode) ||
            !int.TryParse(parts[1], out var ctrl) ||
            !int.TryParse(parts[2], out var shift) ||
            !int.TryParse(parts[3], out var alt) ||
            keyCode is < 0 or > 255)
        {
            return false;
        }

        binding = new Binding(keyCode, ctrl != 0, shift != 0, alt != 0);
        return true;
    }

    public static KeyChoice? FindChoice(int keyCode) => CommonKeys.FirstOrDefault(choice => choice.KeyCode == keyCode);

    private static IReadOnlyList<KeyChoice> BuildCommonKeys()
    {
        var keys = new List<KeyChoice>
        {
            new(8, "Backspace"),
            new(9, "Tab"),
            new(13, "Enter"),
            new(19, "Pause"),
            new(20, "Caps Lock"),
            new(27, "Esc"),
            new(32, "Espaço"),
            new(33, "Page Up"),
            new(34, "Page Down"),
            new(35, "End (Fim)"),
            new(36, "Home (Início)"),
            new(37, "Seta para a esquerda"),
            new(38, "Seta para cima"),
            new(39, "Seta para a direita"),
            new(40, "Seta para baixo"),
            new(44, "Print Screen"),
            new(45, "Insert (Inserir)"),
            new(46, "Delete (Excluir)"),
        };

        for (var key = 0; key <= 9; key++)
            keys.Add(new KeyChoice(48 + key, key.ToString()));

        for (var letter = 'A'; letter <= 'Z'; letter++)
            keys.Add(new KeyChoice(letter, letter.ToString()));

        for (var key = 0; key <= 9; key++)
            keys.Add(new KeyChoice(96 + key, $"Numpad {key}"));

        keys.Add(new KeyChoice(106, "Numpad *"));
        keys.Add(new KeyChoice(107, "Numpad +"));
        keys.Add(new KeyChoice(109, "Numpad -"));
        keys.Add(new KeyChoice(110, "Numpad decimal"));
        keys.Add(new KeyChoice(111, "Numpad /"));

        for (var key = 1; key <= 24; key++)
            keys.Add(new KeyChoice(111 + key, $"F{key}"));

        keys.Add(new KeyChoice(144, "Num Lock"));
        keys.Add(new KeyChoice(145, "Scroll Lock"));

        return keys;
    }
}
