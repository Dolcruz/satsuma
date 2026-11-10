using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Satsuma.Ops;

namespace Satsuma.Core;

/// <summary>Checks spreadsheet content, including values that cannot be represented as doubles.</summary>
public static class SheetSelfTest
{
    public static async Task<int> Run(string dir, StringBuilder report)
    {
        Directory.CreateDirectory(dir);
        int failures = 0;

        async Task Check(string name, Func<Task> test)
        {
            try
            {
                await test();
                report.AppendLine($"ok    sheet content  {name}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"FAIL  sheet content  {name}: {ex.Message}");
            }
        }

        async Task<OpContext> Convert(string name, string text, string target)
        {
            var source = Path.Combine(dir, name + ".csv");
            File.WriteAllText(source, text, new UTF8Encoding(true));
            var ctx = new OpContext(new[] { source }, null, null, default);
            await SheetOps.Convert(source, target, ctx);
            Require(ctx.Outputs.Count == 1, "Expected one output.");
            return ctx;
        }

        async Task RejectJson(string name, string text)
        {
            var source = Path.Combine(dir, name + ".csv");
            File.WriteAllText(source, text);
            var ctx = new OpContext(new[] { source }, null, null, default);
            try { await SheetOps.Convert(source, "JSON", ctx); }
            catch (FormatException)
            {
                Require(ctx.Outputs.Count == 0 && !File.Exists(Path.ChangeExtension(source, "json")),
                    "Rejected input must not create a JSON output.");
                return;
            }
            throw new Exception("Expected an explicit error instead of losing a column.");
        }

        await Check("empty quoted last row", () =>
        {
            var rows = SheetOps.ParseDelimited("name\r\n\"\"", ',');
            Require(rows.Count == 2 && rows[1].SequenceEqual(new[] { "" }), "The empty quoted row was lost.");
            return Task.CompletedTask;
        });
        await Check("empty quoted file", () =>
        {
            var rows = SheetOps.ParseDelimited("\"\"", ',');
            Require(rows.Count == 1 && rows[0].SequenceEqual(new[] { "" }), "The empty quoted field was lost.");
            return Task.CompletedTask;
        });
        await Check("unclosed quoted field", () =>
        {
            try { SheetOps.ParseDelimited("name\r\n\"unfinished", ','); }
            catch (FormatException) { return Task.CompletedTask; }
            throw new Exception("An unclosed quoted field must be rejected.");
        });
        await Check("BOM, escaped quotes, multiline fields and trailing cells", () =>
        {
            var rows = SheetOps.ParseDelimited("\uFEFF\"a,\"\"b\"\"\",tail,\r\n\"line1\r\nline2\",\"\",", ',');
            Require(rows.Count == 2 && rows[0].SequenceEqual(new[] { "a,\"b\"", "tail", "" }) &&
                rows[1].SequenceEqual(new[] { "line1\r\nline2", "", "" }), "Quoted values changed during parsing.");
            return Task.CompletedTask;
        });
        await Check("quoted semicolons in CSV header", async () =>
        {
            var ctx = await Convert("comma", "\"Name;with;semicolons\",Value\r\nAnna,42\r\n", "TSV");
            Require(File.ReadAllText(ctx.Outputs[0]) == "Name;with;semicolons\tValue\r\nAnna\t42\r\n",
                "Quoted semicolons were mistaken for separators.");
        });
        await Check("quoted commas and newline in semicolon header", async () =>
        {
            var ctx = await Convert("semicolon", "\"Name,with,commas\nand a newline\";Value\r\nAnna;42\r\n", "TSV");
            Require(File.ReadAllText(ctx.Outputs[0]) == "\"Name,with,commas\nand a newline\"\tValue\r\nAnna\t42\r\n",
                "Quoted commas or newline changed delimiter detection.");
        });

        string[] values = { "9007199254740993", "1.2300", "00123", "1e309", "NaN", "Infinity", "-0", "=SUM(A1:A2)", " 42 ", "0.123456789012345678901234567890", "'quoted", "TRUE", "2026-11-10" };
        string content = "value\r\n" + string.Join("\r\n", values) + "\r\n";
        await Check("JSON preserves exact text", async () =>
        {
            var ctx = await Convert("values-json", content, "JSON");
            using var json = JsonDocument.Parse(File.ReadAllText(ctx.Outputs[0]));
            var actual = json.RootElement.EnumerateArray().Select(r => r.GetProperty("value")).ToArray();
            Require(actual.Length == values.Length && actual.All(v => v.ValueKind == JsonValueKind.String) &&
                actual.Select(v => v.GetString()).SequenceEqual(values), "JSON changed a cell's value or type.");
        });
        await Check("XLSX preserves exact text", async () =>
        {
            var ctx = await Convert("values-xlsx", content, "XLSX");
            using var workbook = new XLWorkbook(ctx.Outputs[0]);
            var sheet = workbook.Worksheet(1);
            for (int i = 0; i < values.Length; i++)
                Require(sheet.Cell(i + 2, 1).DataType == XLDataType.Text && sheet.Cell(i + 2, 1).GetString() == values[i],
                    $"XLSX changed row {i + 2}.");
            var roundtrip = new OpContext(ctx.Outputs, null, null, default);
            await SheetOps.Convert(ctx.Outputs[0], "JSON", roundtrip);
            using var json = JsonDocument.Parse(File.ReadAllText(roundtrip.Outputs.Single()));
            Require(json.RootElement.EnumerateArray().Select(r => r.GetProperty("value").GetString()).SequenceEqual(values),
                "XLSX to JSON changed a cell's text.");
        });
        await Check("duplicate JSON headers", () => RejectJson("duplicate", "name,name\r\nfirst,second\r\n"));
        await Check("generated JSON header collision", () => RejectJson("collision", "column2,\r\nfirst,second\r\n"));
        await Check("wide JSON row", () => RejectJson("wide", "name,value\r\nfirst,second,third\r\n"));
        await Check("blank JSON header and short row", async () =>
        {
            var ctx = await Convert("short", "name,\r\nfirst,second\r\nthird\r\n", "JSON");
            using var json = JsonDocument.Parse(File.ReadAllText(ctx.Outputs[0]));
            Require(json.RootElement[0].GetProperty("column2").GetString() == "second" &&
                json.RootElement[1].GetProperty("name").GetString() == "third" &&
                json.RootElement[1].GetProperty("column2").GetString() == "", "Blank header or short-row mapping changed.");
        });
        return failures;
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
