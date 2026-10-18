using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Satsuma.Core;

/// <summary>Per-user Windows integration: autostart and the Explorer context menu entry. No admin rights needed.</summary>
public static class Integration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "Satsuma";

    static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static void Apply(Settings s)
    {
        SetStartup(s.LaunchAtStartup);
        SetContextMenu(s.ExplorerContextMenu);
    }

    public static void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(AppName, $"\"{ExePath}\" --background");
            else key.DeleteValue(AppName, false);
        }
        catch { }
    }

    static readonly string[] MenuRoots =
    {
        @"Software\Classes\*\shell\Satsuma",
        @"Software\Classes\Directory\shell\Satsuma",
    };

    public static void SetContextMenu(bool enabled)
    {
        foreach (var root in MenuRoots)
        {
            try
            {
                if (!enabled) { Registry.CurrentUser.DeleteSubKeyTree(root, false); continue; }
                using var key = Registry.CurrentUser.CreateSubKey(root);
                key.SetValue("", "Convert with Satsuma");
                key.SetValue("Icon", $"\"{ExePath}\",0");
                // Explorer launches one process per selected item; the running instance merges them into one wheel.
                key.SetValue("MultiSelectModel", "Player");
                using var cmd = key.CreateSubKey("command");
                cmd.SetValue("", $"\"{ExePath}\" --wheel \"%1\"");
            }
            catch { }
        }
    }

    public static void RemoveAll()
    {
        SetStartup(false);
        SetContextMenu(false);
    }
}
