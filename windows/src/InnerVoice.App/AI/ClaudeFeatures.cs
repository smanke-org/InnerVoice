using InnerVoice.Core;
using InnerVoice.Core.AI;

namespace InnerVoice.App.AI;

/// Book › Claude (Cloud): the features only Claude does (the Mac's ClaudeFeatures): Clean Up
/// Text, Write Chapter Recaps, Suggest Delivery Hints and Suggest Spellings. The last two are
/// offered, as on the Mac, only in books with voices (`Project.ShowsVoices`/`ShowsPronunciations`).
/// Each sends text only with a key, the narrator's yes in Settings and this book's permission
/// (`ClaudeTextModel` checks all three). Results wait in a review list while Review Claude's
/// Changes is on (spellings always do: the narrator confirms each), applied in one Undo step.
public sealed partial class AIRunner
{
    public enum ClaudeJob { Cleanup, Recaps, Delivery, Spellings }

    public static readonly ClaudeJob[] ClaudeJobs = [ClaudeJob.Cleanup, ClaudeJob.Recaps, ClaudeJob.Delivery, ClaudeJob.Spellings];

    /// Whether this job applies to this book (project type), as on the Mac.
    public static bool Offered(ClaudeJob job, Project project) => job switch
    {
        ClaudeJob.Delivery => project.ShowsVoices,
        ClaudeJob.Spellings => project.ShowsPronunciations,
        _ => true,
    };

    public sealed record RecapProposal(Guid ChapterID, string Text, string? Before);

    /// Results waiting for the narrator to keep or leave out.
    public sealed record ClaudeProposals(ClaudeJob Job, List<CleanupFix> Fixes, List<RecapProposal> Recaps, string Model)
    {
        public List<DeliveryProposal> Hints { get; init; } = [];
        public List<SpellingProposal> Spellings { get; init; } = [];

        public int Count => Job switch
        {
            ClaudeJob.Cleanup => Fixes.Count,
            ClaudeJob.Recaps => Recaps.Count,
            ClaudeJob.Delivery => Hints.Count,
            _ => Spellings.Count,
        };
    }

    public ClaudeProposals? Proposals { get; set; }

    public static string Title(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup => "Clean Up Text",
        ClaudeJob.Recaps => "Write Chapter Recaps",
        ClaudeJob.Delivery => "Suggest Delivery Hints",
        _ => "Suggest Spellings",
    };

    /// What each job sends, for the questions before a run.
    public static string What(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup or ClaudeJob.Delivery => "the book, a passage at a time",
        ClaudeJob.Recaps => "each chapter (to recap it for the next one)",
        _ => "each unfinished term in the pronunciation guide, with the sentence it first appears in",
    };

    /// The Claude model the Claude-only features use: the chosen model when it's Claude,
    /// otherwise the default (Sonnet).
    public static string FeatureModelID =>
        ModelManager.Shared.SelectionIsClaude ? ModelManager.Shared.Selection[Claude.SelectionPrefix.Length..] : Claude.DefaultModelID;

    sealed record Request(List<ChatMessage> Messages, int MaxTokens, Dictionary<string, Guid> Labels, Guid? ChapterID);

    List<Request> Requests(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup => CleanupPrompt.Chunks(document.Project).Select(c => new Request(c.Messages, 1500, c.Labels, null)).ToList(),
        ClaudeJob.Recaps => RecapPrompt.Jobs(document.Project).Select(j => new Request(j.Messages, 400, [], j.ChapterID)).ToList(),
        ClaudeJob.Delivery => DeliveryPrompt.Chunks(document.Project, document.Index).Select(c => new Request(c.Messages, 1200, c.Labels, null)).ToList(),
        _ => SpellingPrompt.Chunks(document.Project, document.Index).Select(c => new Request(c.Messages, 2000, c.Labels, null)).ToList(),
    };

    static int OutputEach(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup => 250,
        ClaudeJob.Recaps => 150,
        ClaudeJob.Delivery => 200,
        _ => 900,
    };

    /// What a run would cost, worked out on this PC.
    public CostEstimate Estimate(ClaudeJob job) => CostEstimator.Requests(Requests(job).Select(r => r.Messages), OutputEach(job));

    /// Why there's nothing to send, or null when there is.
    public string? NothingToSend(ClaudeJob job) => Requests(job).Count > 0 ? null : job switch
    {
        ClaudeJob.Cleanup => "There's no text to clean up.",
        ClaudeJob.Recaps => "No chapters need a recap: the first has nothing before it, and recaps you wrote are kept.",
        ClaudeJob.Delivery => "No lines to direct yet. Tag Speakers first; lines with your own hints are kept.",
        _ => "Every term in the pronunciation guide has a Say It As already.",
    };

    /// Runs a Claude-only feature. The window asks first (the book's permission, the cost).
    public async Task RunClaude(ClaudeJob job)
    {
        if (IsRunning) return;
        Failure = null;
        Finished = null;
        Proposals = null;
        var reqs = Requests(job);
        if (reqs.Count == 0) { Finished = NothingToSend(job); Changed?.Invoke(); return; }
        var title = Title(job);
        Set(new Status(Job.Claude, 0, $"{title} with Claude…"));
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var model = ClaudeAccount.Shared.Model(FeatureModelID, document.Project.Settings.CloudTextAllowed is not null);
            var fixes = new List<CleanupFix>();
            var recaps = new List<RecapProposal>();
            var hints = new List<DeliveryProposal>();
            var spellings = new List<SpellingProposal>();
            int refused = 0, unreadable = 0;
            for (int n = 0; n < reqs.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var req = reqs[n];
                Set(Current! with { Progress = (double)n / reqs.Count, Message = $"{title} with {model.DisplayName} ({n + 1} of {reqs.Count})…" });
                string text;
                try { text = await model.Complete(req.Messages, null, req.MaxTokens, ct: ct); }
                catch (ClaudeException e) when (e.Message == ClaudeException.Refused) { refused++; continue; }
                ct.ThrowIfCancellationRequested();
                var p = document.Project;
                if (job == ClaudeJob.Cleanup)
                {
                    if (CleanupPrompt.Parse(text) is { } replies)
                        fixes.AddRange(CleanupPrompt.Fixes(replies, new CleanupPrompt.Chunk(req.Labels, req.Messages), p, document.Index));
                    else unreadable++;
                }
                else if (job == ClaudeJob.Recaps)
                {
                    if (req.ChapterID is { } id && RecapPrompt.Parse(text) is { } recap)
                        recaps.Add(new RecapProposal(id, recap, p.Chapters.Find(c => c.Id == id)?.RecapText));
                    else unreadable++;
                }
                else if (job == ClaudeJob.Delivery)
                {
                    if (DeliveryPrompt.Parse(text) is { } replies)
                        hints.AddRange(DeliveryPrompt.Proposals(replies, new DeliveryPrompt.Chunk(req.Labels, req.Messages), p, document.Index));
                    else unreadable++;
                }
                else if (SpellingPrompt.Parse(text, new SpellingPrompt.Chunk(req.Labels, req.Messages), p) is { } found)
                    spellings.AddRange(found);
                else unreadable++;
            }
            var proposals = new ClaudeProposals(job, fixes, recaps, model.DisplayName) { Hints = hints, Spellings = spellings };
            int skipped = refused + unreadable;
            var note = skipped == 0 ? "" : $" Claude didn't answer for {skipped} of {reqs.Count} " + (skipped == 1 ? "passage." : "passages.");
            int c = proposals.Count;
            if (c == 0)
                Finished = job switch
                {
                    ClaudeJob.Cleanup => "Claude found no import problems to fix.",
                    ClaudeJob.Recaps => "Claude wrote no recaps.",
                    ClaudeJob.Delivery => "Claude found no lines that need more than their tone.",
                    _ => "Claude had no spellings to suggest.",
                } + note;
            else if (job == ClaudeJob.Spellings || document.Project.Settings.ReviewsCloudAI)
            {
                Proposals = proposals;
                Finished = job switch
                {
                    ClaudeJob.Cleanup => $"Claude proposed {c:N0} " + (c == 1 ? "fix" : "fixes") + " to review.",
                    ClaudeJob.Recaps => $"Claude wrote {c:N0} " + (c == 1 ? "recap" : "recaps") + " to review.",
                    ClaudeJob.Delivery => $"Claude suggested {c:N0} delivery " + (c == 1 ? "hint" : "hints") + " to review.",
                    _ => $"Claude suggested {c:N0} " + (c == 1 ? "spelling" : "spellings") + " to confirm.",
                } + note;
            }
            else
            {
                Apply(proposals);
                Finished = job switch
                {
                    ClaudeJob.Cleanup => $"Fixed {c:N0} import " + (c == 1 ? "problem." : "problems.") + " Edit › Undo puts the text back.",
                    ClaudeJob.Recaps => $"Wrote {c:N0} chapter " + (c == 1 ? "recap." : "recaps."),
                    _ => $"Added {c:N0} delivery " + (c == 1 ? "hint." : "hints."),
                } + note;
            }
        }
        catch (OperationCanceledException) { Finished = "Stopped. Nothing was changed."; }
        catch (Exception e) { Failure = e.Message; }
        finally { Set(null); }
    }

    /// Applies the kept proposals in one Undo step.
    public void Apply(ClaudeProposals kept)
    {
        if (kept.Count == 0) return;
        switch (kept.Job)
        {
            case ClaudeJob.Cleanup:
                document.Change("Clean Up Text", p => TextCleanup.Apply(kept.Fixes, p), textChanged: true);
                break;
            case ClaudeJob.Recaps:
                document.Change("Write Chapter Recaps", p => { foreach (var r in kept.Recaps) RecapPrompt.Put(r.Text, r.ChapterID, p); });
                break;
            case ClaudeJob.Delivery:
                document.Change("Delivery Hints", p => DeliveryPrompt.Apply(kept.Hints, p));
                break;
            default:
                document.Change("Use Suggested Spellings", p => SpellingPrompt.Apply(kept.Spellings, p));
                break;
        }
    }
}
