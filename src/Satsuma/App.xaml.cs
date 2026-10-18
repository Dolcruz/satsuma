using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Satsuma.Core;
using Satsuma.Ops;
using Satsuma.UI;
using Wpf.Ui.Appearance;

namespace Satsuma;

public partial class App : Application
{
    public const string RepoUrl = "https://github.com/Dolcruz/satsuma";

    public static App Instance => (App)Current;

    bool _fluentLoaded;

    /// <summary>Loads the WPF-UI (Fluent) resources the first time a Fluent surface is shown.</summary>
    public static void EnsureFluent()
    {
        var app = Instance;
        if (app._fluentLoaded) return;
        app._fluentLoaded = true;
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        ApplicationThemeManager.ApplySystemTheme();
    }

    /// <summary>Never-shown placeholder that holds Application.MainWindow while Settings is closed.</summary>
    public Window Anchor => _anchor ??= new Window { Title = "Satsuma", ShowInTaskbar = false, Width = 0, Height = 0 };
    Window? _anchor;

    SingleInstance? _single;
    InputHooks? _hooks;
    WheelWindow? _wheel;
    Tray? _tray;
    HwndSource? _messageWindow;

    const int HotkeyId = 0x5A75;
    readonly List<string> _pendingWheelFiles = new();
    DispatcherTimer? _pendingTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        if (args.Contains("--cleanup"))
        {
            // Called by the uninstaller: remove our per-user registry entries.
            Integration.RemoveAll();
            Shutdown();
            return;
        }

        if (args.Length >= 2 && args[0] == "--selftest")
        {
            Settings.Load();
            Dispatcher.InvokeAsync(async () =>
            {
                int failures;
                try { failures = await SelfTest.Run(args[1]); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "selftest.txt"), ex.ToString()); failures = 99; }
                Shutdown(failures);
            });
            return;
        }

        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            SingleInstance.Send(args.Length == 0 ? new[] { "--show" } : args);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log(ex.Exception); ex.SetObserved(); };

        Settings.Load();
        Integration.Apply(Settings.Current);

        _wheel = new WheelWindow();
        _wheel.ItemChosen += (item, paths) => _ = RunItem(item, paths);
        // WPF-UI themes Application.MainWindow (Mica frame etc.). The wheel must never be that window.
        MainWindow = Anchor;

        _hooks = new InputHooks();
        _hooks.ModifierTapped += OnModifierTapped;
        _hooks.LeftButtonReleased += () => _wheel.OnDragGestureEnded();
        _hooks.EscapePressed += () => { if (_wheel.IsOpen && _wheel.IsDragMode) _wheel.CloseWheel(); };
        _hooks.Install();

        _tray = new Tray();
        CreateMessageWindow();
        Settings.Changed += RegisterHotkey;

        _single.ArgsReceived += a => Dispatcher.BeginInvoke(() => HandleArgs(a));
        _single.StartListening();

        HandleArgs(args);

        MemoryTrim.IsBusy = () => _wheel.IsVisible || Hud.IsBusy;
        MemoryTrim.Schedule();

        if (!Settings.Current.FirstRunDone)
        {
            Settings.Current.FirstRunDone = true;
            Settings.Save();
            SettingsWindow.ShowSingle();
            _tray.Balloon("Satsuma is ready", "Drag any file and tap Shift to convert it. Satsuma lives here in the tray.");
        }
        else if (args.Length == 0)
        {
            SettingsWindow.ShowSingle();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hooks?.Dispose();
        _tray?.Dispose();
        if (_messageWindow != null) Native.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
        _single?.Dispose();
        base.OnExit(e);
    }

    // ---------------- Gesture ----------------

    void OnModifierTapped(TriggerKey key)
    {
        if (_tray?.Paused == true || _wheel == null) return;
        var mode = Settings.Current.ModeFor(key);
        if (mode == null) return;
        bool dragging = _hooks!.IsDragging && Native.IsLeftButtonDown();
        // Return from the hook immediately; do the work on the next dispatcher turn.
        Dispatcher.BeginInvoke(() =>
        {
            if (_wheel.IsOpen) _wheel.SwitchMode(mode.Value);
            else if (dragging) _wheel.ArmForDrag(mode.Value);
        });
    }

    // ---------------- Shortcut (Ctrl+Shift+Space) ----------------

    void CreateMessageWindow()
    {
        var p = new HwndSourceParameters("SatsumaMessages") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 };
        _messageWindow = new HwndSource(p);
        _messageWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                OpenForExplorerSelection();
            }
            return IntPtr.Zero;
        });
        RegisterHotkey();
    }

    void RegisterHotkey()
    {
        if (_messageWindow == null) return;
        Native.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
        if (Settings.Current.KeyboardShortcutEnabled)
        {
            const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_SPACE = 0x20;
            Native.RegisterHotKey(_messageWindow.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE);
        }
    }

    public void OpenForExplorerSelection()
    {
        if (_wheel == null || _tray?.Paused == true) return;
        var fg = Native.GetForegroundWindow();
        if (!ExplorerSelection.IsExplorerOrDesktop(fg))
        {
            Hud.Flash("Select files in Explorer first", "Then press Ctrl + Shift + Space to open the wheel.");
            return;
        }
        var paths = ExplorerSelection.GetSelectedPaths(fg);
        if (paths.Count == 0)
        {
            Hud.Flash("Nothing selected", "Select one or more files in Explorer, then try again.");
            return;
        }
        _wheel.OpenFor(paths, WheelMode.Convert, keyboard: true);
    }

    // ---------------- Command line / context menu ----------------

    void HandleArgs(string[] args)
    {
        if (args.Length == 0) return;
        if (args.Contains("--show")) { SettingsWindow.ShowSingle(); return; }

        int i = Array.IndexOf(args, "--wheel");
        if (i < 0) return;
        foreach (var p in args.Skip(i + 1))
            if (File.Exists(p) || Directory.Exists(p)) _pendingWheelFiles.Add(p);

        // Explorer starts one process per selected file; gather them for a moment into one wheel.
        _pendingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
        {
            _pendingTimer!.Stop();
            if (_pendingWheelFiles.Count == 0) return;
            var files = _pendingWheelFiles.ToList();
            _pendingWheelFiles.Clear();
            _wheel?.OpenFor(files, WheelMode.Convert, keyboard: false);
        }, Dispatcher);
        _pendingTimer.Stop();
        _pendingTimer.Start();
    }

    // ---------------- Running ----------------

    sealed class Relay<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    async Task RunItem(WheelItem item, IReadOnlyList<string> paths)
    {
        if (item.Instant)
        {
            try { await item.Run(new OpContext(paths, null, null, default)); }
            catch (Exception ex) { Log(ex); Hud.Flash("Couldn't do that", ex.Message); }
            return;
        }

        var subtitle = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} files";
        var job = Hud.Start(item.Verb ?? item.Label, subtitle, paths[0]);
        var ctx = new OpContext(paths, new Relay<double>(job.Progress), new Relay<string>(job.Status), job.Token);
        try
        {
            if (item.NeedsFfmpeg && !FFmpeg.IsAvailable)
            {
                await Task.Run(() => FFmpeg.EnsureAsync(ctx, null, job.Token));
                job.Status(subtitle);
            }
            await Task.Run(() => item.Run(ctx), job.Token);
            job.Complete(ctx.Outputs);
        }
        catch (OperationCanceledException)
        {
            job.Fail("Cancelled");
        }
        catch (Exception ex)
        {
            Log(ex);
            job.Fail(Friendly(ex));
        }
    }

    static string Friendly(Exception ex)
    {
        var root = ex is AggregateException ag ? ag.Flatten().InnerException ?? ex : ex;
        return root switch
        {
            UnauthorizedAccessException => "Satsuma isn't allowed to write there.",
            IOException io when io.Message.Contains("being used") => "The file is open in another app.",
            ImageMagick.MagickException m => "This image couldn't be read: " + m.Message.Split('@')[0].Trim(),
            _ => root.Message,
        };
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "log.txt"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
