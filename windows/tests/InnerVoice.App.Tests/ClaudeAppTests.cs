using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using InnerVoice.App.AI;
using InnerVoice.App.Document;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.AI;

namespace InnerVoice.App.Tests;

/// Claude in the app, against a fake Anthropic: nothing is sent until the book says yes, then
/// Tag Speakers runs through Claude and its changes wait for review.
[Collection("Model choice")]
public class ClaudeAppTests
{
    /// Answers every passage by giving each labeled line to Mara.
    sealed class FakeAnthropic : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Sent) Sent.Add((request.RequestUri!, body));
            var json = JsonNode.Parse(body)!;
            var system = json["system"]?[0]?["text"]?.GetValue<string>() ?? "";
            var prompt = json["messages"]![0]!["content"] is JsonValue v ? v.GetValue<string>() : json["messages"]![0]!["content"]!.ToJsonString();
            string text;
            if (system.StartsWith("You proofread"))
            {
                // Clean Up Text: fix the planted scanning mistake.
                var label = Regex.Match(prompt, @"\[(P\d+)\][^\[]*tbe night").Groups[1].Value;
                text = label.Length == 0 ? """{"fixes":[]}"""
                    : new JsonObject { ["fixes"] = new JsonArray(new JsonObject { ["paragraph"] = label, ["find"] = "tbe night", ["replace"] = "the night", ["issue"] = "ocr", ["note"] = "tbe" }) }.ToJsonString();
            }
            else if (system.Contains("\"Previously…\" recap"))
                text = """{"recap":"Previously… Mara waited at the harbor for news of the boat."}""";
            else
            {
                var labels = Regex.Matches(prompt, @"\bL\d+\b").Select(m => m.Value).Distinct();
                var lines = new JsonArray(labels.Select(l => (JsonNode)new JsonObject { ["id"] = l, ["speaker"] = "Mara", ["confidence"] = 95 }).ToArray());
                text = new JsonObject { ["lines"] = lines }.ToJsonString();
            }
            var reply = new JsonObject
            {
                ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-haiku-5-5",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["stop_reason"] = "end_turn", ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 50 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    [AvaloniaFact]
    public async Task CleanUpTextAndRecapsWaitForReviewAndAskTheBookFirst()
    {
        var fake = new FakeAnthropic();
        ClaudeAccount.TestHttp = new HttpClient(fake);
        var account = ClaudeAccount.Shared;
        try
        {
            account.Save("sk-ant-test", consent: true);
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            var runner = w.Runner;
            // A scanning mistake at the end of a narration paragraph.
            var target = doc.Project.Chapters.SelectMany(c => c.Paragraphs).First(x => x.Style == ParagraphStyle.Body && x.Text.Length > 20);
            doc.Change("Plant", p => p.Paragraph(target.Id, doc.Index)!.Text += " It was tbe night.");
            Assert.True(runner.Estimate(AIRunner.ClaudeJob.Cleanup).TotalTokens > 0);
            Assert.Contains("estimate", runner.Estimate(AIRunner.ClaudeJob.Cleanup).Sentence(AIRunner.FeatureModelID));

            // The book hasn't said yes: the model refuses and nothing is sent.
            await runner.RunClaude(AIRunner.ClaudeJob.Cleanup);
            Assert.Equal(ClaudeException.BookNotAllowed, runner.Failure);
            Assert.Empty(fake.Sent);

            doc.SetCloudTextAllowed(true);
            await runner.RunClaude(AIRunner.ClaudeJob.Cleanup);
            Assert.Null(runner.Failure);
            Assert.NotEmpty(fake.Sent);
            Assert.Equal(Claude.DefaultModelID, AIRunner.FeatureModelID); // a local model chosen: the features use Sonnet
            var fixes = runner.Proposals;
            Assert.NotNull(fixes);
            var fix = Assert.Single(fixes.Fixes);
            Assert.Equal(target.Id, fix.ParagraphID);
            Assert.EndsWith("tbe night.", doc.Project.Paragraph(target.Id, doc.Index)!.Text); // nothing changed yet
            Snap(new ClaudeProposalsWindow(doc, runner, fixes), "ReviewTextFixes.png");
            runner.Apply(fixes);
            Assert.EndsWith("the night.", doc.Project.Paragraph(target.Id, doc.Index)!.Text);
            Assert.Equal("Clean Up Text", doc.UndoName);

            var jobs = InnerVoice.Core.AI.RecapPrompt.Jobs(doc.Project);
            if (jobs.Count > 0)
            {
                await runner.RunClaude(AIRunner.ClaudeJob.Recaps);
                var recaps = runner.Proposals;
                Assert.NotNull(recaps);
                Assert.Equal(jobs.Count, recaps.Recaps.Count);
                Snap(new ClaudeProposalsWindow(doc, runner, recaps), "ReviewChapterRecaps.png");
                runner.Apply(recaps);
                var chapter = doc.Project.Chapters.First(c => c.Id == recaps.Recaps[0].ChapterID);
                Assert.Equal("Mara waited at the harbor for news of the boat.", chapter.RecapText);
                Assert.Null(chapter.RecapEdited);
                // Review off: kept straight away.
                doc.UpdateSettings("Review Claude's Changes", st => st.ReviewCloudAI = false);
                doc.Change("Clear", p => { foreach (var c in p.Chapters) c.Recap = null; });
                await runner.RunClaude(AIRunner.ClaudeJob.Recaps);
                Assert.Null(runner.Proposals);
                Assert.StartsWith("Wrote", runner.Finished);
                Assert.NotNull(doc.Project.Chapters.First(c => c.Id == recaps.Recaps[0].ChapterID).RecapText);
            }
            else Assert.Equal("No chapters need a recap: the first has nothing before it, and recaps you wrote are kept.", runner.NothingToSend(AIRunner.ClaudeJob.Recaps));

            doc.SetCloudTextAllowed(false);
            int sent = fake.Sent.Count;
            await runner.RunClaude(AIRunner.ClaudeJob.Cleanup);
            Assert.Equal(ClaudeException.BookNotAllowed, runner.Failure);
            Assert.Equal(sent, fake.Sent.Count);
            w.Close();
        }
        finally
        {
            account.Disconnect();
            ClaudeAccount.TestHttp = null;
        }
    }

    static void Snap(Avalonia.Controls.Window window, string file)
    {
        window.Show();
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is { } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, file), (int?)null);
        }
        window.Close();
    }

    static void SnapSettings(string file)
    {
        if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is not { } dir) return;
        var settings = SettingsWindow.Show(null, "AI");
        settings.Height = 1700;
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        settings.CaptureRenderedFrame()?.Save(Path.Combine(dir, file), (int?)null);
        settings.Close();
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task NothingIsSentUntilTheBookSaysYesThenChangesWaitForReview()
    {
        var fake = new FakeAnthropic();
        ClaudeAccount.TestHttp = new HttpClient(fake);
        Environment.SetEnvironmentVariable("INNERVOICE_MODEL", Claude.SelectionPrefix + "claude-haiku-5-5");
        var account = ClaudeAccount.Shared;
        try
        {
            Assert.False(ModelManager.Shared.IsReady); // not set up yet
            account.Save("sk-ant-test", consent: true);
            Assert.True(ModelManager.Shared.IsReady);
            Assert.Equal("Claude Haiku 5.5", ModelManager.Shared.SelectionName);

            var doc = ProjectDocument.Unsaved(TestApp.Sample().Also(p => p.LastTagged = null));
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            // The window asks first; a test run never says yes, so nothing runs.
            await w.TagSpeakers();
            Assert.Empty(fake.Sent);
            Assert.False(w.Runner.IsRunning);

            // Past the question, the model itself refuses a book that hasn't said yes.
            await w.Runner.TagSpeakers();
            Assert.Equal(ClaudeException.BookNotAllowed, w.Runner.Failure);
            await w.Runner.BuildSummaries();
            Assert.Equal(ClaudeException.BookNotAllowed, w.Runner.Failure);
            Assert.Empty(fake.Sent);

            doc.SetCloudTextAllowed(true);
            Assert.NotNull(doc.Project.Settings.CloudTextAllowed);
            await w.Runner.TagSpeakers();
            Assert.Null(w.Runner.Failure);
            Assert.NotEmpty(fake.Sent);
            Assert.All(fake.Sent, s => Assert.Equal("https://api.anthropic.com/v1/messages", s.Uri.ToString()));
            var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara"));
            var review = w.Runner.Review;
            Assert.NotNull(review);
            Assert.Equal("Claude Haiku 5.5", review.Model);
            Assert.All(review.Items, i => Assert.Equal(mara.Id, i.SpeakerAfter));
            Assert.All(review.Items, i => Assert.Equal("Claude Haiku 5.5", doc.Project.Lines.First(l => l.Id == i.LineID).AiModel));

            // The review window draws; undoing one line puts it back in one Undo step.
            var sheet = new AIReviewWindow(doc, review);
            sheet.Show();
            for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is { } dir)
            {
                Directory.CreateDirectory(dir);
                sheet.CaptureRenderedFrame()?.Save(Path.Combine(dir, "ReviewClaudesChanges.png"), (int?)null);
            }
            sheet.Close();
            SnapSettings("Settings-AI-Claude.png");
            var first = review.Items[0];
            doc.Change("Undo Claude's Changes", p => AIReview.Undo(new HashSet<Guid> { first.LineID }, review.Before, review.AddedCharacters, p));
            Assert.Equal(first.SpeakerBefore, doc.Project.Lines.First(l => l.Id == first.LineID).SpeakerID);
            doc.Undo();
            Assert.Equal(mara.Id, doc.Project.Lines.First(l => l.Id == first.LineID).SpeakerID);

            // Turned off for the book: no review next time.
            doc.UpdateSettings("Review Claude's Changes", s => s.ReviewCloudAI = false);
            await w.Runner.Retag([0]);
            Assert.Null(w.Runner.Review);

            // Stop Sending: the book's yes is gone and nothing more goes out.
            doc.SetCloudTextAllowed(false);
            int sent = fake.Sent.Count;
            await w.Runner.TagSpeakers();
            Assert.Equal(ClaudeException.BookNotAllowed, w.Runner.Failure);
            Assert.Equal(sent, fake.Sent.Count);
            w.Close();
        }
        finally
        {
            account.Disconnect();
            Assert.False(account.IsSetUp);
            Environment.SetEnvironmentVariable("INNERVOICE_MODEL", null);
            ClaudeAccount.TestHttp = null;
            Assert.False(ModelManager.Shared.SelectionIsClaude); // Disconnect cleared the choice
            SnapSettings("Settings-AI-Claude-SetUp.png");
            if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is { } dir)
            {
                var setup = SettingsWindow.ClaudeSetupDialog();
                setup.Show();
                for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
                setup.CaptureRenderedFrame()?.Save(Path.Combine(dir, "SetUpClaude.png"), (int?)null);
                setup.Close();
            }
        }
    }
}
