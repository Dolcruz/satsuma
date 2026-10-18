using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Satsuma.Core;
using Forms = System.Windows.Forms;

namespace Satsuma.UI;

/// <summary>System tray icon with a Fluent (WPF-UI styled) context menu instead of the dated WinForms one.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    ContextMenu? _menu;
    public bool Paused { get; private set; }

    public event Action? PausedChanged;

    public Tray()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Satsuma.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Satsuma — drag a file, tap Shift",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SettingsWindow.ShowSingle();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    void ShowMenu()
    {
        _menu ??= BuildMenu();
        ((MenuItem)_menu.Items[1]).Header = Paused ? "Resume gestures" : "Pause gestures";
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Settings", "", SettingsWindow.ShowSingle));
        menu.Items.Add(Item("Pause gestures", "", () => { Paused = !Paused; PausedChanged?.Invoke(); }));
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
