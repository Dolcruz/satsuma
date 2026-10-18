using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;

namespace Satsuma.Ops;

public static class ImageOps
{
    public static readonly string[] Targets = { "JPG", "PNG", "WEBP", "AVIF", "TIFF", "BMP", "GIF", "ICO", "PDF" };

    static MagickImage Load(string path)
    {
        var ext = FileKinds.Ext(path);
        var settings = new MagickReadSettings();
        if (ext == "svg")
        {
            settings.Density = new Density(192);
            settings.BackgroundColor = MagickColors.None;
        }
        var img = new MagickImage(path, settings);
        img.AutoOrient();
        return img;
    }

    static MagickFormat FormatFor(string target) => target.ToUpperInvariant() switch
    {
        "JPG" => MagickFormat.Jpeg,
        "PNG" => MagickFormat.Png,
        "WEBP" => MagickFormat.WebP,
        "AVIF" => MagickFormat.Avif,
        "HEIC" => MagickFormat.Heic,
        "TIFF" => MagickFormat.Tiff,
        "BMP" => MagickFormat.Bmp,
        "GIF" => MagickFormat.Gif,
        "ICO" => MagickFormat.Ico,
        "PDF" => MagickFormat.Pdf,
        _ => throw new NotSupportedException(target),
    };

    static bool LacksAlpha(MagickFormat f) => f is MagickFormat.Jpeg or MagickFormat.Bmp or MagickFormat.Pdf;

    public static Task Convert(string path, string target, OpContext ctx) => Task.Run(() =>
    {
        var format = FormatFor(target);
        var ext = target.ToLowerInvariant();
        var output = Output.For(path, ext);

        // Animated GIF → WebP keeps its animation; everything else takes the first frame.
        if (FileKinds.Ext(path) == "gif" && format is MagickFormat.WebP)
        {
            using var frames = new MagickImageCollection(path);
            frames.Coalesce();
            frames.Write(output, format);
            ctx.Outputs.Add(output);
            return;
        }

        using var img = Load(path);
        if (LacksAlpha(format) && img.HasAlpha)
        {
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaOption.Remove);
        }
        switch (format)
        {
            case MagickFormat.Jpeg:
            case MagickFormat.WebP:
            case MagickFormat.Avif:
            case MagickFormat.Heic:
                img.Quality = (uint)ctx.Settings.JpegQuality;
                break;
            case MagickFormat.Ico:
                img.Resize(new MagickGeometry(256, 256));
                img.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,24,16");
                break;
        }
        img.Write(output, format);
        ctx.Outputs.Add(output);
    });

    static Task Transform(string path, string suffix, OpContext ctx, Action<MagickImage> change, Func<MagickImage, string>? extFor = null) => Task.Run(() =>
    {
        using var img = Load(path);
        change(img);
        var ext = extFor?.Invoke(img) ?? (FileKinds.Ext(path) is "heic" or "heif" or "svg" or "psd" ? "png" : FileKinds.Ext(path));
        var output = Output.For(path, ext, suffix);
        img.Write(output);
        ctx.Outputs.Add(output);
    });

    public static Task Compress(string path, OpContext ctx) => Transform(path, " (compressed)", ctx, img =>
    {
        img.Strip();
        var q = (uint)ctx.Settings.CompressQuality;
        if (img.Format is MagickFormat.Png or MagickFormat.Png32 or MagickFormat.Png24 or MagickFormat.Png8)
        {
            img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        }
        else img.Quality = q;
        // Anything larger than 4K gets scaled down — nobody needs a 48 MP photo in a chat.
        if (Math.Max(img.Width, img.Height) > 3840) img.Resize(new MagickGeometry(3840, 3840));
    }, img => img.Format is MagickFormat.Png or MagickFormat.Png32 or MagickFormat.Png24 or MagickFormat.Png8 ? "png"
        : img.Format == MagickFormat.WebP ? "webp" : img.Format == MagickFormat.Avif ? "avif" : "jpg");

    public static Task HalfSize(string path, OpContext ctx) =>
        Transform(path, " (50%)", ctx, img => img.Resize(new Percentage(50)));

    public static Task Rotate(string path, OpContext ctx) =>
        Transform(path, " (rotated)", ctx, img => img.Rotate(90));

    public static Task Flip(string path, OpContext ctx) =>
        Transform(path, " (flipped)", ctx, img => img.Flop());

    public static Task Grayscale(string path, OpContext ctx) =>
        Transform(path, " (grayscale)", ctx, img => img.Grayscale());

    public static Task StripMetadata(string path, OpContext ctx) =>
        Transform(path, " (clean)", ctx, img => img.Strip());

    public static Task CombineToPdf(OpContext ctx) => Task.Run(() =>
    {
        using var all = new MagickImageCollection();
        foreach (var p in ctx.Paths.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
        {
            var img = Load(p);
            if (img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaOption.Remove); }
            all.Add(img);
        }
        var output = Output.For(ctx.Paths[0], "pdf", " (combined)");
        all.Write(output, MagickFormat.Pdf);
        ctx.Outputs.Add(output);
    });

    public static Task MakeGif(OpContext ctx) => Task.Run(() =>
    {
        using var all = new MagickImageCollection();
        uint w = 0, h = 0;
        foreach (var p in ctx.Paths.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
        {
            var img = Load(p);
            if (w == 0)
            {
                img.Resize(new MagickGeometry(800, 800) { Greater = true });
                w = img.Width; h = img.Height;
            }
            else
            {
                img.Resize(new MagickGeometry(w, h));
                img.BackgroundColor = MagickColors.White;
                img.Extent(w, h, Gravity.Center);
            }
            img.AnimationDelay = 60;
            all.Add(img);
        }
        all.Quantize(new QuantizeSettings { Colors = 256 });
        all.Optimize();
        var output = Output.For(ctx.Paths[0], "gif", " (animation)");
        all.Write(output, MagickFormat.Gif);
        ctx.Outputs.Add(output);
    });
}
