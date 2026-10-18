using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Satsuma.Core;

namespace Satsuma.UI;

/// <summary>
/// A pinned, always-on-top file card — a temporary shelf. Drag it anywhere on screen; drag the file out of it into
/// any app; double-click to open.
/// </summary>
public sealed class FloatWindow : Window
{
    static int _count;
    readonly string _path;

    public static void Open(string path)
    {
        var w = new FloatWindow(path);
        Native.GetCursorPos(out var p);
        var (work, scale) = Native.MonitorAt(p);
        int offset = (_count++ % 6) * 24;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = (work.Right / scale) - 196 - offset;
        w.Top = (work.Top / scale) + 24 + offset;
        w.Show();
    }

    FloatWindow(string path)
    {
        _path = path;
        Style = new Style(typeof(Window));
        Title = Path.GetFileName(path);
        Width = 172;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = Palette.Ui;
        SystemTheme.Refresh();
        Chrome.ApplyFlyoutChrome(this);

        var thumb = new Image
        {
            Source = ShellThumbnail.Get(path, 256),
            Height = 120,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(14, 18, 14, 8),
        };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);

        var name = new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\')),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Brush(Palette.Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12, 0, 12, 14),
        };

        var close = new Button
        {
            Content = "",
            FontFamily = Palette.Icons,
            FontSize = 9,
            Width = 24, Height = 24,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Focusable = false,
            Opacity = 0,
            Cursor = Cursors.Hand,
            Foreground = Palette.Brush(Palette.Text),
            Background = Palette.Brush(Palette.SurfaceAlt),
            ToolTip = "Close",
        };
        close.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border CornerRadius="12" Background="{TemplateBinding Background}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
            </ControlTemplate>
            """);
        close.Click += (_, _) => Close();

        var stack = new StackPanel();
        stack.Children.Add(thumb);
        stack.Children.Add(name);

        var root = new Grid { Background = Brushes.Transparent, ToolTip = path };
        root.Children.Add(stack);
        root.Children.Add(close);
        Content = root;

        MouseEnter += (_, _) => close.Opacity = 1;
        MouseLeave += (_, _) => close.Opacity = 0;
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Delete) Close(); };

        // Drag the thumbnail to drop the file into any app; drag anywhere else to move the card.
        Point? start = null;
        bool fromThumb = false;
        root.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { OpenFile(); e.Handled = true; return; }
            start = e.GetPosition(this);
            fromThumb = thumb.IsMouseOver;
        };
        root.PreviewMouseMove += (_, e) =>
        {
            if (start == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - start.Value;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
            bool moveWindow = !fromThumb || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            start = null;
            if (moveWindow) { DragMove(); return; }
            if (!File.Exists(_path) && !Directory.Exists(_path)) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { _path });
            var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            // Moved somewhere else → the shelf has done its job.
            if (result == DragDropEffects.Move || !File.Exists(_path) && !Directory.Exists(_path)) Close();
        };
        root.PreviewMouseLeftButtonUp += (_, _) => start = null;

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open", OpenFile));
        menu.Items.Add(MenuItem("Show in Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true })));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Close", Close));
        root.ContextMenu = menu;
    }

    static MenuItem MenuItem(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    void OpenFile()
    {
        try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>A tiny invisible top-level window the Windows share sheet can anchor to.</summary>
public sealed class ShareHost : Window
{
    static ShareHost? _instance;
    public static ShareHost Instance => _instance ??= new ShareHost();

    ShareHost()
    {
        Style = new Style(typeof(Window));
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Width = Height = 1;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "Satsuma Share";
    }

    public IntPtr Prepare()
    {
        Native.GetCursorPos(out var p);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible) Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, p.X, p.Y, 1, 1, Native.SWP_NOACTIVATE);
        Native.ForceForeground(hwnd);
        Activate();
        return hwnd;
    }
}
