using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace Satsuma.Ops;

/// <summary>Spreadsheets (XLSX/CSV/TSV/JSON/HTML).</summary>
public static class SheetOps
{
    public static readonly string[] Targets = { "XLSX", "CSV", "TSV", "JSON", "HTML" };

    public static Task Convert(string path, string target, OpContext ctx) => Task.Run(() =>
    {
        var rows = Read(path);
        var output = Output.For(path, target.ToLowerInvariant());
        switch (target.ToUpperInvariant())
        {
            case "XLSX": WriteXlsx(output, rows); break;
            case "CSV": File.WriteAllText(output, ToDelimited(rows, ','), new UTF8Encoding(true)); break;
            case "TSV": File.WriteAllText(output, ToDelimited(rows, '\t'), new UTF8Encoding(true)); break;
            case "JSON": File.WriteAllText(output, ToJson(rows), Encoding.UTF8); break;
            case "HTML": File.WriteAllText(output, ToHtml(Path.GetFileNameWithoutExtension(path), rows), Encoding.UTF8); break;
        }
        ctx.Outputs.Add(output);
    });

    static List<List<string>> Read(string path)
    {
        var ext = FileKinds.Ext(path);
        if (ext is "xlsx" or "xlsm")
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();
            var range = ws.RangeUsed();
            var rows = new List<List<string>>();
            if (range == null) return rows;
            foreach (var row in range.Rows())
                rows.Add(row.Cells().Select(c => c.GetFormattedString()).ToList());
            return rows;
        }
        var text = File.ReadAllText(path);
        char delim = ext == "tsv" ? '\t' : DetectDelimiter(text);
        return ParseDelimited(text, delim);
    }

    static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        // German Excel exports use semicolons.
        return firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
    }

    public static List<List<string>> ParseDelimited(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delim) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        if (rows.Count > 0 && rows[0].Count > 0 && rows[0][0].StartsWith('﻿')) rows[0][0] = rows[0][0][1..];
        return rows;
    }

    static string ToDelimited(List<List<string>> rows, char delim)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(delim, row.Select(f =>
                f.IndexOfAny(new[] { delim, '"', '\n', '\r' }) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    static void WriteXlsx(string path, List<List<string>> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        for (int r = 0; r < rows.Count; r++)
        for (int c = 0; c < rows[r].Count; c++)
        {
            var v = rows[r][c];
            var cell = ws.Cell(r + 1, c + 1);
            // Keep leading-zero values (postcodes, IDs) as text.
            bool leadingZero = v.Length > 1 && v[0] == '0' && v[1] != '.';
            if (r > 0 && !leadingZero && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                cell.Value = d;
            else cell.Value = v;
        }
        if (rows.Count > 0)
        {
            ws.Row(1).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
        }
        ws.Columns().AdjustToContents(1, Math.Min(rows.Count, 200), 6, 60);
        wb.SaveAs(path);
    }

    static string ToJson(List<List<string>> rows)
    {
        if (rows.Count == 0) return "[]";
        var header = rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"column{i + 1}" : h).ToList();
        var list = rows.Skip(1).Select(r =>
        {
            var obj = new Dictionary<string, object?>();
            for (int i = 0; i < header.Count; i++)
            {
                var v = i < r.Count ? r[i] : "";
                bool leadingZero = v.Length > 1 && v[0] == '0' && v[1] != '.';
                obj[header[i]] = !leadingZero && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && v.Trim().Length > 0 ? d : v;
            }
            return obj;
        }).ToList();
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static string ToHtml(string title, List<List<string>> rows)
    {
        var sb = new StringBuilder("<table>\n");
        for (int r = 0; r < rows.Count; r++)
        {
            var tag = r == 0 ? "th" : "td";
            sb.Append("<tr>");
            foreach (var f in rows[r]) sb.Append($"<{tag}>{WebUtility.HtmlEncode(f)}</{tag}>");
            sb.Append("</tr>\n");
        }
        sb.Append("</table>");
        return DocOps.HtmlPage(title, $"<h1>{WebUtility.HtmlEncode(title)}</h1>\n" + sb);
    }
}

/// <summary>SRT ⇄ VTT, and subtitles → plain text.</summary>
public static class SubtitleOps
{
    public static Task Convert(string path, string target, OpContext ctx) => Task.Run(() =>
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var output = Output.For(path, target.ToLowerInvariant());
        string result = target.ToUpperInvariant() switch
        {
            "VTT" => "WEBVTT\n\n" + Regex.Replace(text.TrimStart('﻿'), @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2"),
            "SRT" => ToSrt(text),
            "TXT" => ToText(text),
            _ => throw new NotSupportedException(target),
        };
        File.WriteAllText(output, result.Replace("\n", "\r\n"), Encoding.UTF8);
        ctx.Outputs.Add(output);
    });

    static readonly Regex Cue = new(@"(?:(\d{2}):)?(\d{2}):(\d{2})[\.,](\d{3})\s*-->\s*(?:(\d{2}):)?(\d{2}):(\d{2})[\.,](\d{3})[^\n]*", RegexOptions.Compiled);

    static IEnumerable<(string start, string end, string text)> Cues(string text)
    {
        foreach (var block in Regex.Split(text, @"\n{2,}"))
        {
            var lines = block.Split('\n');
            int idx = Array.FindIndex(lines, l => Cue.IsMatch(l));
            if (idx < 0) continue;
            var m = Cue.Match(lines[idx]);
            string T(int h, int mi, int s, int ms) =>
                $"{(m.Groups[h].Success ? m.Groups[h].Value : "00")}:{m.Groups[mi].Value}:{m.Groups[s].Value},{m.Groups[ms].Value}";
            yield return (T(1, 2, 3, 4), T(5, 6, 7, 8), string.Join("\n", lines.Skip(idx + 1)).Trim());
        }
    }

    static string ToSrt(string text)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var (s, e, t) in Cues(text))
            sb.Append(n++).Append('\n').Append(s).Append(" --> ").Append(e).Append('\n').Append(t).Append("\n\n");
        return sb.ToString();
    }

    static string ToText(string text)
    {
        var lines = Cues(text).Select(c => Regex.Replace(c.text, "<[^>]+>", "").Replace("\n", " ").Trim()).Where(l => l.Length > 0);
        var sb = new StringBuilder();
        string? last = null;
        foreach (var l in lines) { if (l != last) sb.AppendLine(l); last = l; }
        return sb.ToString();
    }
}

/// <summary>ZIP/TAR/TGZ creation and extraction (RAR/7z via the tar.exe that ships with Windows 11).</summary>
public static class ArchiveOps
{
    public static string[] TargetsFor(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".zip")) return new[] { "TAR", "TGZ" };
        if (lower.EndsWith(".tar")) return new[] { "ZIP", "TGZ" };
        if (lower.EndsWith(".tgz") || lower.EndsWith(".tar.gz")) return new[] { "ZIP", "TAR" };
        if (lower.EndsWith(".7z") || lower.EndsWith(".rar")) return new[] { "ZIP" };
        return Array.Empty<string>();
    }

    static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var e in new[] { ".tar.gz", ".tgz", ".zip", ".tar", ".gz", ".7z", ".rar" })
            if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return name[..^e.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static Task Extract(string path, OpContext ctx) => Task.Run(() =>
    {
        var dir = Output.Unique(Path.Combine(Path.GetDirectoryName(path)!, BaseName(path)));
        ExtractTo(path, dir);
        ctx.Outputs.Add(dir);
    });

    static void ExtractTo(string path, string dir)
    {
        Directory.CreateDirectory(dir);
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".zip")) ZipFile.ExtractToDirectory(path, dir, overwriteFiles: false);
        else if (lower.EndsWith(".tar")) TarFile.ExtractToDirectory(path, dir, overwriteFiles: false);
        else if (lower.EndsWith(".tgz") || lower.EndsWith(".tar.gz"))
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dir, overwriteFiles: false);
        }
        else if (lower.EndsWith(".gz"))
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var outFs = File.Create(Path.Combine(dir, Path.GetFileNameWithoutExtension(path)));
            gz.CopyTo(outFs);
        }
        else
        {
            var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(tar, $"-xf \"{path}\" -C \"{dir}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "This archive could not be opened." : err.Trim());
        }
    }

    public static Task Convert(string path, string target, OpContext ctx) => Task.Run(() =>
    {
        var temp = Path.Combine(Path.GetTempPath(), "satsuma-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractTo(path, temp);
            var ext = target.ToUpperInvariant() switch { "ZIP" => "zip", "TAR" => "tar", _ => "tar.gz" };
            var output = Output.Unique(Path.Combine(Path.GetDirectoryName(path)!, BaseName(path) + "." + ext));
            Pack(temp, output, includeBaseDir: false);
            ctx.Outputs.Add(output);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    });

    /// <summary>"Compress" tool: zips one or many files/folders.</summary>
    public static Task Zip(OpContext ctx) => Task.Run(() =>
    {
        var first = ctx.Paths[0].TrimEnd('\\');
        var parent = Path.GetDirectoryName(first)!;
        string name = ctx.Paths.Count == 1 ? Path.GetFileNameWithoutExtension(first) : (Path.GetFileName(parent) is { Length: > 0 } parentName ? parentName : "Archive");
        var output = Output.Unique(Path.Combine(parent, name + ".zip"));
        if (ctx.Paths.Count == 1 && Directory.Exists(first))
        {
            ZipFile.CreateFromDirectory(first, output, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        else
        {
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            int i = 0;
            foreach (var p in ctx.Paths)
            {
                if (Directory.Exists(p))
                {
                    var root = Path.GetDirectoryName(p.TrimEnd('\\'))!;
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, Path.GetRelativePath(root, f).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                else zip.CreateEntryFromFile(p, Path.GetFileName(p), CompressionLevel.Optimal);
                ctx.Progress(++i / (double)ctx.Paths.Count);
            }
        }
        ctx.Outputs.Add(output);
    });

    static void Pack(string dir, string output, bool includeBaseDir)
    {
        if (output.EndsWith(".zip")) ZipFile.CreateFromDirectory(dir, output, CompressionLevel.Optimal, includeBaseDir);
        else if (output.EndsWith(".tar")) TarFile.CreateFromDirectory(dir, output, includeBaseDir);
        else
        {
            using var fs = File.Create(output);
            using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            TarFile.CreateFromDirectory(dir, gz, includeBaseDir);
        }
    }
}
