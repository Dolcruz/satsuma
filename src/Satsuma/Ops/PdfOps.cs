using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Satsuma.Ops;

public static class PdfOps
{
    public static readonly string[] Targets = { "PNG", "JPG", "TXT", "MD", "DOCX", "HTML" };

    public static async Task Convert(string path, string target, IProgress<double> p, OpContext ctx)
    {
        switch (target.ToUpperInvariant())
        {
            case "PNG":
            case "JPG":
                await RenderPages(path, target.ToUpperInvariant() == "JPG", p, ctx);
                break;
            default:
                await Task.Run(() =>
                {
                    var pages = ExtractText(path);
                    using var output = Output.For(path, target.ToLowerInvariant());
                    switch (target.ToUpperInvariant())
                    {
                        case "TXT": File.WriteAllText(output.TemporaryPath, string.Join("\n\n", pages), Encoding.UTF8); break;
                        case "MD": File.WriteAllText(output.TemporaryPath, ToMarkdown(Path.GetFileNameWithoutExtension(path), pages), Encoding.UTF8); break;
                        case "HTML": File.WriteAllText(output.TemporaryPath, DocOps.HtmlPage(Path.GetFileNameWithoutExtension(path), Markdig.Markdown.ToHtml(ToMarkdown(Path.GetFileNameWithoutExtension(path), pages))), Encoding.UTF8); break;
                        case "DOCX": DocOps.WriteDocx(output.TemporaryPath, DocOps.BlocksFromMarkdown(ToMarkdown(Path.GetFileNameWithoutExtension(path), pages))); break;
                    }
                    output.Commit(ctx);
                });
                break;
        }
    }

    static List<string> ExtractText(string path)
    {
        var pages = new List<string>();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            string text;
            try { text = ContentOrderTextExtractor.GetText(page); }
            catch { text = page.Text; }
            pages.Add(text.Trim());
        }
        return pages;
    }

    static string ToMarkdown(string title, List<string> pages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + title).AppendLine();
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages.Count > 1) sb.AppendLine($"## Page {i + 1}").AppendLine();
            // Re-flow hard-wrapped lines into paragraphs; blank lines stay paragraph breaks.
            foreach (var para in pages[i].Replace("\r", "").Split("\n\n"))
            {
                var line = string.Join(" ", para.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
                if (line.Length > 0) sb.AppendLine(line).AppendLine();
            }
        }
        return sb.ToString();
    }

    static async Task RenderPages(string path, bool jpeg, IProgress<double> p, OpContext ctx)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        uint count = doc.PageCount;
        string ext = jpeg ? "jpg" : "png";
        using var output = count > 1 ? Output.FolderFor(path, " pages") : Output.For(path, ext);
        double scale = ctx.Settings.PdfRenderDpi / 96.0;

        for (uint i = 0; i < count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Round(page.Size.Width * scale),
                DestinationHeight = (uint)Math.Round(page.Size.Height * scale),
                BitmapEncoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId,
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            var destination = count > 1
                ? Path.Combine(output.TemporaryPath, $"{Path.GetFileNameWithoutExtension(path)}-{(i + 1).ToString().PadLeft(count.ToString().Length, '0')}.{ext}")
                : output.TemporaryPath;
            stream.Seek(0);
            await using (var fs = File.Create(destination))
                await stream.AsStreamForRead().CopyToAsync(fs);
            p.Report((i + 1.0) / count);
        }
        output.Commit(ctx);
    }

    // ---- Tools ----

    public static Task Merge(OpContext ctx) => Task.Run(() =>
    {
        using var output = new PdfDocument();
        var files = ctx.Paths.Where(p => FileKinds.Of(p) == FileKind.Pdf).OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int f = 0; f < files.Count; f++)
        {
            using var input = PdfReader.Open(files[f], PdfDocumentOpenMode.Import);
            foreach (var page in input.Pages) output.AddPage(page);
            ctx.Progress((f + 1.0) / files.Count);
        }
        using var path = Output.For(files[0], "pdf", " (merged)");
        output.Save(path.TemporaryPath);
        path.Commit(ctx);
    });

    public static Task Split(string path, IProgress<double> p, OpContext ctx) => Task.Run(() =>
    {
        using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        using var folder = Output.FolderFor(path, " pages");
        int count = input.PageCount;
        for (int i = 0; i < count; i++)
        {
            using var single = new PdfDocument();
            single.AddPage(input.Pages[i]);
            single.Save(Path.Combine(folder.TemporaryPath, $"{Path.GetFileNameWithoutExtension(path)}-{(i + 1).ToString().PadLeft(count.ToString().Length, '0')}.pdf"));
            p.Report((i + 1.0) / count);
        }
        folder.Commit(ctx);
    });

    public static Task Rotate(string path, IProgress<double> p, OpContext ctx) => Task.Run(() =>
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        foreach (var page in doc.Pages) page.Rotate = (page.Rotate + 90) % 360;
        using var output = Output.For(path, "pdf", " (rotated)");
        doc.Save(output.TemporaryPath);
        output.Commit(ctx);
    });
}
