using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Satsuma.Core;

namespace Satsuma.Ops;

/// <summary>Decides which slices the wheel shows for a given selection and mode.</summary>
public static class Catalog
{
    public static List<WheelItem> For(WheelMode mode, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return new();
        return mode switch
        {
            WheelMode.Convert => Convert(paths),
            WheelMode.Tools => Tools(paths),
            _ => Actions(),
        };
    }

    // ---------------- Convert ----------------

    static string[] TargetsFor(string path)
    {
        var kind = FileKinds.Of(path);
        var ext = FileKinds.Ext(path);
        string[] all = kind switch
        {
            FileKind.Image => ImageOps.Targets,
            FileKind.Video => MediaOps.VideoTargets,
            FileKind.Audio => MediaOps.AudioTargets,
            FileKind.Pdf => PdfOps.Targets,
            FileKind.Sheet => SheetOps.Targets,
            FileKind.Subtitle => new[] { "SRT", "VTT", "TXT" },
            FileKind.Archive => ArchiveOps.TargetsFor(path),
            FileKind.Word or FileKind.Rtf or FileKind.Markdown or FileKind.Text or FileKind.Html => DocOps.TargetsFor(kind),
            _ => Array.Empty<string>(),
        };
        return all.Where(t => !SameFormat(t, ext)).ToArray();
    }

    static bool SameFormat(string target, string ext) => (target.ToLowerInvariant(), ext) switch
    {
        ("jpg", "jpeg" or "jfif") => true,
        ("tiff", "tif") => true,
        ("aiff", "aif") => true,
        ("heic", "heif") => true,
        ("xlsx", "xlsm") => true,
        ("md", "markdown") => true,
        ("html", "htm") => true,
        ("ogg", "oga") => true,
        var (t, e) => t == e,
    };

    static List<WheelItem> Convert(IReadOnlyList<string> paths)
    {
        // Offer a format when every selected file either can become it or already is it
        // (so a PNG + JPG selection still offers "JPG" — the JPG is simply left alone).
        bool Already(string p, string t) => SameFormat(t, FileKinds.Ext(p));
        var candidates = paths.SelectMany(TargetsFor).Distinct().ToList();
        var targets = candidates
            .Where(t => paths.All(p => TargetsFor(p).Contains(t) || Already(p, t)))
            .OrderBy(t => Array.IndexOf(TargetsFor(paths.First(p => !Already(p, t))), t))
            .ToList();
        bool anyMedia = paths.Any(p => FileKinds.Of(p) is FileKind.Video or FileKind.Audio);

        return targets.Select(t => new WheelItem
        {
            Id = "convert:" + t,
            Label = t == "WEBM" ? "WebM" : t == "WEBP" ? "WebP" : t == "OPUS" ? "Opus" : t,
            Glyph = GlyphForFormat(t),
            Verb = "Converting to " + t,
            NeedsFfmpeg = anyMedia,
            Run = ctx => ctx.ForEach((path, p) => Already(path, t) ? Task.CompletedTask : ConvertOne(path, t, p, ctx)),
        }).ToList();
    }

    static Task ConvertOne(string path, string target, IProgress<double> p, OpContext ctx) => FileKinds.Of(path) switch
    {
        FileKind.Image => ImageOps.Convert(path, target, ctx),
        FileKind.Video or FileKind.Audio => MediaOps.Convert(path, target, p, ctx),
        FileKind.Pdf => PdfOps.Convert(path, target, p, ctx),
        FileKind.Sheet => SheetOps.Convert(path, target, ctx),
        FileKind.Subtitle => SubtitleOps.Convert(path, target, ctx),
        FileKind.Archive => ArchiveOps.Convert(path, target, ctx),
        _ => DocOps.Convert(path, target, ctx),
    };

    static string GlyphForFormat(string t) => t switch
    {
        "JPG" or "PNG" or "WEBP" or "AVIF" or "HEIC" or "TIFF" or "BMP" => "",
        "ICO" => "",
        "GIF" => "",
        "PDF" => "",
        "MP4" or "MOV" or "MKV" or "WEBM" or "AVI" or "WMV" => "",
        "MP3" or "M4A" or "WAV" or "FLAC" or "OGG" or "OPUS" or "AIFF" or "WMA" => "",
        "TXT" => "",
        "MD" => "",
        "HTML" => "",
        "DOCX" => "",
        "XLSX" or "CSV" or "TSV" => "",
        "JSON" => "",
        "SRT" or "VTT" => "",
        "ZIP" or "TAR" or "TGZ" => "",
        _ => "",
    };

    // ---------------- Tools ----------------

    static WheelItem Each(string id, string label, string glyph, string verb, Func<string, IProgress<double>, OpContext, Task> run, bool ffmpeg = false) => new()
    {
        Id = id, Label = label, Glyph = glyph, Verb = verb, NeedsFfmpeg = ffmpeg,
        Run = ctx => ctx.ForEach((path, p) => run(path, p, ctx)),
    };

    static WheelItem All(string id, string label, string glyph, string verb, Func<OpContext, Task> run) => new()
    {
        Id = id, Label = label, Glyph = glyph, Verb = verb, Run = run,
    };

    static List<WheelItem> ToolsFor(FileKind kind, int count) => kind switch
    {
        FileKind.Image => new List<WheelItem>
        {
            Each("img:compress", "Compress", "", "Compressing", (f, _, c) => ImageOps.Compress(f, c)),
            Each("img:half", "Resize 50%", "", "Resizing", (f, _, c) => ImageOps.HalfSize(f, c)),
            Each("img:rotate", "Rotate", "", "Rotating", (f, _, c) => ImageOps.Rotate(f, c)),
            Each("img:flip", "Flip", "", "Flipping", (f, _, c) => ImageOps.Flip(f, c)),
            Each("img:gray", "Grayscale", "", "Removing color", (f, _, c) => ImageOps.Grayscale(f, c)),
            Each("img:strip", "Strip EXIF", "", "Removing metadata", (f, _, c) => ImageOps.StripMetadata(f, c)),
        }.Concat(count > 1 ? new[]
        {
            All("img:pdf", "Make PDF", "", "Combining into PDF", ImageOps.CombineToPdf),
            All("img:gif", "Make GIF", "", "Animating", ImageOps.MakeGif),
        } : Array.Empty<WheelItem>()).ToList(),

        FileKind.Video => new List<WheelItem>
        {
            Each("vid:compress", "Compress", "", "Compressing video", MediaOps.CompressVideo, true),
            Each("vid:720", "720p", "", "Downscaling", MediaOps.To720p, true),
            Each("vid:mute", "Mute", "", "Removing audio", MediaOps.Mute, true),
            Each("vid:rotate", "Rotate", "", "Rotating video", MediaOps.RotateVideo, true),
            Each("vid:speed", "2× Speed", "", "Speeding up", MediaOps.DoubleSpeed, true),
            Each("vid:frame", "Snapshot", "", "Grabbing a frame", MediaOps.Snapshot, true),
        },

        FileKind.Audio => new List<WheelItem>
        {
            Each("aud:compress", "Compress", "", "Compressing audio", MediaOps.CompressAudio, true),
            Each("aud:norm", "Normalize", "", "Normalizing loudness", MediaOps.Normalize, true),
            Each("aud:mono", "Mono", "", "Mixing to mono", MediaOps.Mono, true),
            Each("aud:trim", "Trim Silence", "", "Trimming silence", MediaOps.TrimSilence, true),
        },

        FileKind.Pdf => new List<WheelItem>
        {
            Each("pdf:split", "Split", "", "Splitting pages", PdfOps.Split),
            Each("pdf:rotate", "Rotate", "", "Rotating pages", PdfOps.Rotate),
        }.Concat(count > 1 ? new[] { All("pdf:merge", "Merge", "", "Merging PDFs", PdfOps.Merge) } : Array.Empty<WheelItem>()).ToList(),

        FileKind.Archive => new List<WheelItem>
        {
            Each("arc:extract", "Extract", "", "Extracting", (f, _, c) => ArchiveOps.Extract(f, c)),
        },

        _ => new List<WheelItem>(),
    };

    static List<WheelItem> Tools(IReadOnlyList<string> paths)
    {
        var kinds = paths.Select(FileKinds.Of).Distinct().ToList();
        List<WheelItem> items;
        if (kinds.Count == 1) items = ToolsFor(kinds[0], paths.Count);
        else
        {
            // Mixed selection: only tools that every kind supports (matched by label, e.g. "Rotate").
            var sets = kinds.Select(k => ToolsFor(k, paths.Count)).ToList();
            var labels = sets.Select(s => s.Select(i => i.Label)).Aggregate((a, b) => a.Intersect(b)).ToList();
            items = labels.Select(label => new WheelItem
            {
                Id = "mixed:" + label,
                Label = label,
                Glyph = sets[0].First(i => i.Label == label).Glyph,
                Verb = sets[0].First(i => i.Label == label).Verb,
                NeedsFfmpeg = kinds.Any(k => k is FileKind.Video or FileKind.Audio),
                Run = ctx => ctx.ForEach((path, p) =>
                {
                    var tool = ToolsFor(FileKinds.Of(path), 1).First(i => i.Label == label);
                    return tool.Run(new OpContext(new[] { path }, p, null, ctx.Token).With(ctx));
                }),
            }).ToList();
        }

        if (!kinds.Contains(FileKind.Archive) || paths.Count > 1)
            items.Add(All("any:zip", "ZIP", "", "Zipping", ArchiveOps.Zip));
        return items;
    }

    // ---------------- Actions ----------------

    static List<WheelItem> Actions() => new()
    {
        new() { Id = "act:share", Label = "Share", Glyph = "", Instant = true, Run = FileActions.Share },
        new() { Id = "act:float", Label = "Float", Glyph = "", Instant = true, Run = FileActions.Float },
        new() { Id = "act:copy", Label = "Copy to", Glyph = "", Instant = true, Run = FileActions.CopyTo },
        new() { Id = "act:move", Label = "Move to", Glyph = "", Instant = true, Run = FileActions.MoveTo },
        new() { Id = "act:path", Label = "Copy Path", Glyph = "", Instant = true, Run = FileActions.CopyPath },
        new() { Id = "act:term", Label = "Terminal", Glyph = "", Instant = true, Run = FileActions.Terminal },
    };
}
