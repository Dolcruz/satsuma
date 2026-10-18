using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Satsuma.Core;

/// <summary>
/// Satsuma idles in the tray almost all the time. After a burst of work (wheel, conversion, settings),
/// compact the managed heap and hand unused pages back to Windows so the idle footprint stays small.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    static DispatcherTimer? _timer;

    /// <summary>Busy check supplied by the app (wheel open, jobs running…).</summary>
    public static Func<bool>? IsBusy { get; set; }

    /// <summary>Trims a few seconds after the last call, unless the app is busy by then.</summary>
    public static void Schedule()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _timer!.Stop();
            if (IsBusy?.Invoke() == true) { _timer.Start(); return; }
            Trim();
        }, Dispatcher.CurrentDispatcher);
        _timer.Stop();
        _timer.Start();
    }

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }
}
