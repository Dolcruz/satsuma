using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace Satsuma.Core;

/// <summary>Reads the user's Windows personalization so every surface matches the OS. Values are cached; call Refresh().</summary>
public static class SystemTheme
{
    static bool _dark;
    static Color _accent = Color.FromRgb(0, 120, 212);

    static SystemTheme() => Refresh();

    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _dark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { _dark = false; }

        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            // Dark mode uses a lighter accent shade, light mode a darker one — same as Windows does.
            var c = ui.GetColorValue(_dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            _accent = Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch { }
    }

    public static bool IsDark => _dark;

    public static Color Accent => _accent;

    /// <summary>Foreground color that reads well on top of the accent fill.</summary>
    public static Color OnAccent
    {
        get
        {
            var a = _accent;
            double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255;
            return lum > 0.6 ? Colors.Black : Colors.White;
        }
    }

    public static bool AnimationsEnabled => System.Windows.SystemParameters.ClientAreaAnimation;
}
