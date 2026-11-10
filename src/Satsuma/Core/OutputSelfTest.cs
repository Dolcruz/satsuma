using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using PdfSharp.Pdf;
using Satsuma.Ops;

namespace Satsuma.Core;

/// <summary>Verifies publication and cleanup with overlapping conversions and independent outputs.</summary>
public static class OutputSelfTest
{
    public static async Task<int> Run(string dir, StringBuilder report)
    {
        Directory.CreateDirectory(dir);
        int failures = 0;
        async Task Check(string name, Func<string, Task> test)
        {
            var root = Path.Combine(dir, name);
            Directory.CreateDirectory(root);
            try
            {
                await test(root);
                Require(!Directory.EnumerateDirectories(root, ".satsuma-*", SearchOption.AllDirectories).Any(),
                    "An operation left its staging directory behind.");
                report.AppendLine($"ok    output safety  {name}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"FAIL  output safety  {name}: {ex.Message}");
            }
        }
        static OpContext Context(string path, CancellationToken token = default) => new(new[] { path }, null, null, token);

        await Check("existing-and-late-file", root =>
        {
            var target = Path.Combine(root, "result.txt");
            File.WriteAllText(target, "existing");
            using (var output = Output.FileAt(target))
            {
                File.WriteAllText(output.TemporaryPath, "converted");
                // Another application claims the next candidate after our work starts.
                File.WriteAllText(Path.Combine(root, "result (2).txt"), "arrived later");
                var ctx = Context(target);
                var result = output.Commit(ctx);
                Require(Path.GetFullPath(result) == Path.GetFullPath(Path.Combine(root, "result (3).txt")) && File.ReadAllText(result) == "converted", "Collision naming failed.");
                Require(ctx.Outputs.SequenceEqual(new[] { result }), "Published output was not registered.");
            }
            Require(File.ReadAllText(target) == "existing" && File.ReadAllText(Path.Combine(root, "result (2).txt")) == "arrived later",
                "An existing file was overwritten.");
            return Task.CompletedTask;
        });
        await Check("concurrent-publication", async root =>
        {
            var target = Path.Combine(root, "result.txt");
            using var first = Output.FileAt(target);
            using var second = Output.FileAt(target);
            File.WriteAllText(first.TemporaryPath, "first");
            File.WriteAllText(second.TemporaryPath, "second");
            var a = Context(target);
            var b = Context(target);
            await Task.WhenAll(Task.Run(() => first.Commit(a)), Task.Run(() => second.Commit(b)));
            Require(a.Outputs.Single() != b.Outputs.Single(), "Two jobs reported the same output.");
            Require(File.ReadAllText(a.Outputs.Single()) == "first" && File.ReadAllText(b.Outputs.Single()) == "second", "A completed result was lost.");
        });
        await Check("cancellation-before-publication", root =>
        {
            var target = Path.Combine(root, "result.txt");
            File.WriteAllText(target, "another job");
            using var cancelled = new CancellationTokenSource();
            var ctx = Context(target, cancelled.Token);
            using (var output = Output.FileAt(target))
            {
                File.WriteAllText(output.TemporaryPath, "unfinished");
                cancelled.Cancel();
                try { output.Commit(ctx); throw new Exception("Cancelled output was published."); }
                catch (OperationCanceledException) { }
            }
            Require(ctx.Outputs.Count == 0 && File.ReadAllText(target) == "another job" && Directory.GetFiles(root).Length == 1,
                "Cancellation modified another output.");
            return Task.CompletedTask;
        });
        await Check("failure-cleans-only-owned-output", root =>
        {
            var target = Path.Combine(root, "result.txt");
            File.WriteAllText(target, "another job");
            try
            {
                using var output = Output.FileAt(target);
                File.WriteAllText(output.TemporaryPath, "partial");
                throw new InvalidDataException("writer failed");
            }
            catch (InvalidDataException) { }
            Require(File.ReadAllText(target) == "another job" && Directory.GetFiles(root).Length == 1, "Failure changed another output.");
            return Task.CompletedTask;
        });
        await Check("folder-collision-and-failure", root =>
        {
            var target = Path.Combine(root, "pages");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "existing.txt"), "existing");
            var ctx = Context(target);
            using (var output = Output.FolderAt(target))
            {
                File.WriteAllText(Path.Combine(output.TemporaryPath, "page.txt"), "complete");
                Require(Directory.GetDirectories(root, "pages*").Length == 1, "An incomplete final folder is visible.");
                output.Commit(ctx);
            }
            using (var failed = Output.FolderAt(target))
                File.WriteAllText(Path.Combine(failed.TemporaryPath, "partial.txt"), "partial");
            Require(File.ReadAllText(Path.Combine(target, "existing.txt")) == "existing" &&
                File.ReadAllText(Path.Combine(ctx.Outputs.Single(), "page.txt")) == "complete" &&
                Directory.GetDirectories(root, "pages*").Length == 2, "Folder cleanup changed a published result.");
            return Task.CompletedTask;
        });
        await Check("concurrent-image-conversions", async root =>
        {
            var png = Path.Combine(root, "photo.png");
            var bmp = Path.Combine(root, "photo.bmp");
            using (var image = new MagickImage(MagickColors.Red, 256, 256)) image.Write(png);
            using (var image = new MagickImage(MagickColors.Blue, 256, 256)) image.Write(bmp);
            var first = Context(png);
            var second = Context(bmp);
            await Task.WhenAll(ImageOps.Convert(png, "WEBP", first), ImageOps.Convert(bmp, "WEBP", second));
            Require(first.Outputs.Single() != second.Outputs.Single(), "Two conversions published the same file.");
            using var red = new MagickImage(first.Outputs.Single());
            using var blue = new MagickImage(second.Outputs.Single());
            using var redPixels = red.GetPixels();
            using var bluePixels = blue.GetPixels();
            var redPixel = redPixels.GetPixel(0, 0).ToColor()!;
            var bluePixel = bluePixels.GetPixel(0, 0).ToColor()!;
            Require(redPixel.R > redPixel.B && bluePixel.B > bluePixel.R, "A conversion contains the other job's image.");
            File.WriteAllText(Path.Combine(root, "photo.gif"), "not an image");
            var failed = Context(Path.Combine(root, "photo.gif"));
            try { await ImageOps.Convert(failed.Paths[0], "WEBP", failed); throw new Exception("Invalid image unexpectedly converted."); }
            catch (MagickException) { }
            Require(failed.Outputs.Count == 0 && Directory.GetFiles(root, "*.webp").Length == 2, "Failure changed completed images.");
        });
        await Check("archive-folder-publication", async root =>
        {
            var source = Path.Combine(root, "bundle.zip");
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("nested/file.txt").Open()))
                writer.Write("archive contents");
            var first = Context(source);
            var second = Context(source);
            await Task.WhenAll(ArchiveOps.Extract(source, first), ArchiveOps.Extract(source, second));
            Require(first.Outputs.Single() != second.Outputs.Single() &&
                first.Outputs.Concat(second.Outputs).All(p => File.ReadAllText(Path.Combine(p, "nested", "file.txt")) == "archive contents"),
                "Concurrent extraction lost a folder.");
        });
        if (FFmpeg.IsAvailable)
        {
            await Check("media-failure-and-cancellation", async root =>
            {
                var source = Path.Combine(root, "tone.wav");
                var invalid = Path.Combine(root, "tone.flac");
                await FFmpeg.Run($"-f lavfi -i sine=frequency=440:duration=2 -c:a pcm_s16le {FFmpeg.Q(source)}", null, default);
                File.WriteAllText(invalid, "invalid audio");
                var success = Context(source);
                var failed = Context(invalid);
                async Task Fail()
                {
                    try { await MediaOps.Convert(invalid, "MP3", new Progress<double>(), failed); }
                    catch (InvalidOperationException) { return; }
                    throw new Exception("Invalid audio unexpectedly converted.");
                }
                await Task.WhenAll(MediaOps.Convert(source, "MP3", new Progress<double>(), success), Fail());
                var completed = success.Outputs.Single();
                var original = File.ReadAllBytes(completed);
                Require(failed.Outputs.Count == 0 && original.Length > 0, "A failed media conversion changed another job's result.");
                using var cancel = new CancellationTokenSource();
                var cancelled = Context(source, cancel.Token);
                cancel.Cancel();
                try { await MediaOps.Convert(source, "MP3", new Progress<double>(), cancelled); throw new Exception("Cancelled media unexpectedly completed."); }
                catch (OperationCanceledException) { }
                Require(cancelled.Outputs.Count == 0 && File.ReadAllBytes(completed).SequenceEqual(original) && Directory.GetFiles(root, "*.mp3").Length == 1,
                    "Cancelled media changed a completed output.");
            });
        }
        else report.AppendLine("skip  output safety  media-failure-and-cancellation (FFmpeg not installed)");
        await Check("pdf-folder-publication", async root =>
        {
            var source = Path.Combine(root, "document.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.AddPage(); pdf.Save(source); }
            var first = Context(source);
            var second = Context(source);
            await Task.WhenAll(PdfOps.Split(source, new Progress<double>(), first), PdfOps.Split(source, new Progress<double>(), second));
            Require(first.Outputs.Single() != second.Outputs.Single() &&
                first.Outputs.Concat(second.Outputs).All(p => Directory.GetFiles(p, "*.pdf").Length == 2),
                "Concurrent PDF splitting lost pages.");
        });
        return failures;
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
