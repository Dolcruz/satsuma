using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Satsuma.Core;
using Satsuma.Ops;

namespace Satsuma.UI;

/// <summary>
/// The floating wheel. Two ways in:
///  • Drag mode — summoned mid-drag; it's an OLE drop target and the drop picks the slice.
///  • Pointer/keyboard mode — summoned by shortcut or context menu for already-known files.
/// </summary>
public sealed class WheelWindow : Window
{
    readonly WheelControl _wheel = new();
    readonly DispatcherTimer _armTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    IntPtr _hwnd;
    List<string> _paths = new();
    bool _dragMode;
    bool _gotDrag;
    bool _closing;
    Native.POINT _anchor;

    public event Action<WheelItem, IReadOnlyList<string>>? ItemChosen;

    public bool IsOpen => IsVisible && !_closing;
    public bool IsDragMode => _dragMode;
    public WheelMode Mode => _wheel.Mode;

    public WheelWindow()
    {
        // Opt out of WPF-UI's implicit window style: it paints its own opaque background.
        Style = new Style(typeof(Window));
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Satsuma";
        Content = _wheel;
        Focusable = true;

        _armTimer.Tick += (_, _) =>
        {
            _armTimer.Stop();
            // Nothing was dragged onto us: the gesture wasn't a file drag. Vanish without a trace.
            if (_dragMode && !_gotDrag) CloseWheel(instant: true);
        };
        _wheel.Hidden += () =>
        {
            if (_closing) { Hide(); _closing = false; MemoryTrim.Schedule(); }
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd, noActivate: true);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionAtAnchor, DispatcherPriority.Render);
        Deactivated += (_, _) => { if (!_dragMode && IsOpen) CloseWheel(); };

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, e) => { _wheel.ClearPointer(); e.Handled = true; };
        Drop += OnDrop;

        MouseMove += (_, e) => { if (!_dragMode) _wheel.SetPointer(e.GetPosition(_wheel)); };
        MouseLeave += (_, _) => { if (!_dragMode) _wheel.ClearPointer(); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_dragMode) return;
            var (index, _) = _wheel.HitTestWheel(e.GetPosition(_wheel));
            if (index >= 0) Choose(index); else CloseWheel();
        };
        MouseRightButtonUp += (_, _) => CloseWheel();
        PreviewKeyDown += OnKeyDown;
        // Alt is a wheel switcher here — don't let it arm the (invisible) system menu.
        PreviewKeyUp += (_, e) => { if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) e.Handled = true; };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // WPF-UI's theme manager repaints the background of every app window when the theme is applied.
        // The wheel must stay see-through, so undo that immediately.
        if (e.Property == BackgroundProperty && e.NewValue is not SolidColorBrush { Color.A: 0 })
            Background = Brushes.Transparent;
    }

    // ---------------- Opening ----------------

    /// <summary>Called mid-drag. Starts invisible; becomes visible once Windows tells us files are being dragged over it.</summary>
    public void ArmForDrag(WheelMode mode)
    {
        if (IsOpen && _dragMode) { SwitchMode(mode); return; }
        SystemTheme.Refresh();
        _dragMode = true;
        _gotDrag = false;
        _closing = false;
        _paths = new();
        _wheel.KeyboardMode = false;
        _wheel.HideInstant();
        _wheel.SetItems(mode, new(), animate: false);
        Native.GetCursorPos(out _anchor);
        ShowAt(activate: false);
        _armTimer.Stop();
        _armTimer.Start();
    }

    /// <summary>Opens the wheel for files we already know (shortcut, context menu).</summary>
    public void OpenFor(IReadOnlyList<string> paths, WheelMode mode, bool keyboard)
    {
        SystemTheme.Refresh();
        _dragMode = false;
        _closing = false;
        _armTimer.Stop();
        SetPaths(paths);
        _wheel.KeyboardMode = keyboard;
        _wheel.SetItems(mode, Catalog.For(mode, _paths), animate: false);
        UpdateEmptyText();
        Native.GetCursorPos(out _anchor);
        ShowAt(activate: true);
        _wheel.ClearPointer();
        _wheel.Show();
    }

    /// <summary>
    /// WPF-UI re-themes every window of the app (render background + DWM backdrop) whenever its theme is applied.
    /// Undo that for the wheel so the layered window stays see-through.
    /// </summary>
    void KeepTransparent()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (HwndSource.FromHwnd(_hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        int none = 1; // DWMSBT_NONE
        Native.DwmSetWindowAttribute(_hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref none, sizeof(int));
        var zero = new Native.MARGINS();
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref zero);
        Background = Brushes.Transparent;
    }

    void ShowAt(bool activate)
    {
        new WindowInteropHelper(this).EnsureHandle();
        KeepTransparent();
        if (!IsVisible)
        {
            new WindowInteropHelper(this).EnsureHandle();
            Native.MakeToolWindow(_hwnd, noActivate: !activate);
            PositionAtAnchor();
            Show();
        }
        else Native.MakeToolWindow(_hwnd, noActivate: !activate);
        PositionAtAnchor();
        if (activate)
        {
            Native.ForceForeground(_hwnd);
            Activate();
            Focus();
            Keyboard.Focus(this);
        }
    }

    void PositionAtAnchor()
    {
        if (_hwnd == IntPtr.Zero) return;
        var (work, scale) = Native.MonitorAt(_anchor);
        int w = (int)Math.Round(WheelControl.Size * scale);
        int h = (int)Math.Round((WheelControl.Size + 76) * scale);
        int x = _anchor.X - (int)Math.Round(WheelControl.Cx * scale);
        int y = _anchor.Y - (int)Math.Round(WheelControl.Cy * scale);
        x = Math.Clamp(x, work.Left - (int)(30 * scale), Math.Max(work.Left, work.Right - w + (int)(30 * scale)));
        y = Math.Clamp(y, work.Top - (int)(20 * scale), Math.Max(work.Top, work.Bottom - h));
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void SetPaths(IReadOnlyList<string> paths)
    {
        _paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var first = _paths.FirstOrDefault();
        _wheel.Caption = first == null ? "" : Path.GetFileName(first.TrimEnd('\\')) is { Length: > 0 } n ? n : first;
        _wheel.SubCaption = _paths.Count > 1 ? $"+{_paths.Count - 1} more" : null;
        var kinds = _paths.Select(FileKinds.Of).Distinct().ToList();
        _wheel.HubGlyph = kinds.Count == 1 ? FileKinds.Glyph(kinds[0]) : "";
        var exts = _paths.Select(p => Directory.Exists(p) ? "Folder" : FileKinds.Ext(p).ToUpperInvariant()).Distinct().ToList();
        _wheel.HubLabel = _paths.Count == 0 ? "" : exts.Count == 1 && exts[0].Length > 0
            ? (_paths.Count > 1 ? $"{_paths.Count} × {exts[0]}" : exts[0])
            : $"{_paths.Count} items";
    }

    void UpdateEmptyText()
    {
        _wheel.EmptyText = _wheel.Mode switch
        {
            WheelMode.Convert => _paths.Any(Directory.Exists) ? "Folders can't be converted — try Tools" : "No conversions for this file type",
            WheelMode.Tools => "No tools for this selection",
            _ => null,
        };
    }

    public void SwitchMode(WheelMode mode)
    {
        if (!IsOpen || mode == _wheel.Mode) return;
        _wheel.SetItems(mode, Catalog.For(mode, _paths), animate: true);
        UpdateEmptyText();
        if (_dragMode && Native.GetCursorPos(out var p))
            _wheel.SetPointer(_wheel.PointFromScreen(new Point(p.X, p.Y)));
    }

    // ---------------- OLE drag & drop ----------------

    void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_dragMode) { e.Effects = DragDropEffects.None; return; }
        if (!_gotDrag)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            {
                e.Effects = DragDropEffects.None;
                CloseWheel(instant: true);
                return;
            }
            _gotDrag = true;
            _armTimer.Stop();
            SetPaths(files);
            _wheel.SetItems(_wheel.Mode, Catalog.For(_wheel.Mode, _paths), animate: false);
            UpdateEmptyText();
            _wheel.Show();
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _wheel.SetPointer(e.GetPosition(_wheel));
        // Always report Copy so the drag source never deletes the original (no "move" semantics).
        e.Effects = _wheel.HotIndex >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _wheel.SetPointer(e.GetPosition(_wheel));
        int index = _wheel.HotIndex;
        e.Effects = index >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (index >= 0)
        {
            // Let the drag source finish its modal loop before we start work.
            Dispatcher.BeginInvoke(() => Choose(index), DispatcherPriority.Background);
        }
        else CloseWheel();
    }

    /// <summary>The mouse button went up somewhere — if no drop landed on us, close.</summary>
    public void OnDragGestureEnded()
    {
        if (!_dragMode || !IsOpen) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_dragMode && IsOpen) CloseWheel();
        }, DispatcherPriority.Background);
    }

    // ---------------- Keyboard ----------------

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape: CloseWheel(); break;
            case Key.Right: case Key.Down: case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                _wheel.MoveSelection(1); break;
            case Key.Left: case Key.Up:
                _wheel.MoveSelection(-1); break;
            case Key.Tab: _wheel.MoveSelection(-1); break;
            case Key.Enter: case Key.Space:
                if (_wheel.HotIndex >= 0) Choose(_wheel.HotIndex);
                break;
            case >= Key.D1 and <= Key.D9:
                Choose(key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                Choose(key - Key.NumPad1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------- Closing ----------------

    void Choose(int index)
    {
        if (index < 0 || index >= _wheel.Items.Count) return;
        var item = _wheel.Items[index];
        var paths = _paths.ToList();
        _wheel.Select(index);
        CloseWheel();
        ItemChosen?.Invoke(item, paths);
    }

    public void CloseWheel(bool instant = false)
    {
        _armTimer.Stop();
        if (!IsVisible) return;
        if (instant || !_wheel.IsShown)
        {
            _wheel.HideInstant();
            _closing = false;
            Hide();
            return;
        }
        _closing = true;
        _wheel.Hide();
    }
}
