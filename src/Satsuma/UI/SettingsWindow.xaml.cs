using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Satsuma.Core;
using Satsuma.Ops;
using Wpf.Ui.Appearance;

namespace Satsuma.UI;

public partial class SettingsWindow
{
    static SettingsWindow? _open;
    bool _loading;

    public static void ShowSingle()
    {
        if (_open == null)
        {
            App.EnsureFluent();
            _open = new SettingsWindow();
            Application.Current.MainWindow = _open;
            _open.Closed += (_, _) => { _open = null; Application.Current.MainWindow = App.Instance.Anchor; MemoryTrim.Schedule(); };
            _open.Show();
        }
        if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
        _open.Activate();
    }

    static readonly (string label, TriggerKey key)[] Keys =
    {
        ("Shift", TriggerKey.Shift), ("Ctrl", TriggerKey.Ctrl), ("Alt", TriggerKey.Alt), ("Off", TriggerKey.None),
    };

    public SettingsWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Load();
        Hook();
    }

    void Load()
    {
        _loading = true;
        var s = Settings.Current;
        foreach (var combo in new[] { ConvertKey, ToolsKey, ActionsKey })
        {
            combo.Items.Clear();
            foreach (var (label, _) in Keys) combo.Items.Add(label);
        }
        ConvertKey.SelectedIndex = Array.FindIndex(Keys, k => k.key == s.ConvertKey);
        ToolsKey.SelectedIndex = Array.FindIndex(Keys, k => k.key == s.ToolsKey);
        ActionsKey.SelectedIndex = Array.FindIndex(Keys, k => k.key == s.ActionsKey);

        ActiveSwitch.IsChecked = !s.Paused;
        UpdateState();
        Shortcut.IsChecked = s.KeyboardShortcutEnabled;
        ContextMenuSwitch.IsChecked = s.ExplorerContextMenu;
        Reveal.IsChecked = s.RevealResultInExplorer;
        Startup.IsChecked = s.LaunchAtStartup;
        Quality.Value = s.JpegQuality;

        PdfDpi.Items.Clear();
        foreach (var d in new[] { 150, 200, 300 }) PdfDpi.Items.Add($"{d} DPI");
        PdfDpi.SelectedIndex = s.PdfRenderDpi switch { <= 150 => 0, >= 300 => 2, _ => 1 };

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Satsuma {v?.Major}.{v?.Minor}.{v?.Build}";
        UpdateLabels();
        UpdateFfmpeg();
        _loading = false;
    }

    void UpdateState()
    {
        bool on = !Settings.Current.Paused;
        StateTitle.Text = on ? "Satsuma is on" : "Satsuma is paused";
        StateText.Text = on
            ? "Drag a file and tap a key anytime. Satsuma starts with Windows and waits in the tray."
            : "Gestures and the keyboard shortcut are off until you switch Satsuma back on.";
        StateIcon.Symbol = on ? Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24 : Wpf.Ui.Controls.SymbolRegular.PauseCircle24;
        StateIcon.Foreground = on
            ? (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush")
            : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
    }

    void UpdateLabels()
    {
        var s = Settings.Current;
        string K(TriggerKey k) => k == TriggerKey.None ? "" : k.ToString();
        var parts = new[]
        {
            s.ConvertKey != TriggerKey.None ? $"{K(s.ConvertKey)} → Convert" : null,
            s.ToolsKey != TriggerKey.None ? $"{K(s.ToolsKey)} → Tools" : null,
            s.ActionsKey != TriggerKey.None ? $"{K(s.ActionsKey)} → Actions" : null,
        }.Where(p => p != null);
        HowKeys.Text = string.Join("\n", parts) + "\nTap another key to switch.";
        QualityLabel.Text = $"{(int)Quality.Value}% for JPG, WebP, AVIF and HEIC exports";
    }

    void UpdateFfmpeg()
    {
        var path = FFmpeg.Find();
        if (path != null)
        {
            FfmpegStatus.Text = "Ready · " + path;
            FfmpegButton.Content = "Installed";
            FfmpegButton.IsEnabled = false;
            FfmpegButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        }
        else
        {
            FfmpegStatus.Text = "Needed for video and audio. Downloaded once (~100 MB) from gyan.dev, or automatically on first use.";
            FfmpegButton.Content = "Download";
            FfmpegButton.IsEnabled = true;
        }
    }

    void Hook()
    {
        SelectionChangedEventHandler keyChanged = (_, _) =>
        {
            if (_loading) return;
            var s = Settings.Current;
            var picks = new[] { ConvertKey, ToolsKey, ActionsKey }.Select(c => c.SelectedIndex < 0 ? TriggerKey.None : Keys[c.SelectedIndex].key).ToArray();
            // Keep keys unique: if a key was taken by another wheel, that wheel gets the one that was freed.
            var old = new[] { s.ConvertKey, s.ToolsKey, s.ActionsKey };
            for (int i = 0; i < 3; i++)
            {
                if (picks[i] == old[i] || picks[i] == TriggerKey.None) continue;
                for (int j = 0; j < 3; j++)
                    if (j != i && picks[j] == picks[i]) picks[j] = old[i];
            }
            (s.ConvertKey, s.ToolsKey, s.ActionsKey) = (picks[0], picks[1], picks[2]);
            Settings.Save();
            Load();
        };
        ConvertKey.SelectionChanged += keyChanged;
        ToolsKey.SelectionChanged += keyChanged;
        ActionsKey.SelectionChanged += keyChanged;

        ActiveSwitch.Click += (_, _) => Settings.SetPaused(ActiveSwitch.IsChecked != true);
        // Keep in sync when pausing from the tray menu while this window is open.
        Action onChanged = () => Dispatcher.BeginInvoke(() =>
        {
            if (ActiveSwitch.IsChecked == Settings.Current.Paused) ActiveSwitch.IsChecked = !Settings.Current.Paused;
            UpdateState();
        });
        Settings.Changed += onChanged;
        Closed += (_, _) => Settings.Changed -= onChanged;

        Shortcut.Click += (_, _) => { Settings.Current.KeyboardShortcutEnabled = Shortcut.IsChecked == true; Settings.Save(); };
        ContextMenuSwitch.Click += (_, _) =>
        {
            Settings.Current.ExplorerContextMenu = ContextMenuSwitch.IsChecked == true;
            Settings.Save();
            Integration.SetContextMenu(Settings.Current.ExplorerContextMenu);
        };
        Reveal.Click += (_, _) => { Settings.Current.RevealResultInExplorer = Reveal.IsChecked == true; Settings.Save(); };
        Startup.Click += (_, _) =>
        {
            Settings.Current.LaunchAtStartup = Startup.IsChecked == true;
            Settings.Save();
            Integration.SetStartup(Settings.Current.LaunchAtStartup);
        };
        Quality.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            Settings.Current.JpegQuality = (int)Quality.Value;
            Settings.Save();
            UpdateLabels();
        };
        PdfDpi.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Settings.Current.PdfRenderDpi = new[] { 150, 200, 300 }[Math.Max(0, PdfDpi.SelectedIndex)];
            Settings.Save();
        };

        FfmpegButton.Click += async (_, _) =>
        {
            FfmpegButton.IsEnabled = false;
            var progress = new Progress<double>(f => FfmpegButton.Content = $"{f:P0}");
            try
            {
                FfmpegStatus.Text = "Downloading…";
                await System.Threading.Tasks.Task.Run(() => FFmpeg.EnsureAsync(null, progress));
            }
            catch (Exception ex) { FfmpegStatus.Text = "Download failed: " + ex.Message; FfmpegButton.IsEnabled = true; FfmpegButton.Content = "Retry"; return; }
            UpdateFfmpeg();
        };

        GitHubButton.Click += (_, _) => Process.Start(new ProcessStartInfo(App.RepoUrl) { UseShellExecute = true });
        QuitButton.Click += (_, _) => Application.Current.Shutdown();
    }
}
