using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Paths to the shared fixtures in windows/Fixtures (written by the Mac's Kit tests).
public static class Fixtures
{
    public static string Folder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures"))) dir = dir.Parent;
            return dir is null ? throw new DirectoryNotFoundException("windows/Fixtures not found") : Path.Combine(dir.FullName, "Fixtures");
        }
    }

    public static string PathOf(string name) => Path.Combine(Folder, name);

    /// Set INNERVOICE_WRITE_FIXTURES=1 to (re)write the fixtures the Windows side makes.
    public static bool ShouldWrite => Environment.GetEnvironmentVariable("INNERVOICE_WRITE_FIXTURES") is { Length: > 0 };

    public static JsonNode Json(string name) => JsonNode.Parse(File.ReadAllText(PathOf(name)))!;

    /// The sample story the Mac fixtures are made from (the Mac's SeedData story).
    public static string SampleMarkdown => SampleStory.Markdown;

    public static Project SampleProject()
    {
        var blocks = PlainTextReader.Blocks(SampleMarkdown, markdown: true);
        var chapters = ChapterDetector.Chapters(blocks, "Sample Story");
        return ProjectBuilder.Make(new ImportedBook("Sample Story", "A. N. Example", chapters), "Sample Story.md");
    }
}
