using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Satsuma.Ops;

namespace Satsuma.Core;

/// <summary>Checks real 2x conversions with and without an audio track.</summary>
public static class MediaSelfTest
{
    public static async Task<int> Run(string dir, StringBuilder report)
    {
        var ffmpeg = FFmpeg.Find();
        var probe = ffmpeg == null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (probe == null || !File.Exists(probe))
        {
            report.AppendLine("skip  2x speed (FFmpeg and ffprobe must be installed together)");
            return 0;
        }

        Directory.CreateDirectory(dir);
        int failures = 0;
        foreach (var audio in new[] { false, true })
        {
            var name = audio ? "with-audio" : "silent";
            try
            {
                var input = Path.Combine(dir, name + ".mp4");
                var sound = audio ? "-f lavfi -i sine=frequency=440:duration=4 -c:a aac -shortest" : "";
                await FFmpeg.Run($"-f lavfi -i testsrc=size=320x240:rate=25:duration=4 {sound} -c:v libx264 -pix_fmt yuv420p {FFmpeg.Q(input)}", null, default);
                var ctx = new OpContext(new[] { input }, null, null, default);
                await MediaOps.DoubleSpeed(input, new Progress<double>(), ctx);
                var output = ctx.Outputs.Single();
                using var info = await Probe(probe, output);
                var streams = info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                if (streams.Count(s => s.GetProperty("codec_type").GetString() == "video") != 1 ||
                    streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") != (audio ? 1 : 0))
                    throw new InvalidOperationException("Output has unexpected video/audio tracks.");
                var duration = double.Parse(info.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                // 4 seconds should become 2; allow three 25-fps frames for rounding and codec padding.
                if (Math.Abs(duration - 2) > 3.0 / 25)
                    throw new InvalidOperationException($"Expected about 2 seconds, got {duration}.");
                await FFmpeg.Run($"-v error -i {FFmpeg.Q(output)} -f null -", null, default);
                report.AppendLine($"ok    2x {name,-12} {duration:F3} s, {(audio ? 1 : 0)} audio track(s), decodable");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"FAIL  2x {name,-12} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
        return failures;
    }

    static async Task<JsonDocument> Probe(string executable, string path)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", path })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("ffprobe: " + await stderr);
        return JsonDocument.Parse(await stdout);
    }
}
