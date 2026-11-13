using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.AI;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.AI;

namespace InnerVoice.App.Windows;

/// The Book menu's AI commands and the bar that shows their progress.
public sealed partial class ProjectWindow
{
    AIRunner? runner;
    public AIRunner Runner => runner ??= MakeRunner();
    readonly Border aiHost = new();

    AIRunner MakeRunner()
    {
        var r = new AIRunner(Document);
        r.Changed += RefreshAIBar;
        return r;
    }

    void AddAIMenu(MenuItem book)
    {
        book.Items.Add(Item("_Tag Speakers", "Ctrl+Shift+T", () => _ = TagSpeakers()));
        book.Items.Add(Item("_Build Summaries", "Ctrl+Shift+B", () => _ = BuildSummaries()));
        book.Items.Add(Item("Sto_p", "Ctrl+OemPeriod", () => runner?.Stop()));
        book.Items.Add(new Separator());
        book.Items.Add(Item("Choose a _Model…", null, () => SettingsWindow.Show(this, "AI")));
        // Claude, per book: shown once Claude is chosen or the book has said yes.
        var review = Item("Review Claude's Changes", null, () =>
        {
            bool on = !Document.Project.Settings.ReviewsCloudAI;
            Document.UpdateSettings("Review Claude's Changes", s => s.ReviewCloudAI = on ? null : false);
        });
        review.ToggleType = MenuItemToggleType.CheckBox;
        ToolTip.SetTip(review, "After Tag Speakers runs with Claude, list every line it changed so you can keep or undo each one");
        var stop = Item("Stop Sending This Book to Claude", null, () => Document.SetCloudTextAllowed(false));
        book.SubmenuOpened += (_, _) =>
        {
            bool claude = ModelManager.Shared.SelectionIsClaude || Document.Project.Settings.CloudTextAllowed is not null;
            review.IsVisible = claude;
            review.IsChecked = Document.Project.Settings.ReviewsCloudAI;
            stop.IsVisible = Document.Project.Settings.CloudTextAllowed is not null;
            stop.IsEnabled = !Runner.IsRunning;
        };
        book.Items.Add(review);
        book.Items.Add(stop);
    }

    public async Task TagSpeakers()
    {
        if (!await EnsureModel() || !await ClaudeAllowed()) return;
        await Runner.TagSpeakers();
        if (Runner.Review is { } r)
        {
            Runner.Review = null;
            await AIReviewWindow.Show(this, Document, r);
        }
    }

    public async Task BuildSummaries()
    {
        if (!await EnsureModel() || !await ClaudeAllowed()) return;
        await Runner.BuildSummaries();
    }

    /// With Claude chosen, a book that hasn't said yes asks once (the narrator just clicked,
    /// so a question is expected). Any other model: always true. Test runs never send.
    async Task<bool> ClaudeAllowed()
    {
        if (!ModelManager.Shared.SelectionIsClaude || Document.Project.Settings.CloudTextAllowed is not null) return true;
        if (AppPaths.IsTestRun) return false;
        if (!await Dialogs.Confirm(this, Claude.BookQuestion, Claude.BookQuestionDetail, "Send to Claude", destructive: false, cancel: "Don't Send"))
            return false;
        Document.SetCloudTextAllowed(true);
        return true;
    }

    /// No model yet: say so and open Settings › AI.
    async Task<bool> EnsureModel()
    {
        if (ModelManager.Shared.IsReady) return true;
        if (await Dialogs.Confirm(this, "Choose a Model First", "Tag Speakers and Build Summaries need an AI model. Settings › AI lists the ones that suit this PC, with one marked Best for this PC, and Claude, which runs in the cloud.", "Choose a Model", destructive: false))
            SettingsWindow.Show(this, "AI");
        return false;
    }

    /// Progress for the run in progress, and the result once it ends.
    void RefreshAIBar()
    {
        var r = runner;
        if (r is null) return;
        if (r.Current is { } s)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = s.Progress, Width = 260, VerticalAlignment = VerticalAlignment.Center };
            var text = Ui.Text(s.Message, 13, maxLines: 1);
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Margin = new Thickness(Look.Lg, Look.Sm) };
            var right = Ui.Row(Look.Md, bar, Ui.Button("Stop", r.Stop, tip: "Stop and keep what's done (Ctrl+.)"));
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(text);
            aiHost.Child = new Border { Child = row, Background = Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.18 : 0.08)) };
        }
        else if (r.Failure is { } f)
        {
            aiHost.Child = Banner(f, Look.Attention, () => { r.Failure = null; aiHost.Child = null; }, extra: f.Contains("Settings") ? Ui.Button("Open Settings", () => SettingsWindow.Show(this, "AI")) : null);
        }
        else if (r.Finished is { } done)
        {
            aiHost.Child = Banner(done, Look.Recorded, () => { r.Finished = null; aiHost.Child = null; },
                extra: Document.Project.LinesNeedingYou > 0 ? Ui.Button("Review", () => Workspace.Go(Pages.PageKind.Review)) : null);
        }
        else aiHost.Child = null;
        RefreshChrome();
    }

    static Control Banner(string text, Color color, Action dismiss, Control? extra = null)
    {
        var row = new DockPanel { Margin = new Thickness(Look.Lg, Look.Sm) };
        var right = Ui.Row(Look.Sm);
        if (extra is not null) right.Children.Add(extra);
        right.Children.Add(Ui.Button("OK", dismiss));
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var t = Ui.Text(text, 13, wrap: TextWrapping.Wrap);
        t.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(t);
        return new Border { Child = row, Background = Look.Brush(color.WithAlpha(Look.IsDark ? 0.22 : 0.10)) };
    }
}
