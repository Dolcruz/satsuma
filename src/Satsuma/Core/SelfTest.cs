using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImageMagick;
using ImageMagick.Drawing;
using Satsuma.Ops;

namespace Satsuma.Core;

/// <summary>
/// `Satsuma.exe --selftest <dir>`: generates sample files of every kind and runs every wheel slice on them.
/// Writes a report to <dir>/selftest.txt and exits with the number of failures.
/// </summary>
public static class SelfTest
{
    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        int failures = 0;

        var samples = await MakeSamples(dir, report);

        foreach (var (name, paths) in samples)
        {
            foreach (var mode in new[] { WheelMode.Convert, WheelMode.Tools })
            {
                foreach (var item in Catalog.For(mode, paths))
                {
                    var ctx = new OpContext(paths, null, null, default);
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await item.Run(ctx);
                        var missing = ctx.Outputs.Where(o => !File.Exists(o) && !Directory.Exists(o)).ToList();
                        var empty = ctx.Outputs.Where(o => File.Exists(o) && new FileInfo(o).Length == 0).ToList();
                        if (ctx.Outputs.Count == 0 || missing.Count > 0 || empty.Count > 0)
                        {
                            failures++;
                            report.AppendLine($"FAIL  {name,-14} {item.Id,-22} no/empty output");
                        }
                        else
                            report.AppendLine($"ok    {name,-14} {item.Id,-22} {sw.ElapsedMilliseconds,6} ms  → {string.Join(", ", ctx.Outputs.Select(Path.GetFileName))}");
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        report.AppendLine($"FAIL  {name,-14} {item.Id,-22} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                    }
                }
            }
        }
        report.AppendLine().AppendLine($"{failures} failure(s)");
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), report.ToString());
        return failures;
    }

    static async Task<List<(string, string[])>> MakeSamples(string dir, StringBuilder report)
    {
        var list = new List<(string, string[])>();
        string P(string n) => Path.Combine(dir, n);

        // Images
        using (var img = new MagickImage(MagickColors.Orange, 320, 200))
        {
            new Drawables().FillColor(MagickColors.White).Circle(160, 100, 200, 100).Draw(img);
            img.Write(P("photo.png"));
            img.Quality = 90;
            img.Write(P("photo.jpg"));
            img.Write(P("photo.avif"), MagickFormat.Avif);
        }
        File.WriteAllText(P("logo.svg"), """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><circle cx="32" cy="32" r="28" fill="#f80"/></svg>""");
        list.Add(("png", new[] { P("photo.png") }));
        list.Add(("avif", new[] { P("photo.avif") }));
        list.Add(("svg", new[] { P("logo.svg") }));
        list.Add(("2 images", new[] { P("photo.png"), P("photo.jpg") }));

        // PDF (from image) — 2 pages
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.White, 595, 842));
            pages.Add(new MagickImage(MagickColors.LightBlue, 595, 842));
            pages.Write(P("doc.pdf"), MagickFormat.Pdf);
        }
        File.Copy(P("doc.pdf"), P("doc2.pdf"), true);
        list.Add(("pdf", new[] { P("doc.pdf") }));
        list.Add(("2 pdfs", new[] { P("doc.pdf"), P("doc2.pdf") }));

        // Text-ish
        File.WriteAllText(P("notes.md"), "# Notes\n\nSome **bold** and *italic* text.\n\n- one\n- two\n\n1. first\n2. second\n\n```\ncode block\n```\n\n> quoted\n");
        File.WriteAllText(P("plain.txt"), "Hello world.\nSecond line.\n\nNew paragraph.");
        File.WriteAllText(P("page.html"), "<html><body><h1>Title</h1><p>Para with <b>bold</b> and <a href=\"https://x.y\">link</a>.</p><ul><li>a</li><li>b</li></ul></body></html>");
        File.WriteAllText(P("letter.rtf"), @"{\rtf1\ansi{\fonttbl\f0\fswiss Helvetica;}\f0\pard Dear reader,\par This is {\b RTF}.\par}");
        list.Add(("md", new[] { P("notes.md") }));
        list.Add(("txt", new[] { P("plain.txt") }));
        list.Add(("html", new[] { P("page.html") }));
        list.Add(("rtf", new[] { P("letter.rtf") }));

        // Sheets
        File.WriteAllText(P("data.csv"), "name,amount,zip\nAnna,12.5,01234\n\"Bob, Jr.\",7,10115\n");
        File.WriteAllText(P("data-de.csv"), "Name;Betrag\nAnna;12\n");
        list.Add(("csv", new[] { P("data.csv") }));
        list.Add(("csv;", new[] { P("data-de.csv") }));

        // Subtitles
        File.WriteAllText(P("subs.srt"), "1\n00:00:01,000 --> 00:00:02,500\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n");
        list.Add(("srt", new[] { P("subs.srt") }));

        // Archive
        var zipDir = Directory.CreateDirectory(P("folder"));
        File.WriteAllText(Path.Combine(zipDir.FullName, "a.txt"), "a");
        System.IO.Compression.ZipFile.CreateFromDirectory(zipDir.FullName, P("bundle.zip"));
        list.Add(("zip", new[] { P("bundle.zip") }));
        list.Add(("folder", new[] { zipDir.FullName }));

        // Media (only if FFmpeg is present — don't download in tests)
        var ff = FFmpeg.Find();
        if (ff != null)
        {
            await FFmpeg.Run($"-f lavfi -i testsrc=size=320x240:rate=25:duration=3 -f lavfi -i sine=frequency=440:duration=3 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {FFmpeg.Q(P("clip.mp4"))}", null, default);
            await FFmpeg.Run($"-f lavfi -i \"sine=frequency=440:duration=3\" -c:a pcm_s16le {FFmpeg.Q(P("tone.wav"))}", null, default);
            list.Add(("mp4", new[] { P("clip.mp4") }));
            list.Add(("wav", new[] { P("tone.wav") }));
            list.Add(("mixed", new[] { P("clip.mp4"), P("photo.png") }));
        }
        else report.AppendLine("skip  media (FFmpeg not installed)");

        // After generating the docx via a conversion, test docx as input too.
        await DocOps.Convert(P("notes.md"), "DOCX", new OpContext(new[] { P("notes.md") }, null, null, default));
        var docx = Directory.GetFiles(dir, "notes*.docx").First();
        list.Add(("docx", new[] { docx }));
        return list;
    }
}
