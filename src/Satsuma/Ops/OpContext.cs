using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Satsuma.Ops;

/// <summary>One slice of the wheel.</summary>
public sealed class WheelItem
{
    public required string Id { get; init; }
    /// <summary>Short label drawn on the wheel ("PNG", "Compress").</summary>
    public required string Label { get; init; }
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public required string Glyph { get; init; }
    /// <summary>Text shown while the job runs ("Converting to PNG").</summary>
    public string? Verb { get; init; }
    /// <summary>Runs on the UI thread without a progress card (file actions).</summary>
    public bool Instant { get; init; }
    /// <summary>Needs FFmpeg.</summary>
    public bool NeedsFfmpeg { get; init; }
    public required Func<OpContext, Task> Run { get; init; }
}

public sealed class OpContext
{
    public OpContext(IReadOnlyList<string> paths, IProgress<double>? progress, IProgress<string>? status, CancellationToken ct)
    {
        Paths = paths;
        _progress = progress;
        _status = status;
        Token = ct;
    }

    readonly IProgress<double>? _progress;
    readonly IProgress<string>? _status;

    public IReadOnlyList<string> Paths { get; }
    public CancellationToken Token { get; }
    public List<string> Outputs { get; private set; } = new();

    /// <summary>Makes a nested context write its results into the parent's output list.</summary>
    public OpContext With(OpContext parent)
    {
        Outputs = parent.Outputs;
        return this;
    }
    public Core.Settings Settings => Core.Settings.Current;

    public void Progress(double fraction) => _progress?.Report(Math.Clamp(fraction, 0, 1));
    public void Status(string text) => _status?.Report(text);

    /// <summary>Runs <paramref name="work"/> for every input, reporting overall progress.</summary>
    public async Task ForEach(Func<string, IProgress<double>, Task> work)
    {
        int n = Paths.Count;
        for (int i = 0; i < n; i++)
        {
            Token.ThrowIfCancellationRequested();
            int index = i;
            if (n > 1) Status($"{Path.GetFileName(Paths[i])}  ·  {i + 1} of {n}");
            await work(Paths[i], new InlineProgress(f => Progress((index + Math.Clamp(f, 0, 1)) / n)));
            Progress((double)(i + 1) / n);
        }
    }

    /// <summary>IProgress that reports synchronously (System.Progress posts to the captured context).</summary>
    sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public static class Output
{
    /// <summary>"photo.heic" → "photo.png" next to the source, never overwriting anything.</summary>
    public static string For(string source, string newExtension, string suffix = "")
    {
        var dir = WritableDir(Path.GetDirectoryName(source)!);
        var name = Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')) + suffix;
        return Unique(Path.Combine(dir, name + "." + newExtension.TrimStart('.')));
    }

    public static string FolderFor(string source, string suffix)
    {
        var dir = WritableDir(Path.GetDirectoryName(source)!);
        var name = Path.GetFileNameWithoutExtension(source) + suffix;
        var path = Path.Combine(dir, name);
        int i = 2;
        while (Directory.Exists(path) || File.Exists(path)) path = Path.Combine(dir, $"{name} ({i++})");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    static string WritableDir(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".satsuma-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }
    }
}
