// Ported from Sources/InnerVoiceKit/BookIndex.swift.

namespace InnerVoice.Core;

/// Fast lookups over a project: where each paragraph is, which lines it holds, who speaks
/// how much. Rebuilt whenever the project changes (linear in the size of the book).
public sealed class BookIndex
{
    public readonly record struct Position(int Chapter, int Paragraph, int Global) : IComparable<Position>
    {
        public int CompareTo(Position other) => Global.CompareTo(other.Global);
        public static bool operator <(Position a, Position b) => a.Global < b.Global;
        public static bool operator >(Position a, Position b) => a.Global > b.Global;
    }

    public sealed class CharacterStats
    {
        public int Lines;
        public int Words;
        public Position? FirstLine;
        public HashSet<int> Chapters = [];
    }

    public IReadOnlyDictionary<Guid, Position> Positions { get; }
    public IReadOnlyDictionary<Guid, List<int>> LinesByParagraph { get; }
    public IReadOnlyDictionary<Guid, int> LineIndexByID { get; }
    public IReadOnlyDictionary<Guid, CharacterStats> Stats { get; }
    public int TotalLines { get; }

    public static readonly BookIndex Empty = new(new Project());

    public BookIndex(Project project)
    {
        var positions = new Dictionary<Guid, Position>();
        var paragraphs = new Dictionary<Guid, Paragraph>();
        int g = 0;
        for (int c = 0; c < project.Chapters.Count; c++)
        {
            var chapter = project.Chapters[c];
            for (int p = 0; p < chapter.Paragraphs.Count; p++)
            {
                var para = chapter.Paragraphs[p];
                positions[para.Id] = new Position(c, p, g++);
                paragraphs.TryAdd(para.Id, para);
            }
        }
        var byParagraph = new Dictionary<Guid, List<int>>();
        var byID = new Dictionary<Guid, int>();
        var stats = new Dictionary<Guid, CharacterStats>();
        for (int i = 0; i < project.Lines.Count; i++)
        {
            var line = project.Lines[i];
            if (!byParagraph.TryGetValue(line.ParagraphID, out var list)) byParagraph[line.ParagraphID] = list = [];
            list.Add(i);
            byID[line.Id] = i;
            if (line.SpeakerID is { } speaker && positions.TryGetValue(line.ParagraphID, out var pos))
            {
                if (!stats.TryGetValue(speaker, out var s)) stats[speaker] = s = new CharacterStats();
                s.Lines++;
                if (paragraphs.TryGetValue(line.ParagraphID, out var para)) s.Words += Words.Count(para.Substring(line.Range));
                if (s.FirstLine is null || pos < s.FirstLine.Value) s.FirstLine = pos;
                s.Chapters.Add(pos.Chapter);
            }
        }
        Positions = positions;
        LinesByParagraph = byParagraph;
        LineIndexByID = byID;
        Stats = stats;
        TotalLines = project.Lines.Count;
    }

    public CharacterStats? StatsFor(Guid id) => Stats.TryGetValue(id, out var s) ? s : null;

    public List<int> LinesIn(Guid paragraphID) => LinesByParagraph.TryGetValue(paragraphID, out var l) ? l : [];

    /// "Ch 3 ¶12" (1-based), the way references read everywhere in the app.
    public string Reference(Guid paragraphID) =>
        Positions.TryGetValue(paragraphID, out var p) ? $"Ch {p.Chapter + 1} ¶{p.Paragraph + 1}" : "—";

    /// Main characters (over 15% of all lines) start hidden in the index.
    public bool ShowsInIndexByDefault(Guid characterID)
    {
        if (TotalLines == 0) return true;
        double share = (double)(StatsFor(characterID)?.Lines ?? 0) / TotalLines;
        return share <= 0.15;
    }

    public bool ShowsInIndex(BookCharacter c) => c.ShowInIndex ?? ShowsInIndexByDefault(c.Id);
}

public static class ProjectIndexExtensions
{
    public static Paragraph? Paragraph(this Project project, Guid id, BookIndex index) =>
        index.Positions.TryGetValue(id, out var p) ? project.Chapters[p.Chapter].Paragraphs[p.Paragraph] : null;

    public static string TextOf(this Project project, Line line, BookIndex index) =>
        project.Paragraph(line.ParagraphID, index)?.Substring(line.Range) ?? "";
}

/// Characters that are probably the same person ("Mara" and "Mara Quinn").
public static class Duplicates
{
    public readonly record struct Pair(Guid Merge, Guid Keep);

    public static List<Pair> Pairs(Project project)
    {
        static HashSet<string> WordsOf(BookCharacter c) =>
            c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Names.Titles.Contains(w)).ToHashSet();
        var output = new List<Pair>();
        foreach (var a in project.Characters)
        {
            foreach (var b in project.Characters)
            {
                if (a.Id == b.Id) continue;
                var wa = WordsOf(a);
                var wb = WordsOf(b);
                if (wa.Count == 1 && wb.Count > 1 && b.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == wa.First())
                    output.Add(new Pair(a.Id, b.Id));
                else if (b.Aliases.Contains(a.Name))
                    output.Add(new Pair(a.Id, b.Id));
            }
        }
        return output;
    }
}
