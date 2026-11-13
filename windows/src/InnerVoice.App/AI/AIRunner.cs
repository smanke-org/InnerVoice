using Avalonia.Threading;
using InnerVoice.App.Document;
using InnerVoice.Core;
using InnerVoice.Core.AI;

namespace InnerVoice.App.AI;

/// Runs the AI over one book: Tag Speakers and Build Summaries (the Mac's TaggingController,
/// without the tone pass and voice casting, which only matter to AI voices). Results show
/// up chapter by chapter while the narrator keeps reading; Stop keeps what is done; one Undo
/// takes a whole run back without touching the narrator's own fixes.
public sealed partial class AIRunner(ProjectDocument document)
{
    public enum Job { Tagging, Summaries, Claude }

    public sealed record Status(Job Job, double Progress, string Message, string? Character = null, double CharacterProgress = 0,
                                int CharacterNumber = 0, int CharacterCount = 0, string Phase = "");

    public Status? Current { get; private set; }
    public string? Finished { get; set; }
    public string? Failure { get; set; }
    /// Claude's changes from the last Tag Speakers run, waiting for the narrator to keep or
    /// undo them (Project Settings › Review Claude's changes). null: nothing to review.
    public PendingReview? Review { get; set; }

    public sealed record PendingReview(List<AIReviewItem> Items, Dictionary<Guid, Line> Before, HashSet<Guid> AddedCharacters, string Model);
    public bool IsRunning => Current is not null;
    public event Action? Changed;

    /// Any book busy with the AI (for the close question).
    public static int Running { get; private set; }

    CancellationTokenSource? cts;

    void Set(Status? s)
    {
        if (Current is null && s is not null) Running++;
        if (Current is not null && s is null) Running--;
        Current = s;
        Changed?.Invoke();
    }

    void Update(Func<Status, Status> change) => Dispatcher.UIThread.Post(() => { if (Current is { } s) { Current = change(s); Changed?.Invoke(); } });

    public void Stop() => cts?.Cancel();

    // MARK: Tag Speakers

    /// Tags every line the narrator and the rules haven't settled. With `chapters`, only those.
    public async Task TagSpeakers(IReadOnlyList<int>? chapters = null)
    {
        if (IsRunning) return;
        Failure = null;
        Review = null;
        Finished = null;
        Set(new Status(Job.Tagging, 0, "Loading the model…"));
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        var changed = new Dictionary<Guid, Line>(); // line id → before
        var charactersBefore = document.Project.Characters.Select(c => c.Id).ToHashSet();
        try
        {
            var model = await ModelManager.Shared.Model(bookAllowed: document.Project.Settings.CloudTextAllowed is not null, book: document.Project.UsageKey());
            var targets = chapters ?? Enumerable.Range(0, document.Project.Chapters.Count).ToList();
            // Lines the AI gave up on ("?") get another try: the rules first, then the AI.
            document.ApplyQuietly(p =>
            {
                var ids = targets.SelectMany(c => p.Chapters[c].Paragraphs.Select(x => x.Id)).ToHashSet();
                var before = p.Lines.ToDictionary(l => l.Id, l => l.Copy());
                foreach (var l in p.Lines.Where(l => ids.Contains(l.ParagraphID) && l.Source == AssignmentSource.Ai && l.SpeakerID is null))
                {
                    l.Source = AssignmentSource.None;
                    l.Confidence = 0;
                }
                RuleAttribution.Apply(p);
                ToneRules.Apply(p);
                foreach (var l in p.Lines)
                    if (before.TryGetValue(l.Id, out var b) && (!b.SameAssignment(l) || !b.SameTone(l))) changed.TryAdd(l.Id, b);
            });
            // Pronoun hints ("she said") for the prompt, from the rules run on a copy.
            var hints = RuleAttribution.Apply(document.Project.Copy());
            int total = Math.Max(1, targets.Count);
            for (int n = 0; n < targets.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                int c = targets[n];
                var title = document.Project.Chapters[c].ShownTitle();
                Set(new Status(Job.Tagging, (double)n / total, $"Tagging {title} ({n + 1} of {total})…"));
                var chunks = AttributionPrompt.Chunks(document.Project, document.Index, c, hints, model.PassageWords);
                for (int k = 0; k < chunks.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[k];
                    int maxTokens = 40 + chunk.Labels.Count * 34;
                    List<AttributionReply>? replies = null;
                    for (int attempt = 0; attempt < 2 && replies is null; attempt++)
                    {
                        var text = await model.Complete(chunk.Messages, AttributionPrompt.Grammar, maxTokens, ct: ct);
                        replies = AttributionPrompt.Parse(text);
                    }
                    if (replies is not null)
                    {
                        var modelName = model.DisplayName;
                        document.ApplyQuietly(p =>
                        {
                            var before = AttributionPrompt.Apply(replies, chunk, p);
                            foreach (var l in before) changed.TryAdd(l.Id, l);
                            // Which model tagged each line, for its accuracy in Activity.
                            var ids = before.Select(l => l.Id).ToHashSet();
                            foreach (var l in p.Lines.Where(l => ids.Contains(l.Id) && l.Source == AssignmentSource.Ai)) l.AiModel = modelName;
                        });
                    }
                    Set(Current! with { Progress = (n + 0.7 * (k + 1) / Math.Max(1, chunks.Count)) / total });
                }
                // Then how each line is said, now that the speakers are known (the Mac's tone
                // pass): a grammar per chunk that needs an answer for every line.
                var toneChunks = TonePrompt.Chunks(document.Project, document.Index, c, model.PassageWords);
                for (int k = 0; k < toneChunks.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = toneChunks[k];
                    var text = await model.Complete(chunk.Messages, chunk.Grammar, 40 + chunk.Labels.Count * 22, ct: ct);
                    ct.ThrowIfCancellationRequested();
                    if (TonePrompt.Parse(text) is { } toneReplies)
                        document.ApplyQuietly(p =>
                        {
                            foreach (var l in TonePrompt.Apply(toneReplies, chunk, p)) changed.TryAdd(l.Id, l);
                        });
                    Set(Current! with { Progress = (n + 0.7 + 0.3 * (k + 1) / Math.Max(1, toneChunks.Count)) / total });
                }
            }
            document.ApplyQuietly(p => p.TonesTagged = AppleDate.Now);
            foreach (var line in document.Project.Lines)
            {
                if (!changed.TryGetValue(line.Id, out var old) || old.SpeakerID is not null || line.SpeakerID is null) continue;
                if (line.Source == AssignmentSource.Ai) Activity.Record(ActivityKey.LinesAI);
                if (line.Source is AssignmentSource.Rule or AssignmentSource.Exchange) Activity.Record(ActivityKey.LinesRules);
            }
            foreach (var line in document.Project.Lines)
                if (changed.TryGetValue(line.Id, out var old) && line.ToneSource == ToneSource.Ai && old.ToneSource != ToneSource.Ai)
                    Activity.Record(ActivityKey.TonesAI);
            int newCharacters = document.Project.Characters.Count(x => !charactersBefore.Contains(x.Id));
            document.ApplyQuietly(p => p.LastTagged = AppleDate.Now);
            int needs = document.Project.LinesNeedingYou;
            _ = document.RecolorAutomaticAsync();
            // Casting that happens on its own: new characters get voices.
            Speech.VoiceCastingRun.CastIfNeeded(document);
            if (model.IsCloud && document.Project.Settings.ReviewsCloudAI)
            {
                var items = AIReview.Items(changed, document.Project);
                if (items.Count > 0)
                {
                    var added = document.Project.Characters.Where(x => !charactersBefore.Contains(x.Id)).Select(x => x.Id).ToHashSet();
                    Review = new PendingReview(items, new Dictionary<Guid, Line>(changed), added, model.DisplayName);
                }
            }
            Finished = $"Tagged {changed.Count:N0} lines" + (newCharacters == 0 ? "" : $" and found {newCharacters} new characters") +
                       (needs > 0 ? $". {needs:N0} need you." : ".");
        }
        catch (OperationCanceledException) { Finished = "Stopped. Tag Speakers continues from there."; }
        catch (Exception e) { Failure = e.Message; }
        finally
        {
            RegisterTagUndo(changed, charactersBefore);
            Set(null);
        }
    }

    void RegisterTagUndo(Dictionary<Guid, Line> changed, HashSet<Guid> charactersBefore)
    {
        if (changed.Count == 0) return;
        var added = document.Project.Characters.Where(c => !charactersBefore.Contains(c.Id)).Select(c => c.Id).ToHashSet();
        document.RegisterRunUndo("Tag Speakers", p =>
        {
            // Put back only what the AI or the rules still own; the narrator's later fixes stay.
            // Speaker and tone are separate: a line can have the narrator's speaker and the AI's tone.
            foreach (var l in p.Lines)
            {
                if (!changed.TryGetValue(l.Id, out var old)) continue;
                if (l.ToneSource != ToneSource.User)
                {
                    l.Tone = old.Tone;
                    l.ToneConfidence = old.ToneConfidence;
                    l.ToneSource = old.ToneSource;
                }
                if (l.Source is not (AssignmentSource.Ai or AssignmentSource.Rule or AssignmentSource.Exchange)) continue;
                l.SpeakerID = old.SpeakerID;
                l.Confidence = old.Confidence;
                l.Source = old.Source;
                l.Kind = old.Kind;
                l.AiModel = old.AiModel;
            }
            var used = p.Lines.Select(l => l.SpeakerID).Where(s => s is not null).Cast<Guid>().ToHashSet();
            p.Characters.RemoveAll(c => added.Contains(c.Id) && !used.Contains(c.Id));
        });
    }

    /// Lets the AI decide the AI's own lines again in these chapters.
    public Task Retag(IReadOnlyList<int> chapters)
    {
        document.Change("Reset AI Tags", p =>
        {
            var ids = chapters.SelectMany(c => p.Chapters[c].Paragraphs.Select(x => x.Id)).ToHashSet();
            foreach (var l in p.Lines.Where(l => ids.Contains(l.ParagraphID) && l.Source == AssignmentSource.Ai))
            {
                l.Source = AssignmentSource.None;
                l.SpeakerID = null;
                l.Confidence = 0;
            }
            RuleAttribution.Apply(p);
        });
        return TagSpeakers(chapters);
    }

    // MARK: Build Summaries

    sealed record SavedSummary(List<Fact> Facts, string Notes, string? FromBook, string? Brief);

    static Dictionary<Guid, SavedSummary> Snapshot(Project p) =>
        p.Characters.ToDictionary(c => c.Id, c => new SavedSummary(c.Facts.ConvertAll(f => f.Copy()), c.VoiceNotes, c.VoiceNotesFromBook, c.VoiceBriefGenerated));

    public async Task BuildSummaries(IReadOnlyCollection<Guid>? only = null)
    {
        if (IsRunning) return;
        Failure = null;
        Finished = null;
        Set(new Status(Job.Summaries, 0, "Loading the model…"));
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        var before = Snapshot(document.Project);
        int added = 0, notesWritten = 0, briefsWritten = 0;
        try
        {
            var model = await ModelManager.Shared.Model(bookAllowed: document.Project.Settings.CloudTextAllowed is not null, book: document.Project.UsageKey());
            var index = document.Index;
            var cast = document.Project.Characters.OrderByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0).ToList();
            if (only is not null) cast = cast.Where(c => only.Contains(c.Id)).ToList();
            // Two parts: reading the book for facts, then a voice brief for each character,
            // written after everyone's facts so each can be set apart from their scene partners.
            const double readShare = 0.75;
            double total = Math.Max(1, cast.Count);
            for (int n = 0; n < cast.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var character = cast[n];
                Set(new Status(Job.Summaries, readShare * n / total, $"Reading about {character.Name} ({n + 1} of {cast.Count})…",
                    character.Name, 0, n + 1, cast.Count, "Analyzing the cast"));
                var chunks = SummaryPrompt.Chunks(document.Project.Character(character.Id) ?? character, document.Project, document.Index,
                    maxWords: model.PassageWords + 200).Take(4).ToList();
                double count = Math.Max(1, chunks.Count);
                for (int k = 0; k < chunks.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    int kk = k, nn = n;
                    var text = await model.Complete(chunks[k].Messages, SummaryPrompt.Grammar, 700, f =>
                        Update(s => s.CharacterNumber == nn + 1 && s.Phase == "Analyzing the cast"
                            ? s with { CharacterProgress = Math.Max(s.CharacterProgress, (kk + f) / count), Progress = Math.Max(s.Progress, readShare * (nn + (kk + f) / count) / total) }
                            : s), ct);
                    Set(Current! with { CharacterProgress = (k + 1) / count, Progress = readShare * (n + (k + 1) / count) / total });
                    if (SummaryPrompt.Parse(text) is not { } replies) continue;
                    var chunk = chunks[k];
                    document.ApplyQuietly(p => added += SummaryPrompt.Merge(replies, chunk, character.Id, p, new BookIndex(p)));
                }
                // What the book says about how they sound, into their Voice Notes.
                document.ApplyQuietly(p => { if (p.Character(character.Id) is { } c && VoiceNotesFromBook.Merge(c)) notesWritten++; });
            }
            // Voice briefs: pitch, pace, texture, attitude, and the contrast with scene partners.
            var speaking = cast.Where(c => (document.Index.StatsFor(c.Id)?.Lines ?? 0) > 0).ToList();
            double briefTotal = Math.Max(1, speaking.Count);
            for (int m = 0; m < speaking.Count; m++)
            {
                ct.ThrowIfCancellationRequested();
                var character = speaking[m];
                Set(new Status(Job.Summaries, readShare + (1 - readShare) * m / briefTotal, $"Writing a voice brief for {character.Name} ({m + 1} of {speaking.Count})…",
                    character.Name, 0, m + 1, speaking.Count, "Writing voice briefs"));
                if (document.Project.Character(character.Id) is not { } current) continue;
                var messages = VoiceBriefPrompt.Messages(current, document.Project, document.Index);
                int mm = m;
                var text = await model.Complete(messages, VoiceBriefPrompt.Grammar, 260, f =>
                    Update(s => s.Phase == "Writing voice briefs" && s.CharacterNumber == mm + 1
                        ? s with { CharacterProgress = Math.Max(s.CharacterProgress, f), Progress = Math.Max(s.Progress, readShare + (1 - readShare) * (mm + f) / briefTotal) }
                        : s), ct);
                if (VoiceBriefPrompt.Parse(text) is { } brief)
                    document.ApplyQuietly(p => { if (p.Character(character.Id) is { } c && brief.Merge(c)) briefsWritten++; });
            }
            if (only is null) document.ApplyQuietly(p => p.SummariesBuilt = AppleDate.Now);
            Activity.Record(ActivityKey.Facts, added);
            _ = document.RecolorAutomaticAsync();
            // Voice Notes and facts shape casting: voices it was unsure of are decided again.
            Speech.VoiceCastingRun.CastIfNeeded(document);
            Finished = $"Found {added:N0} facts" + (briefsWritten > 0 ? $" and wrote {briefsWritten} voice briefs." : ".");
        }
        catch (OperationCanceledException) { Finished = "Stopped. Summaries so far are kept."; }
        catch (Exception e) { Failure = e.Message; }
        finally
        {
            if (added > 0 || notesWritten > 0 || briefsWritten > 0)
                document.RegisterRunUndo("Build Summaries", p =>
                {
                    foreach (var c in p.Characters)
                    {
                        if (!before.TryGetValue(c.Id, out var s)) continue;
                        c.Facts = s.Facts.ConvertAll(f => f.Copy());
                        c.VoiceNotes = s.Notes;
                        c.VoiceNotesFromBook = s.FromBook;
                        c.VoiceBriefGenerated = s.Brief;
                    }
                });
            Set(null);
        }
    }
}
