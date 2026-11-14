// Importing and exporting the computer's list, ported from PronunciationFile in
// Sources/InnerVoiceKit/PronunciationLists.swift. A file made on the Mac opens here and the other
// way round (PronunciationFileTests compares with the Mac's own output in Fixtures/pronunciations).
// - CSV (export and import): `Word,Say It As,Match Case,Notes`, a header row first, quoted as
//   CSV does, CRLF line ends, UTF-8; opens in Excel and Numbers.
// - TSV (import): the same with tabs.
// - Text (import): one `word = how to say it` a line (also `→`, `->`, a tab, `: `); `#` starts a
//   comment.
// - JSON (export and import): `{"format":"InnerVoice Pronunciations","version":1,"entries":[…]}`,
//   or a bare array of entries, or a `{listID, entries}` list.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InnerVoice.Core;

public sealed class PronunciationFileException(PronunciationFileException.Problem kind, string? why = null)
    : Exception(kind == Problem.Empty ? "The file has no pronunciations in it." : $"InnerVoice can’t read this file: {why}")
{
    public enum Problem { Empty, Unreadable }
    public Problem Kind { get; } = kind;
}

public static class PronunciationFile
{
    public enum Format { Csv, Json }

    public const string FormatName = "InnerVoice Pronunciations";

    /// The format a file name asks for by its extension (.csv or .json), else null.
    public static Format? FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => Format.Csv,
        ".json" => Format.Json,
        _ => null,
    };

    sealed class JsonFile
    {
        [JsonRequired] public string Format { get; set; } = "";
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public List<ListedPronunciation> Entries { get; set; } = [];
    }

    static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = ProjectJson.Options.Encoder,
    };

    public static byte[] Export(IEnumerable<ListedPronunciation> entries, Format format)
    {
        var sorted = PronunciationText.SortedByTerm(entries).Select(e => { var o = e.Copy(); o.List = null; return o; }).ToList();
        switch (format)
        {
            case Format.Json:
                var node = JsonSerializer.SerializeToNode(new JsonFile { Format = FormatName, Version = 1, Entries = sorted }, ProjectJson.Options)!;
                return JsonSerializer.SerializeToUtf8Bytes(ProjectJson.SortKeys(node), PrettyOptions);
            default:
                var lines = new List<string> { "Word,Say It As,Match Case,Notes" };
                foreach (var e in sorted)
                    lines.Add(string.Join(",", new[] { e.Term, e.SayItAs, e.MatchCase ? "Yes" : "", e.Notes }.Select(CsvField)));
                return new UTF8Encoding(false).GetBytes(string.Join("\r\n", lines) + "\r\n");
        }
    }

    /// Reads a file by its contents (and its name's extension as a hint). Throws
    /// PronunciationFileException when there's nothing to read.
    public static List<ListedPronunciation> Read(byte[] data, string fileName = "")
    {
        var text = Encoding.UTF8.GetString(data);
        if (text.StartsWith('﻿')) text = text[1..];
        var trimmed = text.Trim();
        if (trimmed.Length == 0) throw new PronunciationFileException(PronunciationFileException.Problem.Empty);
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        List<ListedPronunciation> entries;
        if (ext == "json" || trimmed.StartsWith('{') || trimmed.StartsWith('['))
            entries = ReadJson(trimmed) ?? throw new PronunciationFileException(PronunciationFileException.Problem.Unreadable, "it isn’t a pronunciations file.");
        else if (ext == "csv" || ext == "tsv" || LooksLikeCsv(trimmed))
            entries = ReadCsv(trimmed, ext == "tsv" ? '\t' : ',');
        else
            entries = ReadText(trimmed);
        var usable = entries.Where(e => e.IsUsable).Select(e => { var o = e.Copy(); o.List = null; return o; }).ToList();
        if (usable.Count == 0) throw new PronunciationFileException(PronunciationFileException.Problem.Empty);
        return usable;
    }

    static List<ListedPronunciation>? ReadJson(string text)
    {
        T? Try<T>() where T : class
        {
            try { return JsonSerializer.Deserialize<T>(text, ProjectJson.Options); } catch { return null; }
        }
        if (Try<JsonFile>() is { } file) return file.Entries;
        if (Try<List<ListedPronunciation>>() is { } list) return list;
        if (Try<PronunciationList>() is { } computer) return computer.Entries;
        return null;
    }

    internal static bool LooksLikeCsv(string text)
    {
        var first = text.Split('\n', '\r').FirstOrDefault(l => l.Length > 0)?.ToLowerInvariant() ?? "";
        return first.StartsWith("word,", StringComparison.Ordinal) || first.StartsWith("\"word\",", StringComparison.Ordinal)
            || first.StartsWith("term,", StringComparison.Ordinal);
    }

    internal static string CsvField(string s)
    {
        if (s.IndexOfAny([',', '"', '\n', '\r']) < 0 && !s.StartsWith(' ') && !s.EndsWith(' ')) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// CSV rows, quotes and line breaks inside quotes included. A CRLF is one line break, like
    /// a Swift Character.
    internal static List<List<string>> CsvRows(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        void EndRow()
        {
            row.Add(field.ToString()); field.Clear();
            if (row.Any(f => f.Length > 0)) rows.Add(row);
            row = [];
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
            }
            else field.Append(c);
        }
        EndRow();
        return rows;
    }

    internal static List<ListedPronunciation> ReadCsv(string text, char separator)
    {
        var rows = CsvRows(text, separator);
        if (rows.Count == 0) return [];
        // Columns by the header when there is one, else Word, Say It As, Match Case, Notes.
        var header = rows[0].Select(h => PronunciationText.Trim(h).ToLowerInvariant()).ToList();
        int term = 0, say = 1, match = 2, notes = 3;
        string[] termNames = ["word", "term"];
        if (header.Any(termNames.Contains))
        {
            term = Math.Max(0, header.FindIndex(termNames.Contains));
            int s = header.FindIndex(h => h is "say it as" or "sayitas" or "pronunciation" or "say as" or "spoken as");
            say = s >= 0 ? s : 1;
            match = header.FindIndex(h => h is "match case" or "matchcase" or "case");
            notes = header.FindIndex(h => h is "notes" or "note");
            rows.RemoveAt(0);
        }
        static string At(List<string> r, int i) => i >= 0 && i < r.Count ? PronunciationText.Trim(r[i]) : "";
        return rows.Select(r =>
        {
            var m = At(r, match).ToLowerInvariant();
            return new ListedPronunciation(At(r, term), At(r, say), At(r, notes), m is "yes" or "true" or "1" or "y" or "x");
        }).ToList();
    }

    static readonly string[] TextSeparators = [" = ", "=", " → ", "→", " -> ", "->", "\t", ": "];

    internal static List<ListedPronunciation> ReadText(string text)
    {
        var output = new List<ListedPronunciation>();
        foreach (var raw in text.Split('\n', '\r'))
        {
            var line = PronunciationText.Trim(raw);
            if (line.Length == 0 || line.StartsWith('#')) continue;
            foreach (var sep in TextSeparators)
            {
                int at = line.IndexOf(sep, StringComparison.Ordinal);
                if (at < 0) continue;
                output.Add(new ListedPronunciation(PronunciationText.Trim(line[..at]), PronunciationText.Trim(line[(at + sep.Length)..])));
                break;
            }
        }
        return output;
    }
}
