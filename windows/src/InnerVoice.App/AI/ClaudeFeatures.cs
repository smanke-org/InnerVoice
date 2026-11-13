using InnerVoice.Core;
using InnerVoice.Core.AI;

namespace InnerVoice.App.AI;

/// Book › Claude (Cloud): the features only Claude does (the Mac's ClaudeFeatures). Windows
/// has Clean Up Text and Write Chapter Recaps; Suggest Delivery Hints needs AI voices and
/// Suggest Spellings the pronunciation guide, which only the Mac shows. Each sends text only
/// with a key, the narrator's yes in Settings and this book's permission (`ClaudeTextModel`
/// checks all three). Results wait in a review list while Review Claude's Changes is on, and
/// are applied in one Undo step.
public sealed partial class AIRunner
{
    public enum ClaudeJob { Cleanup, Recaps }

    public sealed record RecapProposal(Guid ChapterID, string Text, string? Before);

    /// Results waiting for the narrator to keep or leave out.
    public sealed record ClaudeProposals(ClaudeJob Job, List<CleanupFix> Fixes, List<RecapProposal> Recaps, string Model)
    {
        public int Count => Job == ClaudeJob.Cleanup ? Fixes.Count : Recaps.Count;
    }

    public ClaudeProposals? Proposals { get; set; }

    public static string Title(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup => "Clean Up Text",
        _ => "Write Chapter Recaps",
    };

    /// What each job sends, for the questions before a run.
    public static string What(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup => "the book, a passage at a time",
        _ => "each chapter (to recap it for the next one)",
    };

    /// The Claude model the Claude-only features use: the chosen model when it's Claude,
    /// otherwise the default (Sonnet).
    public static string FeatureModelID =>
        ModelManager.Shared.SelectionIsClaude ? ModelManager.Shared.Selection[Claude.SelectionPrefix.Length..] : Claude.DefaultModelID;

    sealed record Request(List<ChatMessage> Messages, int MaxTokens, Dictionary<string, Guid> Labels, Guid? ChapterID);

    List<Request> Requests(ClaudeJob job) => job switch
    {
        ClaudeJob.Cleanup => CleanupPrompt.Chunks(document.Project).Select(c => new Request(c.Messages, 1500, c.Labels, null)).ToList(),
        _ => RecapPrompt.Jobs(document.Project).Select(j => new Request(j.Messages, 400, [], j.ChapterID)).ToList(),
    };

    static int OutputEach(ClaudeJob job) => job == ClaudeJob.Cleanup ? 250 : 150;

    /// What a run would cost, worked out on this PC.
    public CostEstimate Estimate(ClaudeJob job) => CostEstimator.Requests(Requests(job).Select(r => r.Messages), OutputEach(job));

    /// Why there's nothing to send, or null when there is.
    public string? NothingToSend(ClaudeJob job) => Requests(job).Count > 0 ? null : job switch
    {
        ClaudeJob.Cleanup => "There's no text to clean up.",
        _ => "No chapters need a recap: the first has nothing before it, and recaps you wrote are kept.",
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
        Set(new Status(job == ClaudeJob.Cleanup ? Job.Cleanup : Job.Recaps, 0, $"{title} with Claude…"));
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var model = ClaudeAccount.Shared.Model(FeatureModelID, document.Project.Settings.CloudTextAllowed is not null);
            var fixes = new List<CleanupFix>();
            var recaps = new List<RecapProposal>();
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
                else if (req.ChapterID is { } id && RecapPrompt.Parse(text) is { } recap)
                    recaps.Add(new RecapProposal(id, recap, p.Chapters.Find(c => c.Id == id)?.RecapText));
                else unreadable++;
            }
            var found = new ClaudeProposals(job, fixes, recaps, model.DisplayName);
            int skipped = refused + unreadable;
            var note = skipped == 0 ? "" : $" Claude didn't answer for {skipped} of {reqs.Count} " + (skipped == 1 ? "passage." : "passages.");
            if (found.Count == 0)
                Finished = (job == ClaudeJob.Cleanup ? "Claude found no import problems to fix." : "Claude wrote no recaps.") + note;
            else if (document.Project.Settings.ReviewsCloudAI)
            {
                Proposals = found;
                int c = found.Count;
                Finished = (job == ClaudeJob.Cleanup
                    ? $"Claude proposed {c:N0} " + (c == 1 ? "fix" : "fixes") + " to review."
                    : $"Claude wrote {c:N0} " + (c == 1 ? "recap" : "recaps") + " to review.") + note;
            }
            else
            {
                Apply(found);
                int c = found.Count;
                Finished = (job == ClaudeJob.Cleanup
                    ? $"Fixed {c:N0} import " + (c == 1 ? "problem." : "problems.") + " Edit › Undo puts the text back."
                    : $"Wrote {c:N0} chapter " + (c == 1 ? "recap." : "recaps.")) + note;
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
        if (kept.Job == ClaudeJob.Cleanup)
            document.Change("Clean Up Text", p => TextCleanup.Apply(kept.Fixes, p), textChanged: true);
        else
            document.Change("Write Chapter Recaps", p => { foreach (var r in kept.Recaps) RecapPrompt.Put(r.Text, r.ChapterID, p); });
    }
}
