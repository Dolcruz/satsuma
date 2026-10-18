using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Satsuma.Ops;

/// <summary>Word, RTF, Markdown, plain text and HTML conversions — all offline, no Office required.</summary>
public static class DocOps
{
    public static string[] TargetsFor(FileKind kind) => kind switch
    {
        FileKind.Word => new[] { "MD", "TXT", "HTML" },
        FileKind.Rtf => new[] { "DOCX", "MD", "TXT", "HTML" },
        FileKind.Markdown => new[] { "HTML", "DOCX", "TXT" },
        FileKind.Text => new[] { "DOCX", "MD", "HTML" },
        FileKind.Html => new[] { "MD", "TXT", "DOCX" },
        _ => Array.Empty<string>(),
    };

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static Task Convert(string path, string target, OpContext ctx) => Task.Run(() =>
    {
        var kind = FileKinds.Of(path);
        var title = Path.GetFileNameWithoutExtension(path);
        string markdown = kind switch
        {
            FileKind.Word => DocxToMarkdown(path),
            FileKind.Rtf => TextToMarkdown(RtfToText(path)),
            FileKind.Markdown => File.ReadAllText(path),
            FileKind.Text => TextToMarkdown(File.ReadAllText(path)),
            FileKind.Html => HtmlToMarkdown(File.ReadAllText(path)),
            _ => throw new NotSupportedException(),
        };

        var output = Output.For(path, target.ToLowerInvariant());
        switch (target.ToUpperInvariant())
        {
            case "MD":
                File.WriteAllText(output, kind == FileKind.Text ? File.ReadAllText(path) : markdown, Encoding.UTF8);
                break;
            case "TXT":
                var plain = kind == FileKind.Rtf ? RtfToText(path) : Markdown.ToPlainText(markdown, Pipeline);
                File.WriteAllText(output, plain.Trim() + Environment.NewLine, Encoding.UTF8);
                break;
            case "HTML":
                File.WriteAllText(output, HtmlPage(title, Markdown.ToHtml(markdown, Pipeline)), Encoding.UTF8);
                break;
            case "DOCX":
                WriteDocx(output, BlocksFromMarkdown(markdown));
                break;
            default: throw new NotSupportedException(target);
        }
        ctx.Outputs.Add(output);
    });

    // ---------------- Reading ----------------

    static string TextToMarkdown(string text)
    {
        // Plain text: keep paragraphs, escape nothing fancy. Lines become hard breaks inside paragraphs.
        var paras = text.Replace("\r\n", "\n").Split("\n\n");
        return string.Join("\n\n", paras.Select(p => p.Replace("\n", "  \n")));
    }

    static string RtfToText(string path)
    {
        string result = "";
        var t = new Thread(() =>
        {
            var doc = new System.Windows.Documents.FlowDocument();
            var range = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd);
            using var fs = File.OpenRead(path);
            range.Load(fs, System.Windows.DataFormats.Rtf);
            result = range.Text;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result.Replace("\r\n", "\n");
    }

    static string DocxToMarkdown(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "";
        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            if (el is Paragraph p)
            {
                var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                var text = RunsToMarkdown(p);
                if (string.IsNullOrWhiteSpace(text)) { continue; }
                string prefix = "";
                var m = Regex.Match(style, @"(?i)(?:heading|berschrift)\s*(\d)");
                if (style.Equals("Title", StringComparison.OrdinalIgnoreCase) || style.Equals("Titel", StringComparison.OrdinalIgnoreCase)) prefix = "# ";
                else if (m.Success) prefix = new string('#', Math.Clamp(int.Parse(m.Groups[1].Value), 1, 6)) + " ";
                else if (p.ParagraphProperties?.NumberingProperties != null) prefix = "- ";
                else if (style == "Quote") prefix = "> ";
                else if (style == "Code") { sb.AppendLine("```").AppendLine(text.Replace("  \n", "\n")).AppendLine("```").AppendLine(); continue; }
                if (text.StartsWith("•")) { prefix = "- "; text = text.TrimStart('•', '\t', ' '); }
                sb.Append(prefix).AppendLine(text).AppendLine();
            }
            else if (el is Table table)
            {
                var rows = table.Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText.Replace("|", "\\|").Trim()).ToList()).ToList();
                if (rows.Count == 0) continue;
                int cols = rows.Max(r => r.Count);
                foreach (var (row, i) in rows.Select((r, i) => (r, i)))
                {
                    while (row.Count < cols) row.Add("");
                    sb.AppendLine("| " + string.Join(" | ", row) + " |");
                    if (i == 0) sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", cols)));
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    static string RunsToMarkdown(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var run in p.Descendants<Run>())
        {
            var text = string.Concat(run.ChildElements.Select(c => c switch
            {
                Text t => t.Text,
                TabChar => "\t",
                Break => "  \n",
                _ => "",
            }));
            if (text.Length == 0) continue;
            bool bold = run.RunProperties?.Bold != null && (run.RunProperties.Bold.Val?.Value ?? true);
            bool italic = run.RunProperties?.Italic != null && (run.RunProperties.Italic.Val?.Value ?? true);
            if (bold && text.Trim().Length > 0) text = $"**{text}**";
            if (italic && text.Trim().Length > 0) text = $"*{text}*";
            sb.Append(text);
        }
        return sb.ToString().Replace("****", "");
    }

    static string HtmlToMarkdown(string html)
    {
        var s = Regex.Replace(html, @"(?is)<(script|style|head)[^>]*>.*?</\1>", "");
        s = Regex.Replace(s, @"(?is)<h([1-6])[^>]*>(.*?)</h\1>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + m.Groups[2].Value + "\n\n");
        s = Regex.Replace(s, @"(?is)<(strong|b)\b[^>]*>(.*?)</\1>", "**$2**");
        s = Regex.Replace(s, @"(?is)<(em|i)\b[^>]*>(.*?)</\1>", "*$2*");
        s = Regex.Replace(s, @"(?is)<a\b[^>]*href=""([^""]*)""[^>]*>(.*?)</a>", "[$2]($1)");
        s = Regex.Replace(s, @"(?is)<li\b[^>]*>", "\n- ");
        s = Regex.Replace(s, @"(?is)<br\s*/?>", "  \n");
        s = Regex.Replace(s, @"(?is)</(p|div|ul|ol|table|tr|blockquote|pre)>", "\n\n");
        s = Regex.Replace(s, @"(?is)<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim() + "\n";
    }

    // ---------------- Writing ----------------

    public enum BlockType { Heading, Paragraph, Bullet, Numbered, Code, Quote }

    public sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false);

    public sealed record Block(BlockType Type, List<Span> Spans, int Level = 0);

    public static List<Block> BlocksFromMarkdown(string markdown)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var blocks = new List<Block>();
        foreach (var b in doc) AddBlock(b, blocks, 0);
        return blocks;
    }

    static void AddBlock(Markdig.Syntax.Block b, List<Block> into, int listDepth, bool numbered = false, int number = 0)
    {
        switch (b)
        {
            case HeadingBlock h:
                into.Add(new Block(BlockType.Heading, Spans(h.Inline), h.Level));
                break;
            case ParagraphBlock p:
                into.Add(new Block(BlockType.Paragraph, Spans(p.Inline)));
                break;
            case FencedCodeBlock or CodeBlock:
                var code = (LeafBlock)b;
                into.Add(new Block(BlockType.Code, new List<Span> { new(code.Lines.ToString(), Code: true) }));
                break;
            case QuoteBlock q:
                foreach (var child in q)
                    if (child is ParagraphBlock qp) into.Add(new Block(BlockType.Quote, Spans(qp.Inline)));
                break;
            case ListBlock list:
                int n = 1;
                foreach (var item in list)
                {
                    if (item is not ListItemBlock li) continue;
                    bool first = true;
                    foreach (var child in li)
                    {
                        if (child is ParagraphBlock lp && first)
                        {
                            into.Add(new Block(list.IsOrdered ? BlockType.Numbered : BlockType.Bullet, Spans(lp.Inline), listDepth) { });
                            if (list.IsOrdered) into[^1].Spans.Insert(0, new Span($"{n}. "));
                            first = false;
                        }
                        else AddBlock(child, into, listDepth + 1);
                    }
                    n++;
                }
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(child, into, listDepth);
                break;
        }
    }

    static List<Span> Spans(ContainerInline? inline, bool bold = false, bool italic = false)
    {
        var list = new List<Span>();
        if (inline == null) return list;
        foreach (var i in inline)
        {
            switch (i)
            {
                case LiteralInline lit: list.Add(new Span(lit.Content.ToString(), bold, italic)); break;
                case CodeInline ci: list.Add(new Span(ci.Content, bold, italic, true)); break;
                case LineBreakInline: list.Add(new Span(" ", bold, italic)); break;
                case EmphasisInline em:
                    list.AddRange(Spans(em, bold || em.DelimiterCount >= 2, italic || em.DelimiterCount == 1));
                    break;
                case ContainerInline ci2: list.AddRange(Spans(ci2, bold, italic)); break;
                case HtmlEntityInline ent: list.Add(new Span(ent.Transcoded.ToString(), bold, italic)); break;
            }
        }
        return list;
    }

    public static void WriteDocx(string path, List<Block> blocks)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = BuildStyles();
        var body = new Body();

        foreach (var b in blocks)
        {
            var pPr = new ParagraphProperties();
            switch (b.Type)
            {
                case BlockType.Heading: pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Heading" + Math.Clamp(b.Level, 1, 3) }; break;
                case BlockType.Code: pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Code" }; break;
                case BlockType.Quote: pPr.ParagraphStyleId = new ParagraphStyleId { Val = "Quote" }; break;
                case BlockType.Bullet:
                case BlockType.Numbered:
                    pPr.Indentation = new Indentation { Left = (360 + 360 * b.Level).ToString(), Hanging = "360" };
                    pPr.SpacingBetweenLines = new SpacingBetweenLines { After = "60" };
                    break;
            }
            var para = new Paragraph(pPr);
            if (b.Type == BlockType.Bullet) para.Append(new Run(new Text("•\t") { Space = SpaceProcessingModeValues.Preserve }));

            if (b.Type == BlockType.Code)
            {
                var lines = b.Spans[0].Text.Replace("\r", "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var run = new Run(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
                    if (i < lines.Length - 1) run.Append(new Break());
                    para.Append(run);
                }
            }
            else
            {
                foreach (var s in b.Spans)
                {
                    var rPr = new RunProperties();
                    if (s.Bold) rPr.Bold = new Bold();
                    if (s.Italic) rPr.Italic = new Italic();
                    if (s.Code) rPr.RunFonts = new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" };
                    para.Append(new Run(rPr, new Text(s.Text) { Space = SpaceProcessingModeValues.Preserve }));
                }
            }
            body.Append(para);
        }
        body.Append(new SectionProperties(new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U }));
        main.Document = new Document(body);
        main.Document.Save();
    }

    static Styles BuildStyles()
    {
        Style Para(string id, string name, string? font, int sizeHalfPts, bool bold, string color, int before, int after) =>
            new(new StyleName { Val = name },
                new BasedOn { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() }, new KeepNext()),
                new StyleRunProperties(
                    font != null ? new RunFonts { Ascii = font, HighAnsi = font } : new RunFonts(),
                    bold ? new Bold() : new Bold { Val = false },
                    new Color { Val = color },
                    new FontSize { Val = sizeHalfPts.ToString() }))
            { Type = StyleValues.Paragraph, StyleId = id };

        var normal = new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "160", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" }, new FontSize { Val = "22" }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true };

        var code = Para("Code", "Code", "Consolas", 19, false, "333333", 0, 160);
        code.StyleParagraphProperties!.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F3F3" });
        var quote = Para("Quote", "Quote", null, 22, false, "555555", 0, 160);
        quote.StyleParagraphProperties!.Append(new Indentation { Left = "567" });

        return new Styles(
            normal,
            Para("Heading1", "heading 1", "Segoe UI Semibold", 36, true, "1F1F1F", 360, 120),
            Para("Heading2", "heading 2", "Segoe UI Semibold", 28, true, "1F1F1F", 280, 100),
            Para("Heading3", "heading 3", "Segoe UI Semibold", 24, true, "1F1F1F", 240, 80),
            code, quote);
    }

    public static string HtmlPage(string title, string bodyHtml) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
          :root { color-scheme: light dark; --fg:#1b1b1b; --bg:#ffffff; --muted:#5f5f5f; --line:#e5e5e5; --code:#f4f4f4; }
          @media (prefers-color-scheme: dark) { :root { --fg:#ececec; --bg:#1c1c1c; --muted:#a8a8a8; --line:#333; --code:#262626; } }
          body { margin:0; background:var(--bg); color:var(--fg); font:16px/1.65 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif; }
          main { max-width:760px; margin:0 auto; padding:56px 24px 96px; }
          h1,h2,h3 { font-family:"Segoe UI Variable Display","Segoe UI",system-ui,sans-serif; line-height:1.25; }
          h1 { font-size:2.1em; margin-top:0; }
          pre, code { font-family:"Cascadia Code",Consolas,monospace; background:var(--code); border-radius:6px; }
          code { padding:.1em .35em; } pre { padding:14px 16px; overflow:auto; } pre code { padding:0; }
          blockquote { margin:0; padding-left:16px; border-left:3px solid var(--line); color:var(--muted); }
          table { border-collapse:collapse; } td,th { border:1px solid var(--line); padding:6px 10px; }
          img { max-width:100%; } a { color:#0067c0; }
        </style>
        </head>
        <body><main>
        {{bodyHtml}}
        </main></body>
        </html>
        """;
}
