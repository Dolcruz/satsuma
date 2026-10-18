using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Satsuma.Core;

/// <summary>Reads the current file selection from the foreground Explorer window (tabs aware) or the desktop.</summary>
public static class ExplorerSelection
{
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    // IShellBrowser derives from IOleWindow; GetWindow is the first method after IUnknown.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowserWindow
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
    }

    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    public static bool IsExplorerOrDesktop(IntPtr hwnd)
    {
        var cls = Native.ClassOf(hwnd);
        return cls is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";
    }

    public static List<string> GetSelectedPaths(IntPtr foreground)
    {
        var result = new List<string>();
        try
        {
            var cls = Native.ClassOf(foreground);
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return result;
            dynamic shell = Activator.CreateInstance(shellType)!;

            if (cls is "Progman" or "WorkerW")
            {
                dynamic windows = shell.Windows();
                object loc = 0, root = Type.Missing;
                int hwnd = 0;
                // SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1
                dynamic desktop = windows.FindWindowSW(ref loc, ref root, 8, ref hwnd, 1);
                AddSelection(desktop?.Document, result);
                return result;
            }

            // Win11 Explorer tabs: the active tab is the topmost ShellTabWindowClass child.
            IntPtr activeTab = Native.FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);

            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (new IntPtr((long)w.HWND) != foreground) continue;
                    if (activeTab != IntPtr.Zero && w is IServiceProvider sp)
                    {
                        var sid = SID_STopLevelBrowser; var iid = IID_IShellBrowser;
                        if (sp.QueryService(ref sid, ref iid, out var browserObj) == 0 && browserObj is IShellBrowserWindow browser)
                        {
                            browser.GetWindow(out var tabHwnd);
                            if (tabHwnd != activeTab) continue;
                        }
                    }
                    AddSelection(w.Document, result);
                    if (result.Count > 0) break;
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    static void AddSelection(dynamic? document, List<string> into)
    {
        if (document == null) return;
        foreach (dynamic item in document.SelectedItems())
        {
            string? p = item.Path;
            if (!string.IsNullOrEmpty(p) && (System.IO.File.Exists(p) || System.IO.Directory.Exists(p)))
                into.Add(p);
        }
    }
}
