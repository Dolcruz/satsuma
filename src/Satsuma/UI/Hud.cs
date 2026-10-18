using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Satsuma.Core;

namespace Satsuma.UI;

/// <summary>Progress cards in the bottom-right corner, styled like Windows 11 notifications. Never steals focus.</summary>
public static class Hud
{
    static HudWindow? _window;
    static HudWindow Window => _window ??= new HudWindow();

    public static HudJob Start(string title, string subtitle, string? thumbnailFor) => Window.AddJob(title, subtitle, thumbnailFor);

    public static bool IsBusy => _window?.IsVisible == true;

    public static void Flash(string title, string subtitle, string? reveal = null)
    {
        var job = Window.AddJob(title, subtitle, null, glyph: "");
        job.CompleteSimple(reveal);
    }
}

public sealed class HudJob
{
    internal HudJob(HudWindow owner, Border view, TextBlock title, TextBlock subtitle, ProgressBar bar, StackPanel buttons, Border iconHost)
    {
        _owner = owner; View = view; _title = title; _subtitle = subtitle; _bar = bar; _buttons = buttons; _iconHost = iconHost;
    }

    readonly HudWindow _owner;
    readonly TextBlock _title, _subtitle;
    readonly ProgressBar _bar;
    readonly StackPanel _buttons;
    readonly Border _iconHost;
    internal Border View { get; }
    internal bool Running { get; private set; } = true;
    internal DateTime FinishedAt;
    readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;

    double _lastProgress = -1;

    public void Progress(double f) => _owner.Dispatcher.BeginInvoke(() =>
    {
        if (!Running) return;
        if (Math.Abs(f - _lastProgress) < 0.002) return;
        _lastProgress = f;
        _bar.IsIndeterminate = f <= 0;
        _bar.Value = f * 100;
    });

    public void Status(string text) => _owner.Dispatcher.BeginInvoke(() => { if (Running) _subtitle.Text = text; });

    internal void AddCancel()
    {
        _buttons.Children.Clear();
        _buttons.Children.Add(HudWindow.IconButton("", "Cancel", () => _cts.Cancel()));
    }

    public void Complete(IReadOnlyList<string> outputs) => _owner.Dispatcher.BeginInvoke(() =>
    {
        Running = false;
        FinishedAt = DateTime.Now;
        _bar.Visibility = Visibility.Collapsed;
        SetGlyph("", true);
        var first = outputs.FirstOrDefault();
        _title.Text = outputs.Count switch
        {
            0 => "Done",
            1 => Path.GetFileName(first!.TrimEnd('\\')),
            _ => $"{outputs.Count} files created",
        };
        _subtitle.Text = first == null ? "" : "Saved to " + ShortDir(Path.GetDirectoryName(first.TrimEnd('\\')) ?? "");
        _buttons.Children.Clear();
        if (first != null)
        {
            _buttons.Children.Add(HudWindow.TextButton("Show", () => Reveal(outputs)));
            if (outputs.Count == 1)
            {
                _buttons.Children.Add(HudWindow.TextButton("Open", () => OpenFile(first)));
                View.Cursor = Cursors.Hand;
                View.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) OpenFile(first); };
                // Results can be dragged straight out of the card into any app.
                HudWindow.MakeDraggable(View, outputs);
            }
            if (Settings.Current.RevealResultInExplorer) Reveal(outputs);
        }
        _owner.ScheduleHide();
    });

    internal void CompleteSimple(string? reveal) => _owner.Dispatcher.BeginInvoke(() =>
    {
        Running = false;
        FinishedAt = DateTime.Now;
        _bar.Visibility = Visibility.Collapsed;
        _buttons.Children.Clear();
        if (reveal != null) _buttons.Children.Add(HudWindow.TextButton("Show", () => Reveal(new[] { reveal })));
        _owner.ScheduleHide();
    });

    public void Fail(string message) => _owner.Dispatcher.BeginInvoke(() =>
    {
        Running = false;
        FinishedAt = DateTime.Now.AddSeconds(4); // errors linger a bit longer
        _bar.Visibility = Visibility.Collapsed;
        SetGlyph("", false, Palette.Danger);
        _title.Text = _cts.IsCancellationRequested ? "Cancelled" : "Couldn't finish";
        _subtitle.Text = _cts.IsCancellationRequested ? _subtitle.Text : message;
        _subtitle.TextWrapping = TextWrapping.Wrap;
        _subtitle.MaxHeight = 54;
        _buttons.Children.Clear();
        _owner.ScheduleHide();
    });

    void SetGlyph(string glyph, bool accent, Color? color = null)
    {
        _iconHost.Child = new TextBlock
        {
            Text = glyph, FontFamily = Palette.Icons, FontSize = 18,
            Foreground = Palette.Brush(color ?? (accent ? SystemTheme.Accent : Palette.Text)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static string ShortDir(string dir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = Path.GetFileName(dir);
        return string.IsNullOrEmpty(name) ? dir : name;
    }

    static void Reveal(IReadOnlyList<string> paths)
    {
        try
        {
            var p = paths[0].TrimEnd('\\');
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
        }
        catch { }
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}

public sealed class HudWindow : Window
{
    readonly StackPanel _stack = new();
    readonly List<HudJob> _jobs = new();
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    IntPtr _hwnd;

    public HudWindow()
    {
        Style = new Style(typeof(Window));
        Title = "Satsuma";
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 372;
        SizeToContent = SizeToContent.Height;
        FontFamily = Palette.Ui;
        Chrome.ApplyFlyoutChrome(this);
        Content = _stack;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd, noActivate: true);
        };
        SizeChanged += (_, _) => Reposition();
        _hideTimer.Tick += (_, _) => Sweep();
    }

    internal HudJob AddJob(string title, string subtitle, string? thumbnailFor, string? glyph = null)
    {
        SystemTheme.Refresh();
        Chrome.Refresh(this);

        var iconHost = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var thumb = thumbnailFor != null ? ShellThumbnail.Get(thumbnailFor, 64) : null;
        if (thumb != null) iconHost.Child = new Image { Source = thumb, Stretch = Stretch.Uniform };
        else iconHost.Child = new TextBlock
        {
            Text = glyph ?? "", FontFamily = Palette.Icons, FontSize = 18, Foreground = Palette.Brush(SystemTheme.Accent),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        var t = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Brush(Palette.Text), TextTrimming = TextTrimming.CharacterEllipsis };
        var s = new TextBlock { Text = subtitle, FontSize = 12, Foreground = Palette.Brush(Palette.Muted), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var bar = new ProgressBar
        {
            Height = 3, Minimum = 0, Maximum = 100, IsIndeterminate = true, Margin = new Thickness(0, 9, 0, 1),
            Foreground = Palette.Brush(SystemTheme.Accent), Background = Palette.Brush(Palette.Divider), BorderThickness = new Thickness(0),
        };
        bar.Template = ThinBarTemplate();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(t);
        textCol.Children.Add(s);
        textCol.Children.Add(bar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textCol, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(iconHost);
        grid.Children.Add(textCol);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Padding = new Thickness(16, 14, 12, 14),
            Child = grid,
            BorderBrush = Palette.Brush(Palette.Divider),
            BorderThickness = new Thickness(0, _stack.Children.Count > 0 ? 1 : 0, 0, 0),
            Background = Brushes.Transparent,
        };

        var job = new HudJob(this, card, t, s, bar, buttons, iconHost);
        job.AddCancel();
        _jobs.Add(job);
        _stack.Children.Add(card);
        Fix_Borders();

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            Reposition();
            if (SystemTheme.AnimationsEnabled)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            }
            else Opacity = 1;
        }
        _hideTimer.Stop();
        return job;
    }

    void Fix_Borders()
    {
        for (int i = 0; i < _stack.Children.Count; i++)
            if (_stack.Children[i] is Border b) b.BorderThickness = new Thickness(0, i > 0 ? 1 : 0, 0, 0);
    }

    internal void ScheduleHide() => _hideTimer.Start();

    void Sweep()
    {
        if (IsMouseOver) return;
        var now = DateTime.Now;
        foreach (var job in _jobs.Where(j => !j.Running && (now - j.FinishedAt).TotalSeconds > 4.5).ToList())
        {
            _jobs.Remove(job);
            _stack.Children.Remove(job.View);
        }
        Fix_Borders();
        if (_jobs.Count == 0)
        {
            _hideTimer.Stop();
            Hide();
            MemoryTrim.Schedule();
        }
    }

    void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.GetCursorPos(out var cursor);
        // Like toasts: bottom-right of the primary monitor's work area.
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
    }

    internal static Button TextButton(string text, Action click)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 6),
            FontSize = 12.5,
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = ButtonTemplate(),
            Foreground = Palette.Brush(Palette.Text),
            Background = Palette.Brush(Palette.SurfaceAlt),
        };
        b.Click += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    internal static Button IconButton(string glyph, string tip, Action click)
    {
        var b = TextButton(glyph, click);
        b.FontFamily = Palette.Icons;
        b.FontSize = 11;
        b.Padding = new Thickness(9, 8, 9, 8);
        b.Background = Brushes.Transparent;
        b.ToolTip = tip;
        return b;
    }

    static ControlTemplate ButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82, "bd"));
        t.Triggers.Add(hover);
        return t;
    }

    static ControlTemplate ThinBarTemplate()
    {
        // Stock ProgressBar template with a rounded track — the parts are what drive the indicator.
        var xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
              <Border x:Name="PART_Track" CornerRadius="1.5" Background="{TemplateBinding Background}" ClipToBounds="True">
                <Grid>
                  <Border x:Name="PART_Indicator" CornerRadius="1.5" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}"/>
                  <Border x:Name="Indeterminate" CornerRadius="1.5" Width="80" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}" Visibility="Collapsed">
                    <Border.RenderTransform><TranslateTransform x:Name="Slide" X="-80"/></Border.RenderTransform>
                  </Border>
                </Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsIndeterminate" Value="True">
                  <Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed"/>
                  <Setter TargetName="Indeterminate" Property="Visibility" Value="Visible"/>
                  <Trigger.EnterActions>
                    <BeginStoryboard x:Name="Run">
                      <Storyboard RepeatBehavior="Forever">
                        <DoubleAnimation Storyboard.TargetName="Slide" Storyboard.TargetProperty="X" From="-80" To="320" Duration="0:0:1.3">
                          <DoubleAnimation.EasingFunction><SineEase EasingMode="EaseInOut"/></DoubleAnimation.EasingFunction>
                        </DoubleAnimation>
                      </Storyboard>
                    </BeginStoryboard>
                  </Trigger.EnterActions>
                  <Trigger.ExitActions><StopStoryboard BeginStoryboardName="Run"/></Trigger.ExitActions>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    internal static void MakeDraggable(FrameworkElement el, IReadOnlyList<string> files)
    {
        Point? start = null;
        el.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(el);
        el.PreviewMouseMove += (_, e) =>
        {
            if (start == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(el) - start.Value;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            var data = new DataObject(DataFormats.FileDrop, files.ToArray());
            DragDrop.DoDragDrop(el, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
        el.PreviewMouseLeftButtonUp += (_, _) => start = null;
    }
}
