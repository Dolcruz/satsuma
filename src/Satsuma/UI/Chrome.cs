using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Satsuma.Core;

namespace Satsuma.UI;

/// <summary>Theme colors matching Windows 11's own flyouts and toasts.</summary>
public static class Palette
{
    public static bool Dark => SystemTheme.IsDark;
    public static Color Surface => Dark ? Color.FromRgb(44, 44, 44) : Color.FromRgb(249, 249, 249);
    public static Color SurfaceAlt => Dark ? Color.FromRgb(56, 56, 56) : Color.FromRgb(240, 240, 240);
    public static Color Text => Dark ? Color.FromRgb(255, 255, 255) : Color.FromRgb(26, 26, 26);
    public static Color Muted => Dark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0);
    public static Color Divider => Dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0);
    public static Color Danger => Dark ? Color.FromRgb(255, 153, 164) : Color.FromRgb(196, 43, 28);

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

/// <summary>Borderless window that still gets DWM's Windows 11 rounded corners and shadow.</summary>
public static class Chrome
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;

    public static void ApplyFlyoutChrome(Window w)
    {
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.SourceInitialized += (_, _) => Refresh(w);
        // Keep our own surface color even when WPF-UI re-themes all app windows.
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(System.Windows.Controls.Control.BackgroundProperty, typeof(Window))
            .AddValueChanged(w, (_, _) =>
            {
                if (w.Background is not SolidColorBrush b || b.Color != Palette.Surface)
                    w.Background = Palette.Brush(Palette.Surface);
            });
    }

    public static void Refresh(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = SystemTheme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var c = SystemTheme.IsDark ? Color.FromRgb(70, 70, 70) : Color.FromRgb(214, 214, 214);
        int colorRef = c.R | (c.G << 8) | (c.B << 16);
        Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        w.Background = Palette.Brush(Palette.Surface);
    }
}
