using System;
using System.Collections.Generic;
using System.IO;

namespace Satsuma.Ops;

public enum FileKind { Image, Video, Audio, Pdf, Word, Rtf, Markdown, Text, Html, Sheet, Subtitle, Archive, Folder, Other }

public static class FileKinds
{
    static readonly Dictionary<string, FileKind> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Add(FileKind.Image, "jpg", "jpeg", "jfif", "png", "webp", "heic", "heif", "avif", "tif", "tiff", "bmp", "gif", "ico", "svg", "psd", "tga", "jxl");
        Add(FileKind.Video, "mp4", "mov", "mkv", "webm", "avi", "wmv", "m4v", "flv", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts");
        Add(FileKind.Audio, "mp3", "m4a", "wav", "flac", "ogg", "oga", "opus", "aiff", "aif", "wma", "aac", "amr");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Word, "docx");
        Add(FileKind.Rtf, "rtf");
        Add(FileKind.Markdown, "md", "markdown");
        Add(FileKind.Text, "txt", "log", "text");
        Add(FileKind.Html, "html", "htm");
        Add(FileKind.Sheet, "xlsx", "xlsm", "csv", "tsv");
        Add(FileKind.Subtitle, "srt", "vtt");
        Add(FileKind.Archive, "zip", "tar", "gz", "tgz", "7z", "rar");
    }

    static void Add(FileKind kind, params string[] exts)
    {
        foreach (var e in exts) Map[e] = kind;
    }

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return FileKind.Archive;
        return Map.TryGetValue(Ext(path), out var k) ? k : FileKind.Other;
    }

    /// <summary>Glyph that represents a kind in the wheel's center.</summary>
    public static string Glyph(FileKind kind) => kind switch
    {
        FileKind.Image => "",
        FileKind.Video => "",
        FileKind.Audio => "",
        FileKind.Pdf => "",
        FileKind.Word or FileKind.Rtf or FileKind.Text or FileKind.Markdown => "",
        FileKind.Html => "",
        FileKind.Sheet => "",
        FileKind.Subtitle => "",
        FileKind.Archive => "",
        FileKind.Folder => "",
        _ => "",
    };
}
