// Ported from Sources/InnerVoiceKit/Import/ManuscriptImporter.swift.

namespace InnerVoice.Core.Import;

public sealed class ImportException(string message) : Exception(message);

/// Reads a manuscript file into chapters and paragraphs.
public static partial class ManuscriptImporter
{
    public static readonly string[] FileExtensions =
        ["docx", "pdf", "rtf", "txt", "text", "md", "markdown", "odt", "pages", "epub", "html", "htm", "xhtml"];

    public static string Unsupported(string name) =>
        $"InnerVoice can't open “{name}”. Save it as Word, PDF, RTF, plain text, Markdown, OpenDocument, EPUB or HTML, then try again.";

    public static ImportedBook Read(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(path);
        var author = "";
        List<RawBlock> blocks;
        List<Chapter>? fromTOC = null;
        switch (ext)
        {
            case "txt" or "text": blocks = PlainTextReader.Read(path, markdown: false); break;
            case "md" or "markdown": blocks = PlainTextReader.Read(path, markdown: true); break;
            case "doc": throw new ImportException($"InnerVoice can't open “{name}”, an older Word file. In Word, choose File › Save As › Word Document (.docx), then open that.");
            case "pages": blocks = PagesReader.Read(path); break;
            default:
                var other = ReadOther(path, ext) ?? throw new ImportException(Unsupported(name));
                blocks = other.Blocks;
                if (other.Title is { Length: > 0 } t) title = t;
                author = other.Author ?? "";
                fromTOC = other.Chapters;
                break;
        }
        var chapters = fromTOC ?? ChapterDetector.Chapters(blocks, title);
        if (chapters.Count == 0) throw new ImportException($"“{name}” has no text in it.");
        return new ImportedBook(title, author, chapters);
    }

    /// `Chapters`: from the book's own table of contents (EPUB), used instead of finding them.
    public sealed record OtherResult(List<RawBlock> Blocks, string? Title = null, string? Author = null, List<Chapter>? Chapters = null);

    /// Word, PDF, RTF, OpenDocument, EPUB and HTML (Import/Readers).
    private static partial OtherResult? ReadOther(string path, string ext);
}
