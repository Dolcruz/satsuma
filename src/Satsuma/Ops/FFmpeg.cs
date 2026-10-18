using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Satsuma.Ops;

/// <summary>Finds FFmpeg (bundled, PATH, or a one-time download into %LOCALAPPDATA%) and runs it with progress.</summary>
public static class FFmpeg
{
    const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    static readonly string LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Satsuma", "ffmpeg");
    static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public static string? Path_ { get; private set; }

    public static string? Find()
    {
        if (Path_ != null && File.Exists(Path_)) return Path_;
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            System.IO.Path.Combine(LocalDir, "ffmpeg.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path_ = c;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = System.IO.Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(c)) return Path_ = c;
            }
            catch { }
        }
        return null;
    }

    public static bool IsAvailable => Find() != null;

    /// <summary>Makes sure FFmpeg exists, downloading the official essentials build once if needed.</summary>
    public static async Task<string> EnsureAsync(OpContext? ctx, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var found = Find();
        if (found != null) return found;

        await DownloadLock.WaitAsync(ct);
        try
        {
            found = Find();
            if (found != null) return found;

            ctx?.Status("Getting FFmpeg for video & audio (one-time, ~100 MB)…");
            Directory.CreateDirectory(LocalDir);
            var zipPath = System.IO.Path.Combine(LocalDir, "ffmpeg-download.zip");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Satsuma/1.0 (+https://github.com)");
                using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                    {
                        progress?.Report((double)read / total);
                        ctx?.Progress((double)read / total);
                    }
                }
            }

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase));
                    entry?.ExtractToFile(System.IO.Path.Combine(LocalDir, name), overwrite: true);
                }
            }
            File.Delete(zipPath);
            ctx?.Progress(0);
            return Find() ?? throw new InvalidOperationException("FFmpeg could not be installed.");
        }
        finally { DownloadLock.Release(); }
    }

    static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>Runs ffmpeg with the given arguments (input/output included) and maps its progress to 0..1.</summary>
    public static async Task Run(string arguments, IProgress<double>? progress, CancellationToken ct)
    {
        var exe = await EnsureAsync(null, null, ct);
        var psi = new ProcessStartInfo(exe, "-hide_banner -nostdin -y -nostats -progress pipe:1 " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        double durationUs = 0;
        var errTail = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (durationUs == 0)
            {
                var m = DurationRx.Match(e.Data);
                if (m.Success)
                    durationUs = (int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                                  double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) * 1_000_000;
            }
            lock (errTail)
            {
                errTail.AppendLine(e.Data);
                if (errTail.Length > 4000) errTail.Remove(0, errTail.Length - 4000);
            }
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || durationUs <= 0) return;
            if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
            {
                if (long.TryParse(e.Data.AsSpan(e.Data.IndexOf('=') + 1), out var us) && us > 0)
                    progress?.Report(us / durationUs);
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using (ct.Register(() => { try { proc.Kill(true); } catch { } }))
            await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            string tail;
            lock (errTail) tail = errTail.ToString();
            var lastLine = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "unknown error";
            throw new InvalidOperationException("FFmpeg: " + lastLine);
        }
        progress?.Report(1);
    }

    public static string Q(string path) => "\"" + path + "\"";
}
