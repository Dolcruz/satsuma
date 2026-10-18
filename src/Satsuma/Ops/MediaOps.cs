using System;
using System.IO;
using System.Threading.Tasks;
using static Satsuma.Ops.FFmpeg;

namespace Satsuma.Ops;

public static class MediaOps
{
    public static readonly string[] VideoTargets = { "MP4", "MOV", "MKV", "WEBM", "AVI", "WMV", "GIF", "MP3", "M4A", "WAV" };
    public static readonly string[] AudioTargets = { "MP3", "M4A", "WAV", "FLAC", "OGG", "OPUS", "AIFF", "WMA" };

    static string H264(int crf) => $"-c:v libx264 -crf {crf} -preset medium -pix_fmt yuv420p -c:a aac -b:a 192k";

    static string ArgsFor(string target, OpContext ctx) => target.ToUpperInvariant() switch
    {
        "MP4" => H264(ctx.Settings.VideoCrf) + " -movflags +faststart",
        "MOV" => H264(ctx.Settings.VideoCrf) + " -movflags +faststart",
        "MKV" => H264(ctx.Settings.VideoCrf),
        "WEBM" => "-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -deadline good -cpu-used 4 -c:a libopus -b:a 128k",
        "AVI" => "-c:v mpeg4 -q:v 3 -c:a libmp3lame -q:a 3",
        "WMV" => "-c:v wmv2 -b:v 4M -c:a wmav2 -b:a 192k",
        "GIF" => "-an -vf \"fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4\" -loop 0",
        "MP3" => "-vn -c:a libmp3lame -q:a 2",
        "M4A" => "-vn -c:a aac -b:a 256k",
        "WAV" => "-vn -c:a pcm_s16le",
        "FLAC" => "-vn -c:a flac",
        "OGG" => "-vn -c:a libvorbis -q:a 6",
        "OPUS" => "-vn -c:a libopus -b:a 160k",
        "AIFF" => "-vn -c:a pcm_s16be",
        "WMA" => "-vn -c:a wmav2 -b:a 192k",
        _ => throw new NotSupportedException(target),
    };

    static async Task Ffmpeg(string input, string output, string args, IProgress<double> p, OpContext ctx, string preInput = "")
    {
        try
        {
            await Run($"{preInput} -i {Q(input)} {args} {Q(output)}", p, ctx.Token);
            ctx.Outputs.Add(output);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
    }

    public static Task Convert(string path, string target, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, target.ToLowerInvariant()), ArgsFor(target, ctx), p, ctx);

    // ---- Video tools ----

    public static Task CompressVideo(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, "mp4", " (compressed)"),
            "-vf \"scale='min(1920,iw)':-2\" -c:v libx264 -crf 28 -preset medium -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart", p, ctx);

    public static Task To720p(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, "mp4", " (720p)"),
            "-vf \"scale=-2:'min(720,ih)'\" -c:v libx264 -crf 23 -preset medium -pix_fmt yuv420p -c:a aac -b:a 160k -movflags +faststart", p, ctx);

    public static Task Mute(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, FileKinds.Ext(path), " (muted)"), "-map 0:v -c copy -an", p, ctx);

    public static Task RotateVideo(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, "mp4", " (rotated)"),
            "-vf transpose=1 -c:v libx264 -crf 20 -preset medium -pix_fmt yuv420p -c:a copy", p, ctx);

    public static Task DoubleSpeed(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, "mp4", " (2x)"),
            "-filter_complex \"[0:v]setpts=0.5*PTS[v];[0:a]atempo=2.0[a]\" -map \"[v]\" -map \"[a]?\" -c:v libx264 -crf 23 -preset medium -pix_fmt yuv420p -c:a aac -b:a 160k", p, ctx);

    public static Task Snapshot(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, "png", " (frame)"), "-frames:v 1 -update 1", p, ctx, preInput: "-ss 1");

    // ---- Audio tools ----

    public static Task CompressAudio(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, "mp3", " (compressed)"), "-vn -c:a libmp3lame -b:a 128k", p, ctx);

    static string SameAudioCodec(string path) => FileKinds.Ext(path) switch
    {
        "mp3" => "-c:a libmp3lame -q:a 2",
        "m4a" or "aac" => "-c:a aac -b:a 256k",
        "flac" => "-c:a flac",
        "ogg" or "oga" => "-c:a libvorbis -q:a 6",
        "opus" => "-c:a libopus -b:a 160k",
        "aiff" or "aif" => "-c:a pcm_s16be",
        "wma" => "-c:a wmav2 -b:a 192k",
        _ => "-c:a pcm_s16le",
    };

    static string SameAudioExt(string path) => FileKinds.Ext(path) is "amr" ? "wav" : FileKinds.Ext(path);

    public static Task Normalize(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, SameAudioExt(path), " (normalized)"), "-vn -af loudnorm=I=-16:TP=-1.5:LRA=11 -ar 48000 " + SameAudioCodec(path), p, ctx);

    public static Task Mono(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, SameAudioExt(path), " (mono)"), "-vn -ac 1 " + SameAudioCodec(path), p, ctx);

    public static Task TrimSilence(string path, IProgress<double> p, OpContext ctx) =>
        Ffmpeg(path, Output.For(path, SameAudioExt(path), " (trimmed)"),
            "-vn -af \"silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse\" " + SameAudioCodec(path), p, ctx);
}
