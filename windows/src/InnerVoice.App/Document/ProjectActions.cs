using InnerVoice.Core;

namespace InnerVoice.App.Document;

/// The narrator's edits, ported from the Mac's Document/Actions.swift. Each is one undo step
/// with a name for the Edit menu.
public static class ProjectActions
{
    public static void Assign(this ProjectDocument d, IReadOnlyCollection<Guid> lineIDs, Guid? speaker)
    {
        var ids = lineIDs.ToHashSet();
        Activity.RecordReview(d.Project.Lines.Where(l => ids.Contains(l.Id)), speaker);
        d.Change(ids.Count == 1 ? "Assign Line" : $"Assign {ids.Count} Lines", p =>
        {
            foreach (var l in p.Lines.Where(l => ids.Contains(l.Id)))
            {
                l.SpeakerID = speaker;
                l.Source = AssignmentSource.User;
                l.Confidence = 1;
            }
        });
    }

    public static void ApplyExchange(this ProjectDocument d, IEnumerable<Exchange.Proposal> proposals)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var pr in proposals) map.TryAdd(pr.LineID, pr.SpeakerID);
        d.Change("Swap Exchange", p =>
        {
            foreach (var l in p.Lines)
            {
                if (!map.TryGetValue(l.Id, out var s)) continue;
                l.SpeakerID = s;
                l.Source = AssignmentSource.User;
                l.Confidence = 1;
            }
        });
    }

    public static void SetKind(this ProjectDocument d, LineKind kind, IReadOnlyCollection<Guid> lineIDs)
    {
        var ids = lineIDs.ToHashSet();
        d.Change("Change Line Kind", p => { foreach (var l in p.Lines.Where(l => ids.Contains(l.Id))) l.Kind = kind; });
    }

    /// The text is narration after all.
    public static void RemoveLines(this ProjectDocument d, IReadOnlyCollection<Guid> lineIDs)
    {
        var ids = lineIDs.ToHashSet();
        Activity.RecordReview(d.Project.Lines.Where(l => ids.Contains(l.Id) && l.Source != AssignmentSource.None), null);
        d.Change(ids.Count == 1 ? "Mark as Narration" : $"Mark {ids.Count} Lines as Narration", p => p.Lines.RemoveAll(l => ids.Contains(l.Id)));
    }

    /// Deletes text the narrator doesn't read from the project's copy of the book.
    public static void DeleteText(this ProjectDocument d, Dictionary<Guid, List<TextSpan>> cuts, HashSet<Guid>? chapters = null,
                                  Dictionary<Guid, TextSpan>? headings = null)
    {
        chapters ??= [];
        headings ??= [];
        if (cuts.Count == 0 && chapters.Count == 0 && headings.Count == 0) return;
        bool onlyChapters = cuts.Count == 0 && headings.Count == 0;
        var name = chapters.Count == 1 && onlyChapters ? "Delete Chapter" : chapters.Count > 1 && onlyChapters ? "Delete Chapters" : "Delete Text";
        d.Change(name, p => p.DeleteText(cuts, chapters, headings), textChanged: true);
    }

    public static void SetPinned(this ProjectDocument d, Guid id, bool on) =>
        d.UpdateCharacter(id, on ? "Pin Speaker" : "Unpin Speaker", c => c.Pinned = on ? true : null);

    /// Makes a line from text the finder missed.
    public static Guid MakeLine(this ProjectDocument d, Guid paragraphID, TextSpan range, Guid? speaker)
    {
        var line = new Line(paragraphID, range, LineKind.Spoken, speaker, speaker is null ? 0 : 1,
                            speaker is null ? AssignmentSource.None : AssignmentSource.User);
        d.Change("Make Line", p =>
        {
            var index = new BookIndex(p);
            if (!index.Positions.TryGetValue(paragraphID, out var pos)) return;
            p.Lines.RemoveAll(l => l.ParagraphID == paragraphID && Math.Min(l.Range.End, range.End) - Math.Max(l.Range.Location, range.Location) > 0);
            int at = p.Lines.FindIndex(l => index.Positions.TryGetValue(l.ParagraphID, out var lp) &&
                                            (lp > pos || (lp == pos && l.Range.Location > range.Location)));
            p.Lines.Insert(at < 0 ? p.Lines.Count : at, line);
        });
        return line.Id;
    }

    public static Guid AddCharacter(this ProjectDocument d, string name)
    {
        var trimmed = name.Trim();
        var existing = d.Project.Characters.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) return existing.Id;
        var c = new BookCharacter(trimmed, CharacterPalette.ForNew(trimmed, d.Project.Characters.Select(x => x.ColorHex)));
        d.Change("New Character", p => p.Characters.Add(c));
        return c.Id;
    }

    /// Sets a character's color, or puts it back on Automatic (`hex` null).
    public static void SetColor(this ProjectDocument d, Guid id, string? hex)
    {
        d.Change(hex is null ? "Automatic Color" : "Change Color", p =>
        {
            var c = p.Character(id);
            if (c is null) return;
            if (hex is not null)
            {
                c.ColorHex = hex;
                c.ColorChosen = true;
            }
            else
            {
                c.ColorChosen = false;
                if (CharacterPalette.FamilyOf(c.ColorHex) is null) c.ColorHex = CharacterPalette.Next(p.Characters.Select(x => x.ColorHex));
                CharacterPalette.RecolorAutomatic(p, new BookIndex(p), only: id);
            }
        });
    }

    /// Matches every Automatic color to what the book now says about each character. Runs
    /// after Tag Speakers and Build Summaries, and when a project opens. No undo step.
    public static async Task RecolorAutomaticAsync(this ProjectDocument d)
    {
        var project = d.Project.Copy();
        var index = new BookIndex(project);
        var families = await Task.Run(() => CharacterPalette.Families(project, index));
        int changed = 0;
        var probe = d.Project.Copy();
        changed = CharacterPalette.RecolorAutomatic(probe, d.Index, families);
        if (changed > 0) d.ApplyQuietly(p => CharacterPalette.RecolorAutomatic(p, new BookIndex(p), families));
    }

    public static void UpdateCharacter(this ProjectDocument d, Guid id, string name, Action<BookCharacter> body) =>
        d.Change(name, p => { if (p.Character(id) is { } c) body(c); });

    /// Moves every line and fact of `from` to `into`; `from`'s names become aliases.
    public static void Merge(this ProjectDocument d, Guid from, Guid into)
    {
        if (from == into) return;
        d.Change("Merge Characters", p =>
        {
            var source = p.Character(from);
            var target = p.Character(into);
            if (source is null || target is null) return;
            foreach (var alias in new[] { source.Name }.Concat(source.Aliases))
                if (alias != target.Name && !target.Aliases.Contains(alias)) target.Aliases.Add(alias);
            foreach (var f in source.Facts)
                if (!target.Facts.Any(t => t.Kind == f.Kind && TextMatch.Similarity(t.Text, f.Text) >= 0.6)) target.Facts.Add(f);
            if (target.Notes.Length == 0) target.Notes = source.Notes;
            if (target.VoiceNotes.Length == 0) target.VoiceNotes = source.VoiceNotes;
            foreach (var l in p.Lines.Where(l => l.SpeakerID == from)) l.SpeakerID = into;
            if (p.Settings.PovCharacterID == from) p.Settings.PovCharacterID = into;
            p.Characters.Remove(source);
        });
    }

    /// Deletes a character; their lines become Who's Speaking?
    public static void DeleteCharacter(this ProjectDocument d, Guid id)
    {
        d.Change("Delete Character", p =>
        {
            foreach (var l in p.Lines.Where(l => l.SpeakerID == id))
            {
                l.SpeakerID = null;
                l.Source = AssignmentSource.None;
                l.Confidence = 0;
            }
            if (p.Settings.PovCharacterID == id) p.Settings.PovCharacterID = null;
            p.Characters.RemoveAll(c => c.Id == id);
        });
    }

    public static void UpdateSettings(this ProjectDocument d, string name, Action<ProjectSettings> body) => d.Change(name, p => body(p.Settings));

    /// Lets this book's text go to Claude for the AI features (the narrator said yes), or stops it.
    public static void SetCloudTextAllowed(this ProjectDocument d, bool allowed) =>
        d.UpdateSettings(allowed ? "Allow Claude" : "Stop Sending to Claude", s => s.CloudTextAllowed = allowed ? AppleDate.Now : null);

    /// The speaker and summary text used as a voice cue (Teleprompter, scripts).
    public static string VoiceCue(BookCharacter c)
    {
        FactKind[] order = [FactKind.Age, FactKind.Gender, FactKind.Accent, FactKind.Voice];
        var parts = order.Select(k => c.Facts.FirstOrDefault(f => f.Kind == k)?.Text).Where(t => t is not null).Cast<string>();
        var cue = new List<string>();
        if (c.VoiceNotes.Length > 0) cue.Add(FirstBlockLine(c.VoiceNotes));
        cue.AddRange(parts);
        if (cue.Count == 0)
        {
            var any = FactKinds.NarratorOrder.Select(k => c.Facts.FirstOrDefault(f => f.Kind == k)?.Text).FirstOrDefault(t => t is not null);
            if (any is not null) cue.Add(any);
        }
        return string.Join(" · ", cue);
    }

    /// Voice notes can run long; the cue shows them as written (the Mac does the same).
    static string FirstBlockLine(string notes) => notes.Trim();
}
