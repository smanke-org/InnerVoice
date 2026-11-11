// Ported from the plain text and Markdown part of Sources/InnerVoiceKit/Import/ManuscriptImporter.swift.

using System.Text;

namespace InnerVoice.Core.Import;

public static class PlainTextReader
{
    static PlainTextReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static List<RawBlock> Read(string path, bool markdown)
    {
        var data = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(StripBom(data, out var enc));
            if (enc is not null) text = enc.GetString(data).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.GetEncoding(1252).GetString(data);
        }
        return Blocks(text, markdown);
    }

    static byte[] StripBom(byte[] data, out Encoding? other)
    {
        other = null;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return data[3..];
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { other = Encoding.Unicode; return []; }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { other = Encoding.BigEndianUnicode; return []; }
        return data;
    }

    /// Paragraphs are separated by blank lines when the file has them (lines inside are
    /// hard-wrapped and get joined); otherwise every line is a paragraph.
    public static List<RawBlock> Blocks(string text, bool markdown)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        int blankCount = lines.Count(l => l.Trim(' ', '\t').Length == 0);
        bool usesBlankLines = blankCount > lines.Length / 6;
        var groups = new List<string>();
        if (usesBlankLines)
        {
            var current = new List<string>();
            foreach (var line in lines)
            {
                var t = line.Trim(' ', '\t');
                if (t.Length == 0)
                {
                    if (current.Count > 0) groups.Add(string.Join(" ", current));
                    current.Clear();
                }
                else if (markdown && t.StartsWith('#'))
                {
                    if (current.Count > 0) groups.Add(string.Join(" ", current));
                    groups.Add(t);
                    current.Clear();
                }
                else current.Add(t);
            }
            if (current.Count > 0) groups.Add(string.Join(" ", current));
        }
        else
        {
            groups = lines.Select(l => l.Trim(' ', '\t')).Where(l => l.Length > 0).ToList();
        }
        return groups.Select(g => markdown ? MarkdownBlock(g) : new RawBlock(g)).ToList();
    }

    public static RawBlock MarkdownBlock(string raw)
    {
        var text = raw;
        int? level = null;
        if (text.StartsWith('#'))
        {
            int hashes = text.TakeWhile(c => c == '#').Count();
            level = Math.Min(hashes, 2);
            text = text[hashes..].Trim(' ', '\t');
        }
        bool quote = false;
        if (text.StartsWith('>'))
        {
            quote = true;
            text = text[1..].Trim(' ', '\t');
        }
        var (plain, italic) = StripEmphasis(text);
        return new RawBlock(plain, italic, level, quote);
    }

    /// Removes *italic*, _italic_ and **bold** markers and returns the italic ranges.
    public static (string Text, List<TextSpan> Italic) StripEmphasis(string text)
    {
        var output = new StringBuilder();
        var italic = new List<TextSpan>();
        int? italicStart = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '*' or '_')
            {
                bool doubled = i + 1 < text.Length && text[i + 1] == c;
                if (doubled) { i += 2; continue; }
                bool prevIsLetter = i > 0 && char.IsLetter(text[i - 1]);
                bool nextIsLetter = i + 1 < text.Length && char.IsLetter(text[i + 1]);
                if (c == '_' && prevIsLetter && nextIsLetter) { output.Append(c); i++; continue; }
                int pos = output.Length;
                if (italicStart is { } start)
                {
                    if (pos > start) italic.Add(new TextSpan(start, pos - start));
                    italicStart = null;
                }
                else if (nextIsLetter || (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])))
                {
                    italicStart = pos;
                }
                else output.Append(c);
                i++;
                continue;
            }
            output.Append(c);
            i++;
        }
        return (output.ToString(), italic);
    }
}
