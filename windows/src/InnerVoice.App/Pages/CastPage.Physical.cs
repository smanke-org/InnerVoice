using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// The collapsed "Physical Description" section on a character's page, like the Mac's
/// PhysicalProfileSection: a summary, a row per field with an "Inferred" badge to click once
/// checked, changes during the story, and continuity notes to check or dismiss.
public sealed partial class CastPage
{
    bool physicalExpanded;
    int newChangeChapter = -1;
    int newChangeField = (int)PhysicalField.Hair;

    static readonly IBrush OrangeInk = new SolidColorBrush(Look.Retake);

    void EditPhysical(BookCharacter c, string name, Action<PhysicalProfile> body) =>
        Document.UpdateCharacter(c.Id, name, x =>
        {
            var p = x.Physical ?? new PhysicalProfile();
            body(p);
            x.Physical = p.IsEmpty ? null : p;
        });

    string ChapterTitle(int i)
    {
        var t = i >= 0 && i < Document.Project.Chapters.Count ? Document.Project.Chapters[i].Title : "";
        return t.Length == 0 ? $"Chapter {i + 1}" : t;
    }

    Control InferredBadge(bool inferred, Action markChecked)
    {
        if (!inferred) return new Border { Width = 1, Height = 1 };
        var b = new Button
        {
            Content = "Inferred", FontSize = 11, Padding = new Thickness(8, 1), MinHeight = 0, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(64, 250, 204, 21)),
        };
        ToolTip.SetTip(b, "The AI interpreted or guessed this; the book doesn’t say it outright. Click once you’ve checked it.");
        b.Click += (_, _) => markChecked();
        return b;
    }

    Control PhysicalSection(BookCharacter c)
    {
        var profile = c.Physical ?? new PhysicalProfile();
        var body = new StackPanel { Spacing = Look.Sm };
        if (profile.IsEmpty)
            body.Children.Add(Ui.Caption($"Build Summaries writes this from what the book says about how {c.FirstName} looks. You can also fill it in yourself.", TextWrapping.Wrap));

        var summary = new DraftTextBox(profile.Summary, v => EditPhysical(c, "Edit Physical Description", p => { p.Summary = v; p.SummaryInferred = false; }),
            multiline: true) { MinHeight = 56, HorizontalAlignment = HorizontalAlignment.Stretch };
        var summaryHead = Ui.Row(Look.Sm, Ui.Caption("Description"),
            profile.Summary.Trim().Length > 0
                ? InferredBadge(profile.SummaryInferred, () => EditPhysical(c, "Mark as Checked", p => p.SummaryInferred = false))
                : new Border { Width = 1, Height = 1 });
        body.Children.Add(Ui.Column(Look.Xs, summaryHead, summary));

        foreach (var f in PhysicalFields.All)
        {
            var field = f;
            var v = profile.Value(field);
            var text = new DraftTextBox(v.Text, t => EditPhysical(c, $"Edit {field.Title()}", p => p.Set(field, new PhysicalValue(t, false))))
            { Width = 360 };
            var title = Ui.Caption(field.Title());
            title.Width = 150;
            title.VerticalAlignment = VerticalAlignment.Center;
            var badge = v.Text.Length > 0
                ? InferredBadge(v.Inferred, () => EditPhysical(c, "Mark as Checked", p => { if (p.Values.TryGetValue(field.Raw(), out var x)) x.Inferred = false; }))
                : new Border { Width = 1, Height = 1 };
            body.Children.Add(Ui.Row(Look.Sm, title, text, badge));
        }
        body.Children.Add(Ui.Caption("“Inferred” marks the AI’s interpretation, not something the book states; click it once you’ve checked. Once you edit this description, Build Summaries leaves it alone.", TextWrapping.Wrap));

        body.Children.Add(Ui.Headline("Changes During the Story"));
        foreach (var change in profile.OrderedChanges)
        {
            var id = change.Id;
            var row = Ui.Row(Look.Sm,
                new TextBlock { Text = change.Chapter is { } ch ? ChapterTitle(ch) : "Chapter unknown", Width = 150, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = $"{change.Field.Title()}: {change.Text}", TextWrapping = TextWrapping.Wrap, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center });
            if (change.Inferred) row.Children.Add(Ui.Caption("Inferred"));
            row.Children.Add(Ui.Button("Delete", () => EditPhysical(c, "Delete Change", p => p.Changes.RemoveAll(x => x.Id == id)), tip: "Delete this change"));
            body.Children.Add(row);
        }
        var chapters = new ComboBox { Width = 160 };
        chapters.ItemsSource = new[] { "Chapter…" }.Concat(Enumerable.Range(0, Document.Project.Chapters.Count).Select(ChapterTitle)).ToList();
        chapters.SelectedIndex = Math.Clamp(newChangeChapter + 1, 0, Document.Project.Chapters.Count);
        chapters.SelectionChanged += (_, _) => newChangeChapter = chapters.SelectedIndex - 1;
        var fields = new ComboBox { Width = 170, ItemsSource = PhysicalFields.All.Select(f => f.Title()).ToList(), SelectedIndex = newChangeField };
        fields.SelectionChanged += (_, _) => newChangeField = Math.Max(0, fields.SelectedIndex);
        var newText = new TextBox { PlaceholderText = "What changes, e.g. Cuts her hair short", Width = 300 };
        void Add()
        {
            var t = (newText.Text ?? "").Trim();
            if (t.Length == 0) return;
            int? chapter = newChangeChapter >= 0 ? newChangeChapter : null;
            var field = PhysicalFields.All[Math.Clamp(newChangeField, 0, PhysicalFields.All.Length - 1)];
            EditPhysical(c, "Add Change", p => p.Changes.Add(new PhysicalChange { Chapter = chapter, Field = field, Text = t }));
        }
        newText.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Add(); e.Handled = true; } };
        body.Children.Add(Ui.Row(Look.Sm, chapters, fields, newText, Ui.Button("Add", Add)));

        if (profile.Flags.Count > 0)
        {
            body.Children.Add(Ui.Headline("Continuity to Check"));
            foreach (var flag in profile.Flags)
            {
                var id = flag.Id;
                body.Children.Add(Ui.Row(Look.Sm,
                    new TextBlock { Text = "⚠", Foreground = OrangeInk, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = $"{flag.Field.Title()}: {flag.Text}", Foreground = OrangeInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center },
                    Ui.Button("Dismiss", () => EditPhysical(c, "Dismiss Continuity Note", p => p.Flags.RemoveAll(x => x.Id == id)))));
            }
        }

        var header = Ui.Row(Look.Sm, Ui.Headline("Physical Description"));
        if (profile.Flags.Count > 0)
        {
            var count = new TextBlock { Text = $"⚠ {profile.Flags.Count}", Foreground = OrangeInk, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(count, "Continuity to check");
            header.Children.Add(count);
        }
        var expander = new Expander { Header = header, Content = body, IsExpanded = physicalExpanded, HorizontalAlignment = HorizontalAlignment.Stretch };
        expander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) physicalExpanded = expander.IsExpanded; };
        return expander;
    }
}
