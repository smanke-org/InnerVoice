// Ported from Sources/InnerVoice/Export/Exporter.swift.
//
// The Mac builds one NSAttributedString and hands it to TextKit (PDF) and to
// NSAttributedString's Office writer (Word). Here `Build` makes the same list of styled
// paragraphs once, and `Pdf` (QuestPDF) and `Word` (OpenXml) each draw that list.

using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using InnerVoice.App.Document;
using InnerVoice.Core;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace InnerVoice.App.Export;

/// Narrator Scripts, the tagged manuscript, the cast list and the pronunciation guide,
/// as PDF, Word or CSV.
public static class Exporter
{
    public enum Kind { NarratorScript, Manuscript, Cast, Pronunciations }

    public enum Format { Pdf, Word, Csv }

    public static readonly Kind[] Kinds = [Kind.NarratorScript, Kind.Manuscript, Kind.Cast, Kind.Pronunciations];

    /// What File › Export… offers for the project type, as on the Mac: Narrator Scripts
    /// belong to narration, the pronunciation guide to books with AI voices.
    public static List<Kind> KindsShown(Project project) =>
        [.. Kinds.Where(k => (k != Kind.NarratorScript || project.ShowsNarrationTools) && (k != Kind.Pronunciations || project.ShowsPronunciations))];

    public static string Title(this Kind k) => k switch
    {
        Kind.NarratorScript => "Narrator Script",
        Kind.Manuscript => "Tagged Manuscript",
        Kind.Cast => "Cast List",
        _ => "Pronunciation Guide",
    };

    public static string Detail(this Kind k) => k switch
    {
        Kind.NarratorScript => "One narrator's lines, each with the cue line before it, and their characters' summaries.",
        Kind.Manuscript => "The whole book with colors and initials, and a character key. For printing or reading on an iPad.",
        Kind.Cast => "Every character with their summary, voice notes, narrator and line counts.",
        _ => "Every term with how to say it and your notes.",
    };

    public static Format[] Formats(Kind k) => k switch
    {
        Kind.NarratorScript or Kind.Manuscript => [Format.Pdf, Format.Word],
        _ => [Format.Pdf, Format.Word, Format.Csv],
    };

    public static string Title(this Format f) => f switch
    {
        Format.Pdf => "PDF",
        Format.Word => "Word",
        _ => "CSV",
    };

    public static string Extension(this Format f) => f switch
    {
        Format.Pdf => "pdf",
        Format.Word => "docx",
        _ => "csv",
    };

    public sealed class Options
    {
        public Kind Kind { get; set; } = Kind.NarratorScript;
        public Format Format { get; set; } = Format.Pdf;
        /// null: every character (no narrators set up yet).
        public Guid? NarratorID { get; set; }
        public bool IncludeCues { get; set; } = true;
        public bool IncludeSummaries { get; set; } = true;
    }

    // MARK: Styled text

    public enum Weight { Regular, SemiBold, Bold }

    /// An sRGB color with alpha (0…1), like the NSColors the Mac uses.
    public readonly record struct Rgba(byte R, byte G, byte B, double A = 1)
    {
        public static readonly Rgba Black = new(0, 0, 0);
        public static readonly Rgba Gray = new(102, 102, 102); // NSColor(white: 0.4)

        public static Rgba Hex(string? hex, string fallback = "#888888")
        {
            var rgb = CharacterPalette.Rgb(hex ?? "") ?? CharacterPalette.Rgb(fallback)!.Value;
            return new((byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255));
        }

        public Rgba WithAlpha(double a) => this with { A = a };

        /// NSColor.blended(withFraction: 0.35, of: .black).
        public Rgba Darker() => new((byte)Math.Round(R * 0.65), (byte)Math.Round(G * 0.65), (byte)Math.Round(B * 0.65), A);

        /// Painted over white paper (Word has no alpha).
        public Rgba OnWhite() => new((byte)Math.Round(R * A + 255 * (1 - A)), (byte)Math.Round(G * A + 255 * (1 - A)), (byte)Math.Round(B * A + 255 * (1 - A)));

        public string RRGGBB => $"{R:X2}{G:X2}{B:X2}";
        public string AARRGGBB => $"{(byte)Math.Round(Math.Clamp(A, 0, 1) * 255):X2}{R:X2}{G:X2}{B:X2}";
    }

    public sealed record Run(string Text, float Size, Weight Weight, Rgba Color, Rgba? Background = null, bool Dashed = false);

    /// One paragraph: its runs, the space after it and its left indent (points).
    public sealed class Para
    {
        public List<Run> Runs { get; } = [];
        public float SpacingAfter { get; init; }
        public float Indent { get; init; }
        public string PlainText => string.Concat(Runs.Select(r => r.Text));
    }

    // The Mac's fonts: body Georgia 11 (sans serif here), small 8.5, h1 20 bold, h2 14 and h3 11 semibold.
    const float Body = 11, Small = 8.5f, H1 = 20, H2 = 14, H3 = 11;
    const float LineHeightMultiple = 1.15f;

    static Run BodyRun(string s, Rgba? color = null) => new(s, Body, Weight.Regular, color ?? Rgba.Black);
    static Run SmallRun(string s, Rgba? color = null) => new(s, Small, Weight.Regular, color ?? Rgba.Black);

    static Para Text(string s, float size, Weight weight = Weight.Regular, Rgba? color = null, float spacing = 6, float indent = 0)
    {
        var p = new Para { SpacingAfter = spacing, Indent = indent };
        if (s.Length > 0) p.Runs.Add(new Run(s, size, weight, color ?? Rgba.Black));
        return p;
    }

    static int Lines(BookIndex index, Guid id) => index.StatsFor(id)?.Lines ?? 0;

    public static List<Para> Build(Options o, ProjectDocument document)
    {
        var project = document.Project;
        var index = document.Index;
        var output = new List<Para>();
        var gray = Rgba.Gray;
        switch (o.Kind)
        {
            case Kind.NarratorScript:
            {
                var narrator = project.Narrators.FirstOrDefault(n => n.Id == o.NarratorID);
                var cast = project.Characters.Where(c => o.NarratorID is null || document.EffectiveNarrator(c)?.Id == o.NarratorID).ToList();
                var ids = cast.Select(c => c.Id).ToHashSet();
                output.Add(Text(project.Title, H1, Weight.Bold, spacing: 4));
                output.Add(Text("Narrator Script" + (narrator is null ? "" : $" · {narrator.Name}"), H2, Weight.SemiBold, gray, spacing: 16));
                if (o.IncludeSummaries)
                    foreach (var c in cast) output.AddRange(CharacterBlock(c, document));
                int lastChapter = -1;
                for (int i = 0; i < project.Lines.Count; i++)
                {
                    var line = project.Lines[i];
                    if (line.SpeakerID is not { } speaker || !ids.Contains(speaker)) continue;
                    if (index.Positions.TryGetValue(line.ParagraphID, out var pos) && pos.Chapter != lastChapter)
                    {
                        lastChapter = pos.Chapter;
                        output.Add(Text(project.Chapters[pos.Chapter].ShownTitle(), H2, Weight.SemiBold, spacing: 8));
                        if (project.Chapters[pos.Chapter].RecapText is { } recap)
                            output.Add(Text($"Previously… {recap}", Body, color: gray, spacing: 10));
                    }
                    if (o.IncludeCues && i > 0)
                    {
                        var cue = project.Lines[i - 1];
                        if (cue.SpeakerID is not { } cueSpeaker || !ids.Contains(cueSpeaker))
                        {
                            var who = project.Character(cue.SpeakerID)?.Name ?? "?";
                            output.Add(Text($"Cue · {who}: “{project.TextOf(cue, index)}”", Small, color: gray, spacing: 2, indent: 18));
                        }
                    }
                    var ch = project.Character(line.SpeakerID);
                    var kind = line.Kind == LineKind.Spoken ? "" : $" ({line.Kind.Title().ToLowerInvariant()})";
                    var color = ch is null ? Rgba.Black : Rgba.Hex(ch.ColorHex).Darker();
                    output.Add(Text($"{index.Reference(line.ParagraphID)}  {ch?.Name.ToUpperInvariant() ?? ""}{kind}", H3, Weight.SemiBold, color, spacing: 1));
                    output.Add(Text($"“{project.TextOf(line, index)}”", Body, spacing: 10));
                }
                break;
            }
            case Kind.Manuscript:
            {
                output.Add(Text(project.Title, H1, Weight.Bold, spacing: 4));
                output.Add(Text("Character key", H2, Weight.SemiBold, gray, spacing: 6));
                foreach (var c in project.Characters.OrderByDescending(c => Lines(index, c.Id)))
                {
                    var key = new Para { SpacingAfter = 2 };
                    key.Runs.Add(new Run($"  {c.Initials}  ", H3, Weight.SemiBold, Rgba.Black, Rgba.Hex(c.ColorHex).WithAlpha(0.3)));
                    key.Runs.Add(BodyRun($"  {c.Name}"));
                    output.Add(key);
                }
                output.Add(Text("", Body, spacing: 12));
                foreach (var chapter in project.Chapters)
                {
                    output.Add(Text(chapter.ShownTitle(), H2, Weight.SemiBold, spacing: 10));
                    foreach (var p in chapter.Paragraphs) output.Add(TaggedParagraph(p, project, index));
                }
                break;
            }
            case Kind.Cast:
                output.Add(Text(project.Title, H1, Weight.Bold, spacing: 4));
                output.Add(Text("Cast List", H2, Weight.SemiBold, gray, spacing: 16));
                foreach (var c in project.Characters.OrderByDescending(c => Lines(index, c.Id)))
                    output.AddRange(CharacterBlock(c, document));
                break;
            case Kind.Pronunciations:
                output.Add(Text(project.Title, H1, Weight.Bold, spacing: 4));
                output.Add(Text("Pronunciation Guide", H2, Weight.SemiBold, gray, spacing: 16));
                foreach (var p in project.Pronunciations.Where(p => !p.Dismissed).OrderBy(p => p.Term, StringComparer.CurrentCultureIgnoreCase))
                {
                    var row = new Para { SpacingAfter = 1 };
                    row.Runs.Add(new Run(p.Term, H3, Weight.SemiBold, Rgba.Black));
                    row.Runs.Add(BodyRun(p.SayItAs.Length == 0 ? "  —" : $"  {p.SayItAs}"));
                    if (p.Confirmed) row.Runs.Add(SmallRun("  ✓ confirmed", gray));
                    output.Add(row);
                    if (p.Notes.Length > 0) output.Add(Text(p.Notes, Small, color: gray, spacing: 8, indent: 12));
                }
                break;
        }
        return output;
    }

    /// A manuscript paragraph with each line highlighted in its speaker's color (red when it
    /// needs the narrator), thoughts underlined with dashes, and "[TH, M] " in front.
    static Para TaggedParagraph(Paragraph p, Project project, BookIndex index)
    {
        var text = p.Text;
        var background = new Rgba?[text.Length];
        var dashed = new bool[text.Length];
        var tags = new List<string>();
        foreach (var li in index.LinesIn(p.Id))
        {
            var line = project.Lines[li];
            if (line.Range.End > text.Length || line.Range.Location < 0) continue;
            bool needs = project.NeedsYou(line);
            var color = needs ? Rgba.Hex(CharacterPalette.AttentionHex) : Rgba.Hex(project.Character(line.SpeakerID)?.ColorHex);
            for (int i = line.Range.Location; i < line.Range.End; i++)
            {
                background[i] = color.WithAlpha(0.28);
                if (line.Kind == LineKind.Thought) dashed[i] = true;
            }
            var tag = needs ? "?" : (project.Character(line.SpeakerID)?.Initials ?? "?");
            if (!tags.Contains(tag)) tags.Add(tag);
        }
        var para = new Para { SpacingAfter = 7 };
        if (tags.Count > 0) para.Runs.Add(SmallRun($"[{string.Join(", ", tags)}] ", Rgba.Gray));
        int start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && background[i] == background[start] && dashed[i] == dashed[start]) continue;
            para.Runs.Add(new Run(text[start..i], Body, Weight.Regular, Rgba.Black, background[start], dashed[start]));
            start = i;
        }
        return para;
    }

    static List<Para> CharacterBlock(BookCharacter c, ProjectDocument document)
    {
        var output = new List<Para>();
        var stats = document.Index.StatsFor(c.Id);
        var head = new Para { SpacingAfter = 2 };
        head.Runs.Add(new Run("■ ", H2, Weight.SemiBold, Rgba.Hex(c.ColorHex)));
        head.Runs.Add(new Run(c.Name, H2, Weight.SemiBold, Rgba.Black));
        output.Add(head);
        var meta = $"{stats?.Lines ?? 0} lines · {stats?.Words ?? 0} words";
        if (stats?.FirstLine is { } first) meta += $" · first line Ch {first.Chapter + 1}";
        if (c.Aliases.Count > 0) meta += $" · also {string.Join(", ", c.Aliases)}";
        if (document.EffectiveNarrator(c) is { } narrator) meta += $" · read by {narrator.Name}";
        // Casting Style: how the narrator shades them (Just Me).
        if (document.Project.Settings.CastingStyle is not null && document.Shades().TryGetValue(c.Id, out var shade) && shade.Cue is { } cue) meta += $" · shade: {cue}";
        output.Add(Text(meta, Small, color: Rgba.Gray, spacing: 4));
        if (c.VoiceNotes.Length > 0) output.Add(Text("Voice: " + c.VoiceNotes, Body, spacing: 4));
        foreach (var kind in FactKinds.NarratorOrder)
        {
            var facts = c.Facts.Where(f => f.Kind == kind).ToList();
            if (facts.Count == 0) continue;
            output.Add(Text($"{kind.Title()}: " + string.Join("; ", facts.Select(f => f.Text)), Body, spacing: 2));
        }
        if (c.Notes.Length > 0) output.Add(Text("Notes: " + c.Notes, Body, spacing: 2));
        output.Add(Text("", Body, spacing: 10));
        return output;
    }

    // MARK: Writing

    public static void Write(Options o, ProjectDocument document, string path)
    {
        switch (o.Format)
        {
            case Format.Csv:
                // UTF-8 with a byte order mark, so Excel on Windows reads accents.
                File.WriteAllText(path, Csv(o, document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                break;
            case Format.Word:
                Word(Build(o, document), document.Project.Title, path);
                break;
            default:
                Pdf(Build(o, document), document.Project.Title, path);
                break;
        }
    }

    // MARK: PDF

    /// A sans-serif face on every system, then faces with ■ and ✓ for the glyphs it lacks.
    static readonly string[] PdfFonts =
        ["Segoe UI", "Helvetica Neue", "Arial", "Lato", "Segoe UI Symbol", "Apple Symbols", "DejaVu Sans"];

    static readonly Lazy<bool> QuestSetUp = new(() =>
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
        // A missing face or glyph falls back instead of failing the export.
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        return true;
    });

    /// US Letter, margins 54 top and bottom and 60 at the sides, like the Mac's NSPrintInfo.
    public static void Pdf(List<Para> paragraphs, string title, string path)
    {
        _ = QuestSetUp.Value;
        var pdf = QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Size(612, 792, Unit.Point);
            page.MarginTop(54);
            page.MarginBottom(54);
            page.MarginLeft(60);
            page.MarginRight(60);
            page.PageColor(QuestPDF.Helpers.Colors.White);
            page.DefaultTextStyle(s => s.FontFamily(PdfFonts).FontSize(Body).LineHeight(LineHeightMultiple).FontColor(QuestPDF.Helpers.Colors.Black));
            page.Content().Column(column =>
            {
                foreach (var p in paragraphs)
                {
                    var item = column.Item().PaddingLeft(p.Indent).PaddingBottom(p.SpacingAfter);
                    if (p.Runs.Count == 0)
                    {
                        // An empty line, as tall as a body line.
                        item.Height(Body * LineHeightMultiple * 1.2f);
                        continue;
                    }
                    item.Text(t =>
                    {
                        foreach (var r in p.Runs)
                        {
                            var span = t.Span(r.Text).FontSize(r.Size).FontColor(Color.FromHex(r.Color.AARRGGBB));
                            span = r.Weight switch
                            {
                                Weight.Bold => span.Bold(),
                                Weight.SemiBold => span.SemiBold(),
                                _ => span,
                            };
                            if (r.Background is { } bg) span = span.BackgroundColor(Color.FromHex(bg.AARRGGBB));
                            if (r.Dashed) span.Underline().DecorationDashed();
                        }
                    });
                }
            });
        })).WithMetadata(new DocumentMetadata { Title = title, Creator = "InnerVoice" });
        pdf.GeneratePdf(path);
    }

    // MARK: Word

    const string WordFont = "Segoe UI";

    static string Twips(float points) => ((int)Math.Round(points * 20)).ToString(CultureInfo.InvariantCulture);
    static string HalfPoints(float points) => ((int)Math.Round(points * 2)).ToString(CultureInfo.InvariantCulture);

    /// Word drops text it can't store in XML; tabs become spaces, other control characters go.
    static string Clean(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\t') b.Append(' ');
            else if (ch == '\n' || ch >= ' ' && ch != '￾' && ch != '￿') b.Append(ch);
        }
        return b.ToString();
    }

    public static void Word(List<Para> paragraphs, string title, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        doc.PackageProperties.Title = title;
        doc.PackageProperties.Creator = "InnerVoice";
        var main = doc.AddMainDocumentPart();

        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new W.Styles(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = WordFont, HighAnsi = WordFont, ComplexScript = WordFont, EastAsia = WordFont },
                new W.FontSize { Val = HalfPoints(Body) }, new W.FontSizeComplexScript { Val = HalfPoints(Body) })),
            new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                new W.SpacingBetweenLines { Before = "0", After = "0", Line = "276", LineRule = W.LineSpacingRuleValues.Auto }))));

        var body = new W.Body();
        foreach (var p in paragraphs)
        {
            var props = new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "0", After = Twips(p.SpacingAfter), Line = "276", LineRule = W.LineSpacingRuleValues.Auto });
            if (p.Indent > 0) props.Append(new W.Indentation { Left = Twips(p.Indent) });
            var para = new W.Paragraph(props);
            foreach (var r in p.Runs) para.Append(WordRun(r));
            body.Append(para);
        }
        body.Append(new W.SectionProperties(
            new W.PageSize { Width = 12240, Height = 15840 },
            new W.PageMargin { Top = 1080, Bottom = 1080, Left = 1200, Right = 1200, Header = 720, Footer = 720, Gutter = 0 }));
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    static W.Run WordRun(Run r)
    {
        // Children in the order the schema wants: b, color, sz, szCs, u, shd.
        var rp = new W.RunProperties();
        if (r.Weight != Weight.Regular) { rp.Append(new W.Bold()); rp.Append(new W.BoldComplexScript()); }
        if (r.Color != Rgba.Black) rp.Append(new W.Color { Val = r.Color.RRGGBB });
        rp.Append(new W.FontSize { Val = HalfPoints(r.Size) });
        rp.Append(new W.FontSizeComplexScript { Val = HalfPoints(r.Size) });
        if (r.Dashed) rp.Append(new W.Underline { Val = W.UnderlineValues.Dash });
        if (r.Background is { } bg) rp.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = bg.OnWhite().RRGGBB });
        var run = new W.Run(rp);
        var parts = Clean(r.Text).Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Append(new W.Break());
            if (parts[i].Length > 0) run.Append(new W.Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }

    // MARK: CSV

    public static string Csv(Options o, ProjectDocument document)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var project = document.Project;
        var rows = new List<string>();
        if (o.Kind == Kind.Pronunciations)
        {
            rows.Add("Term,Say It As,Notes,Confirmed,Count");
            foreach (var p in project.Pronunciations.Where(p => !p.Dismissed))
                rows.Add(string.Join(",", Q(p.Term), Q(p.SayItAs), Q(p.Notes), p.Confirmed ? "yes" : "no", p.Occurrences.ToString(CultureInfo.InvariantCulture)));
            return string.Join("\n", rows);
        }
        rows.Add("Name,Also Called,Narrator,Lines,Words,First Chapter," + string.Join(",", FactKinds.NarratorOrder.Select(k => k.Title())) + ",Voice Notes,Notes");
        foreach (var c in project.Characters)
        {
            var s = document.Index.StatsFor(c.Id);
            var facts = FactKinds.NarratorOrder.Select(k => Q(string.Join("; ", c.Facts.Where(f => f.Kind == k).Select(f => f.Text))));
            var cells = new List<string>
            {
                Q(c.Name), Q(string.Join(", ", c.Aliases)),
                Q(document.EffectiveNarrator(c)?.Name ?? ""),
                (s?.Lines ?? 0).ToString(CultureInfo.InvariantCulture), (s?.Words ?? 0).ToString(CultureInfo.InvariantCulture),
                s?.FirstLine is { } f ? (f.Chapter + 1).ToString(CultureInfo.InvariantCulture) : "",
            };
            cells.AddRange(facts);
            cells.Add(Q(c.VoiceNotes));
            cells.Add(Q(c.Notes));
            rows.Add(string.Join(",", cells));
        }
        return string.Join("\n", rows);
    }
}
