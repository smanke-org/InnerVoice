using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Controls;

/// The Steps bar across the top of the window: Manuscript, Speakers, Review, Characters,
/// Teleprompter, each with how far along it is; the next step is highlighted. Right-click a
/// step to mark it done (skip) or bring it back; the Teleprompter is done only when marked.
public sealed class StepsBar : Border
{
    readonly ProjectDocument document;
    readonly Action<GuidedStep> go;

    public StepsBar(ProjectDocument document, Action<GuidedStep> go)
    {
        this.document = document;
        this.go = go;
        BorderBrush = Look.Brush(Look.Separator);
        BorderThickness = new Thickness(0, 0, 0, 1);
        Background = Look.Brush(Look.Chrome);
        Refresh();
    }

    public void Refresh()
    {
        var p = document.Project;
        int speaking = p.Characters.Count(c => (document.Index.StatsFor(c.Id)?.Lines ?? 0) > 0);
        // A book with voices has Voices and Listen in place of the Teleprompter (as on the Mac).
        int voicesNeeded = 0;
        if (p.ShowsVoices)
        {
            var engine = Speech.VoiceSetup.EngineInUse(p);
            voicesNeeded = p.Characters.Count(c => (document.Index.StatsFor(c.Id)?.Lines ?? 0) > 0 && p.VoiceNeedsYou(c, engine));
        }
        var steps = new GuidedSteps(p, speaking, narration: !p.ShowsVoices, voicesNeeded: voicesNeeded);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.Xs, Margin = new Thickness(Look.Md, Look.Sm) };
        foreach (var s in steps.All)
        {
            bool current = steps.Current?.Step == s.Step;
            var circle = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = Look.Brush(s.IsDone ? Look.Recorded.WithAlpha(0.9) : current ? Look.Accent : Look.Separator),
                Child = new TextBlock
                {
                    Text = s.IsDone ? (s.IsSkipped ? "–" : "✓") : $"{s.Number}", FontSize = 11, FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var label = Ui.Column(0, Ui.Text(GuidedSteps.Title(s.Step), 12, current ? FontWeight.SemiBold : FontWeight.Normal),
                Ui.Text(s.IsSkipped ? "Skipped" : s.Detail, 11, brush: Look.Brush(Look.SecondaryInk)));
            var content = Ui.Row(Look.Sm, circle, label);
            var button = new Button
            {
                Content = content, Background = current ? Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.25 : 0.10)) : Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(Look.Sm, 4), CornerRadius = new CornerRadius(8),
            };
            var step = s.Step;
            button.Click += (_, _) => go(step);
            var menu = new ContextMenu();
            var mark = new MenuItem { Header = s.IsMarked ? "Bring Back" : "Mark Done" };
            mark.Click += (_, _) => document.UpdateSettings(s.IsMarked ? "Bring Back Step" : "Skip Step", st => GuidedSteps.MarkStep(st, step, !s.IsMarked));
            menu.Items.Add(mark);
            button.ContextMenu = menu;
            ToolTip.SetTip(button, $"{GuidedSteps.Title(s.Step)}: {s.Detail}. Right-click to mark it done.");
            row.Children.Add(button);
            if (s.Number < steps.All.Count) row.Children.Add(new TextBlock { Text = "›", Foreground = Look.Brush(Look.TertiaryInk), VerticalAlignment = VerticalAlignment.Center });
        }
        Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
}
