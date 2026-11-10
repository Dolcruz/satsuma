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
        await Check("long-image-paths", async root =>
        {
            // Final paths fit the legacy native limit; owned staging adds enough depth to exceed it.
            var nested = Path.Combine(root, new string('d', Math.Max(1, 225 - Path.GetFullPath(root).Length - 1)));
            Directory.CreateDirectory(nested);
            var png = Path.Combine(nested, "photo.png");
            var blue = Path.Combine(nested, "blue.png");
            using (var image = new MagickImage(MagickColors.Red, 32, 24)) image.Write(png);
            using (var image = new MagickImage(MagickColors.Blue, 32, 24)) image.Write(blue);
            using (var probe = Output.For(png, "webp"))
                Require(probe.TemporaryPath.Length > 260, "The fixture did not exercise a long staging path.");

            var converted = Context(png);
            await ImageOps.Convert(png, "WEBP", converted);
            using (var image = new MagickImage(converted.Outputs.Single()))
                Require(image.Format == MagickFormat.WebP && image.Width == 32 && image.Height == 24, "Long-path conversion changed the image.");

            var resized = Context(png);
            await ImageOps.HalfSize(png, resized);
            using (var image = new MagickImage(resized.Outputs.Single()))
                Require(image.Format == MagickFormat.Png && image.Width == 16 && image.Height == 12, "Long-path transform lost its format or size.");

            var pdf = new OpContext(new[] { png, blue }, null, null, default);
            await ImageOps.CombineToPdf(pdf);
            using (var document = PdfSharp.Pdf.IO.PdfReader.Open(pdf.Outputs.Single()))
                Require(document.PageCount == 2, "Long-path image collection lost a PDF page.");

            var gif = new OpContext(new[] { png, blue }, null, null, default);
            await ImageOps.MakeGif(gif);
            using (var frames = new MagickImageCollection(gif.Outputs.Single()))
                Require(frames.Count == 2, "Long-path GIF lost a frame.");
            var animated = Context(gif.Outputs.Single());
            await ImageOps.Convert(animated.Paths[0], "WEBP", animated);
            using (var frames = new MagickImageCollection(animated.Outputs.Single()))
                Require(frames.Count == 2 && frames.All(f => f.Format == MagickFormat.WebP), "Long-path animated WebP lost its frames or format.");
        });
        await Check("image-transform-formats", async root =>
        {
            var formats = new[]
            {
                ("jpg", MagickFormat.Jpeg), ("jpeg", MagickFormat.Jpeg), ("jfif", MagickFormat.Jpeg),
                ("png", MagickFormat.Png), ("webp", MagickFormat.WebP), ("avif", MagickFormat.Avif),
                ("tif", MagickFormat.Tiff), ("tiff", MagickFormat.Tiff), ("bmp", MagickFormat.Bmp),
                ("gif", MagickFormat.Gif), ("ico", MagickFormat.Ico), ("tga", MagickFormat.Tga), ("jxl", MagickFormat.Jxl),
            };
            foreach (var (extension, format) in formats)
            {
                var source = Path.Combine(root, "photo." + extension);
                using (var image = new MagickImage(MagickColors.Red, 32, 24)) image.Write(source, format);
                var ctx = Context(source);
                await ImageOps.HalfSize(source, ctx);
                using var result = new MagickImage(ctx.Outputs.Single());
                Require(result.Format == format && result.Width == 16 && result.Height == 12,
                    $"Transform changed the {extension} format or dimensions.");
            }
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
