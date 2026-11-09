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

        // When transparency effects are off, surfaces fall back to solid.
        resources["Brush.Island"] = TransparencyEnabled
            ? new SolidColorBrush(Color.FromArgb(0xF7, 0x14, 0x14, 0x14))
            : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
    }

    private static SolidColorBrush Brush(Windows.UI.Color c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
