// Ported from PagesReader in Sources/InnerVoiceKit/Import/ManuscriptImporter.swift.

using System.IO.Compression;

namespace InnerVoice.Core.Import;

/// Pages files keep their text in a private format. When the document was saved with a
/// preview, that PDF has the full text. A .pages file is a zip; one copied off a Mac as a
/// package arrives as a folder, read the same way.
public static class PagesReader
{
    static readonly string[] Previews = ["QuickLook/Preview.pdf", "preview.pdf"];

    public static string CantRead(string name) =>
        $"Unable to read “{name}”. In Pages, choose File › Export To › Word, then open the Word file.";

    public static List<RawBlock> Read(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        foreach (var candidate in Previews)
        {
            // Like the Mac's `try?`: a preview that won't read (scanned, damaged) falls through
            // to the next one, then to the message.
            try
            {
                if (Preview(path, candidate) is { } blocks && blocks.Count > 3) return blocks;
            }
            catch (Exception e) when (e is ImportException or InvalidDataException or IOException) { }
        }
        throw new ImportException(CantRead(name));
    }

    static List<RawBlock>? Preview(string path, string candidate)
    {
        if (Directory.Exists(path))
        {
            var file = Path.Combine(path, candidate.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? PdfReader.Read(file) : null;
        }
        using var zip = ZipFile.OpenRead(path);
        var data = Zip.Entry(zip, candidate);
        if (data is null) return null;
        var temp = Path.Combine(Path.GetTempPath(), $"iv-pages-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(temp, data);
            return PdfReader.Read(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
