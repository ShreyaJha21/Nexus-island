using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Nexus.Shell.Native;

/// <summary>
/// Follows the Windows accent color and the "Transparency effects" setting.
/// Accent: AccentLight2 for icons, rings and progress (readable on dark surfaces),
/// AccentDark1 for primary buttons, as Windows 11 itself does in dark mode.
/// </summary>
internal sealed class SystemTheme
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly UISettings _ui = new();
    private readonly bool _useAccent;

    public SystemTheme(bool useSystemAccent)
    {
        _useAccent = useSystemAccent;
        Apply();
        _ui.ColorValuesChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(Apply);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                Application.Current.Dispatcher.InvokeAsync(Apply);
            }
        };
    }

    public static bool TransparencyEnabled =>
        Registry.GetValue(PersonalizeKey, "EnableTransparency", 1) is not 0;

    public void Apply()
    {
        DebugLog.Write("SystemTheme.Apply");
        var resources = Application.Current.Resources;

        if (_useAccent)
        {
            try
            {
                resources["Brush.Accent"] = Brush(_ui.GetColorValue(UIColorType.AccentLight2));
                resources["Brush.AccentFill"] = Brush(_ui.GetColorValue(UIColorType.AccentDark1));
                resources["Brush.AccentFillHover"] = Brush(_ui.GetColorValue(UIColorType.Accent));
                resources["Brush.AccentFillPressed"] = Brush(_ui.GetColorValue(UIColorType.AccentDark2));
            }
            catch (Exception)
            {
                // Keep Fluent's default brand colors.
            }
        }

        // Over a blurred backdrop the island is a translucent tint, so the colors behind show through
        // softly, like the Start menu. Without blur it is nearly opaque (text over sharp, unblurred
        // content would be hard to read), and solid when transparency effects are off.
        resources["Brush.Island"] = new SolidColorBrush(!TransparencyEnabled
            ? Color.FromRgb(0x1F, 0x1F, 0x1F)
            : Color.FromArgb(IslandAlpha(_backdrop), 0x14, 0x14, 0x14));
    }

    private Backdrop.Kind _backdrop = Backdrop.Kind.None;

    /// <summary>Called once the island knows whether Windows will blur behind it.</summary>
    public void SetBackdrop(Backdrop.Kind kind)
    {
        _backdrop = kind;
        Apply();
    }

    private static byte IslandAlpha(Backdrop.Kind kind)
    {
        var overrideHex = Environment.GetEnvironmentVariable("NEXUS_TINT");
        if (kind != Backdrop.Kind.None && overrideHex is { Length: 2 } && byte.TryParse(overrideHex,
                System.Globalization.NumberStyles.HexNumber, null, out var custom))
        {
            return custom;
        }

        return kind switch
        {
            Backdrop.Kind.SystemAcrylic => 0x80, // Windows' acrylic already adds its own dark tint
            Backdrop.Kind.Blur => 0xB8,          // plain blur needs more tint to keep text readable
            _ => 0xF7,
        };
    }

    private static SolidColorBrush Brush(Windows.UI.Color c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
