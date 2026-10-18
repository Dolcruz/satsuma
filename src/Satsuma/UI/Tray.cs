using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Satsuma.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Satsuma.UI;

/// <summary>System tray icon with a Fluent (WPF-UI styled) context menu instead of the dated WinForms one.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Drawing.Icon _activeIcon, _pausedIcon;
    ContextMenu? _menu;

    public bool Paused => Settings.Current.Paused;

    public Tray()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Satsuma.ico"))!.Stream;
        _activeIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        _pausedIcon = Grayscale(_activeIcon);
        _icon = new Forms.NotifyIcon { Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SettingsWindow.ShowSingle();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        Settings.Changed += UpdateState;
        UpdateState();
    }

    void UpdateState()
    {
        _icon.Icon = Paused ? _pausedIcon : _activeIcon;
        _icon.Text = Paused ? "Satsuma — paused" : "Satsuma — drag a file, tap Shift";
    }

    /// <summary>Desaturated, slightly faded copy of the app icon for the paused state.</summary>
    static Drawing.Icon Grayscale(Drawing.Icon source)
    {
        using var src = source.ToBitmap();
        var dst = new Drawing.Bitmap(src.Width, src.Height);
        using (var g = Drawing.Graphics.FromImage(dst))
        {
            var m = new Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.75f, 0 },
                new[] { 0f, 0, 0, 0, 1 },
            });
            using var attrs = new Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(m);
            g.DrawImage(src, new Drawing.Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, Drawing.GraphicsUnit.Pixel, attrs);
        }
        return Drawing.Icon.FromHandle(dst.GetHicon());
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    void ShowMenu()
    {
        App.EnsureFluent();
        _menu ??= BuildMenu();
        ((MenuItem)_menu.Items[1]).Header = Paused ? "Resume Satsuma" : "Pause Satsuma";
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Settings", "", SettingsWindow.ShowSingle));
        menu.Items.Add(Item("Pause Satsuma", "", () => Settings.SetPaused(!Paused)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Satsuma", "", () => Application.Current.Shutdown()));
        menu.Opened += (_, _) =>
        {
            // Popups only close on outside clicks when their window is foreground.
            if (PresentationSource.FromVisual(menu) is HwndSource src) Native.SetForegroundWindow(src.Handle);
        };
        return menu;
    }

    static MenuItem Item(string header, string glyph, Action click)
    {
        var mi = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = Palette.Icons, FontSize = 14 },
        };
        mi.Click += (_, _) => click();
        return mi;
    }

    public void Dispose()
    {
        Settings.Changed -= UpdateState;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
