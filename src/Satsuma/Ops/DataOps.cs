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
using System.Xml;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Satsuma.Ops;

/// <summary>Spreadsheets (XLSX/CSV/TSV/JSON/HTML).</summary>
public static class SheetOps
{
    public static readonly string[] Targets = { "XLSX", "CSV", "TSV", "JSON", "HTML" };

    public static Task Convert(string path, string target, OpContext ctx) => Task.Run(() =>
    {
        var rows = Read(path);
        using var output = Output.For(path, target.ToLowerInvariant());
        switch (target.ToUpperInvariant())
        {
            case "XLSX": WriteXlsx(output.TemporaryPath, rows); break;
            case "CSV": File.WriteAllText(output.TemporaryPath, ToDelimited(rows, ','), new UTF8Encoding(true)); break;
            case "TSV": File.WriteAllText(output.TemporaryPath, ToDelimited(rows, '\t'), new UTF8Encoding(true)); break;
            case "JSON": File.WriteAllText(output.TemporaryPath, ToJson(rows), Encoding.UTF8); break;
            case "HTML": File.WriteAllText(output.TemporaryPath, ToHtml(Path.GetFileNameWithoutExtension(path), rows), Encoding.UTF8); break;
        }
        output.Commit(ctx);
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
        int commas = 0, semicolons = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') i++;
                else quoted = !quoted;
            }
            else if (!quoted)
            {
                if (c is '\r' or '\n') break;
                if (c == ',') commas++;
                if (c == ';') semicolons++;
            }
        }
        // German Excel exports use semicolons; quoted content is not a separator.
        return semicolons > commas ? ';' : ',';
    }

    public static List<List<string>> ParseDelimited(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, rowStarted = false;
        for (int i = text.StartsWith('\uFEFF') ? 1 : 0; i < text.Length; i++)
        {
            char c = text[i];
            rowStarted = true;
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
                rowStarted = false;
            }
            else field.Append(c);
        }
        if (quoted) throw new FormatException("The file ends inside a quoted field. Close the quote before converting it.");
        if (rowStarted) { row.Add(field.ToString()); rows.Add(row); }
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
        if (rows.Count > 1048576 || rows.Any(row => row.Count > 16384))
            throw new FormatException("XLSX supports at most 1048576 rows and 16384 columns.");
        if (rows.Any(row => row.Any(value => value.Length > 32767)))
            throw new FormatException("XLSX cells support at most 32767 UTF-16 characters.");

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();
        var styles = workbook.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new S.Stylesheet(
            new S.Fonts(new S.Font(new S.FontSize { Val = 11 }, new S.FontName { Val = "Calibri" }),
                        new S.Font(new S.Bold(), new S.FontSize { Val = 11 }, new S.FontName { Val = "Calibri" })),
            new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                        new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })),
            new S.Borders(new S.Border()),
            new S.CellStyleFormats(new S.CellFormat()),
            new S.CellFormats(new S.CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 49 },
                              new S.CellFormat { FontId = 1, FillId = 0, BorderId = 0, NumberFormatId = 49, ApplyFont = true }),
            new S.CellStyles(new S.CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }));
        var strings = workbook.AddNewPart<SharedStringTablePart>();
        strings.SharedStringTable = new S.SharedStringTable();
        var stringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var sheet = workbook.AddNewPart<WorksheetPart>();
        sheet.Worksheet = new S.Worksheet();
        if (rows.Count > 0)
            sheet.Worksheet.Append(new S.SheetViews(new S.SheetView(new S.Pane {
                VerticalSplit = 1, TopLeftCell = "A2", ActivePane = S.PaneValues.BottomLeft, State = S.PaneStateValues.Frozen
            }) { WorkbookViewId = 0 }));
        int columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        if (columns > 0)
        {
            var widths = new S.Columns();
            for (int c = 0; c < columns; c++)
            {
                int length = rows.Take(200).Where(row => c < row.Count)
                    .SelectMany(row => row[c].Split('\r', '\n')).Select(value => value.Length).DefaultIfEmpty(0).Max();
                widths.Append(new S.Column { Min = (uint)c + 1, Max = (uint)c + 1, Width = Math.Clamp(length + 2, 6, 60), CustomWidth = true });
            }
            sheet.Worksheet.Append(widths);
        }
        var data = sheet.Worksheet.AppendChild(new S.SheetData());
        for (int r = 0; r < rows.Count; r++)
        {
            var row = data.AppendChild(new S.Row { RowIndex = (uint)r + 1 });
            for (int c = 0; c < rows[r].Count; c++)
            {
                string value = rows[r][c];
                if (!stringIndexes.TryGetValue(value, out int index))
                {
                    index = stringIndexes.Count;
                    stringIndexes.Add(value, index);
                    strings.SharedStringTable.Append(new S.SharedStringItem(new S.Text(EncodeXlsxText(value)) { Space = SpaceProcessingModeValues.Preserve }));
                }
                row.Append(new S.Cell {
                    CellReference = XLHelper.GetColumnLetterFromNumber(c + 1) + (r + 1).ToString(CultureInfo.InvariantCulture),
                    DataType = S.CellValues.SharedString, StyleIndex = r == 0 ? 1U : 0U,
                    CellValue = new S.CellValue(index.ToString(CultureInfo.InvariantCulture))
                });
            }
        }
        workbook.Workbook.AppendChild(new S.Sheets()).Append(new S.Sheet { Id = workbook.GetIdOfPart(sheet), SheetId = 1, Name = "Sheet1" });
    }

    // SpreadsheetML ST_Xstring also escapes CR and literal _xHHHH_ sequences, beyond XML escaping.
    static string EncodeXlsxText(string value)
    {
        value = Regex.Replace(value, "_(?=x[0-9A-Fa-f]{4}_)", "_x005F_");
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\r' && XmlConvert.IsXmlChar(c)) result.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                result.Append(c).Append(value[++i]);
            else result.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
        }
        return result.ToString();
    }

    static string ToJson(List<List<string>> rows)
    {
        if (rows.Count == 0) return "[]";
        var header = rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"column{i + 1}" : h).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in header)
            if (!names.Add(name))
                throw new FormatException($"The header '{name}' occurs more than once. JSON needs a unique name for every column.");
        var list = rows.Skip(1).Select((r, index) =>
        {
            if (r.Count > header.Count)
                throw new FormatException($"Row {index + 2} has {r.Count} cells, but the header has {header.Count}. JSON needs a header for every column.");
            var obj = new Dictionary<string, string>();
            for (int i = 0; i < header.Count; i++)
                obj[header[i]] = i < r.Count ? r[i] : "";
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
        using var output = Output.For(path, target.ToLowerInvariant());
        string result = target.ToUpperInvariant() switch
        {
            "VTT" => "WEBVTT\n\n" + Regex.Replace(text.TrimStart('﻿'), @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2"),
            "SRT" => ToSrt(text),
            "TXT" => ToText(text),
            _ => throw new NotSupportedException(target),
        };
        File.WriteAllText(output.TemporaryPath, result.Replace("\n", "\r\n"), Encoding.UTF8);
        output.Commit(ctx);
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
        using var output = Output.FolderAt(Path.Combine(Path.GetDirectoryName(path)!, BaseName(path)));
        ExtractTo(path, output.TemporaryPath);
        output.Commit(ctx);
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
            using var output = Output.FileAt(Path.Combine(Path.GetDirectoryName(path)!, BaseName(path) + "." + ext));
            Pack(temp, output.TemporaryPath, includeBaseDir: false);
            output.Commit(ctx);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    });

    /// <summary>"Compress" tool: zips one or many files/folders.</summary>
    public static Task Zip(OpContext ctx) => Task.Run(() =>
    {
        var first = ctx.Paths[0].TrimEnd('\\');
        var parent = Path.GetDirectoryName(first)!;
        string name = ctx.Paths.Count == 1 ? Path.GetFileNameWithoutExtension(first) : (Path.GetFileName(parent) is { Length: > 0 } parentName ? parentName : "Archive");
        using var output = Output.FileAt(Path.Combine(parent, name + ".zip"));
        if (ctx.Paths.Count == 1 && Directory.Exists(first))
        {
            ZipFile.CreateFromDirectory(first, output.TemporaryPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        else
        {
            using var zip = ZipFile.Open(output.TemporaryPath, ZipArchiveMode.Create);
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
        output.Commit(ctx);
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
