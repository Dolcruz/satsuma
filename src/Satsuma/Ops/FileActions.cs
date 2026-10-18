using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Satsuma.Ops;

/// <summary>Actions that work on any file or folder. They run on the UI thread and use native Windows UI.</summary>
public static class FileActions
{
    public static async Task Share(OpContext ctx)
    {
        var host = UI.ShareHost.Instance;
        var hwnd = host.Prepare();
        var dtm = DataTransferManagerInterop.GetForWindow(hwnd);
        var paths = ctx.Paths.ToList();

        void OnRequested(DataTransferManager sender, DataRequestedEventArgs e)
        {
            sender.DataRequested -= OnRequested;
            var deferral = e.Request.GetDeferral();
            _ = Task.Run(async () =>
            {
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var p in paths)
                        items.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p));
                    e.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} items";
                    e.Request.Data.SetStorageItems(items);
                }
                finally { deferral.Complete(); }
            });
        }

        dtm.DataRequested += OnRequested;
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        await Task.CompletedTask;
    }

    public static Task Float(OpContext ctx)
    {
        foreach (var p in ctx.Paths.Take(12)) UI.FloatWindow.Open(p);
        return Task.CompletedTask;
    }

    static string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static Task CopyTo(OpContext ctx) => Transfer(ctx, move: false);
    public static Task MoveTo(OpContext ctx) => Transfer(ctx, move: true);

    static Task Transfer(OpContext ctx, bool move)
    {
        var target = PickFolder(move ? "Move to…" : "Copy to…");
        if (target == null) return Task.CompletedTask;
        var paths = ctx.Paths.ToList();

        // The native Windows copy engine: progress dialog, conflict prompts, undo-able recycle semantics.
        var t = new Thread(() =>
        {
            foreach (var p in paths)
            {
                try
                {
                    var dest = Path.Combine(target, Path.GetFileName(p.TrimEnd('\\')));
                    if (Directory.Exists(p))
                    {
                        if (move) FileSystem.MoveDirectory(p, dest, UIOption.AllDialogs, UICancelOption.DoNothing);
                        else FileSystem.CopyDirectory(p, dest, UIOption.AllDialogs, UICancelOption.DoNothing);
                    }
                    else
                    {
                        if (move) FileSystem.MoveFile(p, dest, UIOption.AllDialogs, UICancelOption.DoNothing);
                        else FileSystem.CopyFile(p, dest, UIOption.AllDialogs, UICancelOption.DoNothing);
                    }
                }
                catch { }
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
                UI.Hud.Flash(move ? "Moved" : "Copied", paths.Count == 1 ? Path.GetFileName(paths[0]) + " → " + Path.GetFileName(target) : $"{paths.Count} items → {Path.GetFileName(target)}", target));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return Task.CompletedTask;
    }

    public static Task CopyPath(OpContext ctx)
    {
        var text = string.Join(Environment.NewLine, ctx.Paths.Select(p => "\"" + p + "\""));
        for (int i = 0; i < 5; i++)
        {
            try { System.Windows.Clipboard.SetText(text); break; }
            catch { Thread.Sleep(40); }
        }
        UI.Hud.Flash("Path copied", ctx.Paths.Count == 1 ? ctx.Paths[0] : $"{ctx.Paths.Count} paths");
        return Task.CompletedTask;
    }

    public static Task Terminal(OpContext ctx)
    {
        var first = ctx.Paths[0];
        var dir = Directory.Exists(first) ? first : Path.GetDirectoryName(first)!;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            if (File.Exists(wt))
                Process.Start(new ProcessStartInfo(wt, $"-d \"{dir}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { WorkingDirectory = dir, UseShellExecute = true });
        }
        catch (Exception ex) { UI.Hud.Flash("Couldn't open Terminal", ex.Message); }
        return Task.CompletedTask;
    }
}
