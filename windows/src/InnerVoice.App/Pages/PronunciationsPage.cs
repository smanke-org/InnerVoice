// Pronunciations (the Mac's Views/PronunciationsPage.swift): the narrator teaches the AI
// voices how to say hard words. Shown only for books made with AI voices.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using InnerVoice.App.AI;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

public sealed class PronunciationsPage : Page
{
    public override PageKind Kind => PageKind.Pronunciations;

    /// Term, Say It As (with the play button), Notes, Count, Match Case, Confirmed.
    const string Columns = "1.6*,1.9*,2.2*,56,90,84";
    const string Explainer = "AI voices guess at names and unusual words from their spelling. Type how a word should sound in Say It As, with capitals for the stressed part (BAR-nuh-bee), and every voice says it that way from then on. One entry also covers plurals and possessives, like Barnaby's. Click the play button to hear it in the narrator's voice.";

    readonly TextBox search = new() { PlaceholderText = "Find a term", Width = 170 };
    readonly ToggleButton showDismissed = new() { Content = "Show Dismissed" };
    readonly Button suggest;
    readonly StackPanel toolbar;
    readonly ListBox list;
    readonly HashSet<Guid> selection = [];
    bool updating, pendingRefresh;
    DockPanel? layout;

    public PronunciationsPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        search.TextChanged += (_, _) => Refresh(false);
        showDismissed.IsCheckedChanged += (_, _) => Refresh(false);
        ToolTip.SetTip(showDismissed, "Show terms you said need no entry");
        suggest = Ui.Button("Suggest Spellings (Cloud)", SuggestSpellings,
            tip: "Cloud: Claude suggests a Say It As for each term without one. Sends each term and the sentence it first appears in to Anthropic. You confirm each spelling.");
        toolbar = Ui.Row(Look.Sm, search, showDismissed,
            Ui.Button("Find Terms", FindTerms, tip: "Look through the manuscript for unusual names and words again"),
            Ui.Button("Add Term", Add, tip: "Add a term to the guide"),
            suggest);

        list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            // The template is also asked with no item while containers are made.
            ItemTemplate = new FuncDataTemplate<Pronunciation>((p, _) => p is null ? new Panel() : Row(p)),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(Look.Lg, 3)) },
        });
        list.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            selection.Clear();
            foreach (var p in list.SelectedItems?.OfType<Pronunciation>() ?? []) selection.Add(p.Id);
        };
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            foreach (var item in MenuItems()) menu.Items.Add(item);
            if (menu.Items.Count == 0) e.Cancel = true;
        };
        list.ContextMenu = menu;
        Refresh(true);
    }

    public override Control Toolbar
    {
        get
        {
            suggest.IsVisible = ClaudeAccount.Shared.IsSetUp;
            suggest.IsEnabled = Window?.Runner.IsRunning != true;
            return toolbar;
        }
    }

    public override string Subtitle
    {
        get
        {
            var all = Document.Project.Pronunciations;
            return $"{all.Count(p => p.SayItAs.Length > 0)} of {all.Count(p => !p.Dismissed)} done";
        }
    }

    /// The selected entries (for tests).
    public IReadOnlyCollection<Guid> Selected => selection;
    /// The entries listed now, in order (for tests).
    public IReadOnlyList<Pronunciation> Listed => list.ItemsSource as IReadOnlyList<Pronunciation> ?? [];

    InnerVoice.App.Windows.ProjectWindow? Window => TopLevel.GetTopLevel(this) as InnerVoice.App.Windows.ProjectWindow;

    /// Non-dismissed entries (all, with Show Dismissed), matching the search: the narrator's
    /// own first, then the most used, then by term.
    List<Pronunciation> Rows()
    {
        var q = (search.Text ?? "").Trim();
        bool dismissed = showDismissed.IsChecked == true;
        return Document.Project.Pronunciations
            .Where(p => dismissed || !p.Dismissed)
            .Where(p => q.Length == 0 || p.Term.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.SayItAs.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(p => p.AddedByUser)
            .ThenByDescending(p => p.Occurrences)
            .ThenBy(p => p.Term, StringComparer.Ordinal)
            .ToList();
    }

    public override void Shown() => TakeFocus();

    public override void Refresh(bool textChanged)
    {
        // Don't pull a text field out from under the narrator while they type in it.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox tb && tb.IsVisible &&
            Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(list, tb))
        {
            if (!pendingRefresh)
            {
                pendingRefresh = true;
                tb.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { pendingRefresh = false; Refresh(false); });
            }
            return;
        }
        var rows = Rows();
        if (rows.Count == 0 && (search.Text ?? "").Trim().Length == 0)
        {
            var actions = Ui.Row(Look.Sm, Ui.Button("Add Term", Add), Ui.Button("Find Terms", FindTerms));
            Content = Ui.Empty("No Pronunciations",
                "When an AI voice says a word wrong, add it here with a spelling of how it should sound. Every voice uses it from then on. InnerVoice also lists names, places and made-up words it finds.",
                actions);
            return;
        }
        updating = true;
        list.ItemsSource = rows;
        list.SelectedItems?.Clear();
        foreach (var p in rows.Where(p => selection.Contains(p.Id))) list.SelectedItems?.Add(p);
        updating = false;
        layout ??= Layout();
        if (Content != layout) Content = layout;
    }

    DockPanel Layout()
    {
        var dock = new DockPanel();
        var explainer = new Border
        {
            Background = Look.Brush(Look.Chrome),
            BorderBrush = Look.Brush(Look.Separator),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(Look.Lg, Look.Sm),
            Child = Ui.Column(2,
                Ui.Text("Teach the AI voices how to say hard words", 13, FontWeight.SemiBold),
                Ui.Text(Explainer, 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 3)),
        };
        DockPanel.SetDock(explainer, Dock.Top);
        dock.Children.Add(explainer);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = Look.Sm, Margin = new Thickness(Look.Lg, Look.Sm, Look.Lg, Look.Xs) };
        string[] titles = ["Term", "Say It As", "Notes", "Count", "Match Case", "Confirmed"];
        for (int i = 0; i < titles.Length; i++)
        {
            var t = Ui.Caption(titles[i]);
            t.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(list);
        return dock;
    }

    Control Row(Pronunciation p)
    {
        var id = p.Id;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = Look.Sm, Background = Brushes.Transparent };

        var term = Field(p.Term, "Term", v => Update(id, "Edit Pronunciation", x => x.Term = v));
        term.FontWeight = FontWeight.Medium;
        row.Children.Add(term);

        var say = Field(p.SayItAs, "e.g. BAR-nuh-bee", v => Update(id, "Edit Pronunciation", x => x.SayItAs = v));
        var sayCell = new DockPanel();
        if (Document.Project.ShowsVoices)
        {
            // Not focusable: pressing it leaves the field being typed in alone, and it says
            // what's typed there now, saved or not.
            var play = new Button
            {
                Content = "▶", Focusable = false, Padding = new Thickness(6, 2), Margin = new Thickness(Look.Xs, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(play, p.SayItAs.Length == 0 ? "Hear how the narrator's voice says it now" : "Hear it in the narrator's voice, with this spelling");
            Avalonia.Automation.AutomationProperties.SetName(play, "Hear it");
            play.Click += (_, _) =>
            {
                if (Document.Project.Pronunciations.FirstOrDefault(x => x.Id == id) is not { } now) return;
                var heard = now.Copy();
                heard.SayItAs = say.Text ?? now.SayItAs;
                Audition.Hear(heard, Document);
            };
            DockPanel.SetDock(play, Dock.Right);
            sayCell.Children.Add(play);
        }
        sayCell.Children.Add(say);
        Grid.SetColumn(sayCell, 1);
        row.Children.Add(sayCell);

        var notes = Field(p.Notes, "Origin, stress, source", v => Update(id, "Edit Pronunciation", x => x.Notes = v));
        notes.Foreground = Look.Brush(Look.SecondaryInk);
        Grid.SetColumn(notes, 2);
        row.Children.Add(notes);

        var count = Ui.Caption(p.Occurrences > 0 ? $"{p.Occurrences}" : "—");
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 3);
        row.Children.Add(count);

        var matchCase = Check(p.MatchCase, "Only when the capitals match, so \"Will\" the name doesn't change \"will\"", "Match Case",
            on => Update(id, "Match Case", x => x.MatchCase = on));
        Grid.SetColumn(matchCase, 4);
        row.Children.Add(matchCase);

        var confirmed = Check(p.Confirmed, "Checked with the author or rights holder", "Confirmed", on =>
        {
            if (on && Document.Project.Pronunciations.FirstOrDefault(x => x.Id == id) is { Confirmed: false }) Activity.Record(ActivityKey.Pronunciations);
            Update(id, "Confirm Pronunciation", x => x.Confirmed = on);
        });
        Grid.SetColumn(confirmed, 5);
        row.Children.Add(confirmed);

        if (p.Dismissed) row.Opacity = 0.6;
        // Typing in a field picks its row, so the right-click menu acts on it.
        row.AddHandler(GotFocusEvent, (_, _) => Select(id), handledEventsToo: true);
        return row;
    }

    static DraftTextBox Field(string text, string placeholder, Action<string> commit) => new(text, commit, watermark: placeholder)
    {
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    CheckBox Check(bool on, string tip, string name, Action<bool> set)
    {
        // Not focusable, like the play button: a click while typing elsewhere keeps that field.
        var box = new CheckBox { IsChecked = on, Focusable = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(box, tip);
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.IsCheckedChanged += (_, _) => { if (!updating) set(box.IsChecked == true); };
        return box;
    }

    void Select(Guid id)
    {
        if (selection.Count == 1 && selection.Contains(id)) return;
        selection.Clear();
        selection.Add(id);
        updating = true;
        list.SelectedItems?.Clear();
        if (list.ItemsSource is IEnumerable<Pronunciation> rows && rows.FirstOrDefault(p => p.Id == id) is { } item) list.SelectedItems?.Add(item);
        updating = false;
    }

    /// Selects an entry (Fix Pronunciation… in the manuscript), showing it even when it was
    /// dismissed or the search hides it.
    void TakeFocus()
    {
        if (Workspace.FocusPronunciation is not { } id) return;
        Workspace.FocusPronunciation = null;
        search.Text = "";
        if (Document.Project.Pronunciations.FirstOrDefault(p => p.Id == id) is { Dismissed: true }) showDismissed.IsChecked = true;
        selection.Clear();
        selection.Add(id);
        Refresh(false);
        if (list.ItemsSource is IEnumerable<Pronunciation> rows && rows.FirstOrDefault(p => p.Id == id) is { } item)
            list.ScrollIntoView(item);
    }

    /// The right-click menu, for the selected rows.
    IEnumerable<Control> MenuItems()
    {
        var ids = selection.Where(id => Document.Project.Pronunciations.Any(p => p.Id == id)).ToList();
        if (ids.Count == 0) yield break;
        if (Document.Project.Pronunciations.FirstOrDefault(p => p.Id == ids[0]) is { FirstParagraphID: { } pid })
            yield return Item("Show First Use in Manuscript", () => Workspace.Show(pid));
        var set = ids.ToHashSet();
        yield return Item(ids.Count > 1 ? $"Dismiss {ids.Count} Terms" : "Dismiss", () =>
            Document.Change("Dismiss Terms", p => { foreach (var x in p.Pronunciations.Where(x => set.Contains(x.Id))) x.Dismissed = true; }));
        yield return Item("Delete", () =>
        {
            selection.ExceptWith(set);
            Document.Change("Delete Terms", p => p.Pronunciations.RemoveAll(x => set.Contains(x.Id)));
        });
    }

    static MenuItem Item(string header, Action action)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => action();
        return m;
    }

    void Update(Guid id, string name, Action<Pronunciation> edit) =>
        Document.Change(name, p => { if (p.Pronunciations.FirstOrDefault(x => x.Id == id) is { } x) edit(x); });

    void Add()
    {
        var p = new Pronunciation { Term = "New Term", AddedByUser = true };
        search.Text = "";
        selection.Clear();
        selection.Add(p.Id);
        Document.Change("Add Term", project => project.Pronunciations.Add(p.Copy()));
        if (list.ItemsSource is IEnumerable<Pronunciation> rows && rows.FirstOrDefault(x => x.Id == p.Id) is { } item)
            list.ScrollIntoView(item);
    }

    void FindTerms() => Document.Change("Find Terms", p => PronunciationFinder.Refresh(p, SpellCheck.IsKnown));

    void SuggestSpellings()
    {
        if (Window is { } w) _ = w.RunClaude(AIRunner.ClaudeJob.Spellings);
    }
}
