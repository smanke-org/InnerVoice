// A project on disk as one ordinary file, the same as the Mac's ProjectArchive.swift:
//
//   Book.innervoice  (zip, UTF-8 names)
//     project.json
//     Original/<manuscript file>
//     Audio/<sample>.m4a
//
// Entries the Windows app doesn't use are still kept and written back.

using System.IO.Compression;

namespace InnerVoice.Core;

public static class ProjectArchive
{
    public sealed class Contents
    {
        public byte[] ProjectJson { get; set; } = [];
        public (string Name, byte[] Data)? Original { get; set; }
        public Dictionary<string, byte[]> Audio { get; set; } = [];
    }

    public sealed class ArchiveException(string message) : Exception(message);

    public static byte[] Write(Contents contents)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: System.Text.Encoding.UTF8))
        {
            Add(zip, "project.json", contents.ProjectJson);
            if (contents.Original is { } o) Add(zip, "Original/" + SafeName(o.Name), o.Data);
            foreach (var name in contents.Audio.Keys.Order(StringComparer.Ordinal))
                Add(zip, "Audio/" + SafeName(name), contents.Audio[name]);
        }
        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(data);
    }

    public static Contents Read(byte[] data)
    {
        if (!LooksLikeArchive(data)) throw new ArchiveException("This file isn't an InnerVoice project.");
        var contents = new Contents();
        bool found = false;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read, false, System.Text.Encoding.UTF8);
            foreach (var e in zip.Entries)
            {
                var path = e.FullName.Replace('\\', '/');
                if (path.EndsWith('/') || path.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
                byte[] bytes;
                using (var s = e.Open())
                using (var buf = new MemoryStream())
                {
                    s.CopyTo(buf);
                    bytes = buf.ToArray();
                }
                if (path == "project.json") { contents.ProjectJson = bytes; found = true; }
                else if (path.StartsWith("Original/", StringComparison.Ordinal) && contents.Original is null)
                    contents.Original = (path["Original/".Length..], bytes);
                else if (path.StartsWith("Audio/", StringComparison.Ordinal))
                    contents.Audio[path["Audio/".Length..]] = bytes;
            }
        }
        catch (InvalidDataException) { throw new ArchiveException("The project file is damaged."); }
        if (!found) throw new ArchiveException("The project file has no project.json in it.");
        return contents;
    }

    /// True for data that starts like a zip archive.
    public static bool LooksLikeArchive(byte[] data) =>
        data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;

    /// File names inside the archive never climb out of their folder.
    public static string SafeName(string name)
    {
        var last = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        return last is "" or "." or ".." ? "file" : last;
    }
}
