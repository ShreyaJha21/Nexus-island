namespace Nexus.Core.Settings;

/// <summary>A parsed global shortcut such as "Ctrl+Alt+Space" or "Win+Shift+N".</summary>
public sealed record Shortcut(bool Ctrl, bool Alt, bool Shift, bool Win, uint VirtualKey, string Text)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Home"] = 0x24, ["End"] = 0x23, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
    };

    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        bool ctrl = false, alt = false, shift = false, win = false;
        uint? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (key is not null)
                    {
                        return null; // two non-modifier keys
                    }

                    key = KeyCode(raw);
                    if (key is null)
                    {
                        return null;
                    }

                    break;
            }
        }

        // A global shortcut needs a key and at least one modifier, or it would swallow normal typing.
        if (key is null || !(ctrl || alt || win))
        {
            return null;
        }

        return new Shortcut(ctrl, alt, shift, win, key.Value, text.Trim());
    }

    private static uint? KeyCode(string name)
    {
        if (NamedKeys.TryGetValue(name, out var named))
        {
            return named;
        }

        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        return null;
    }
}
