// Pronunciations (the Mac's Views/PronunciationsPage.swift): how to say hard words, in every
// book type. One combined list of the two lists (PronunciationLists in Core): This Book's
// entries, edited here, and This PC's (edited in Settings › Pronunciations), each row tagged
// with where it comes from. "Another Computer" rows are the book's copy of the list on the
// computer it was saved on, for words This PC's list doesn't have.

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

public enum PronunciationFilter { All, Book, Computer }

/// A row on the Pronunciations page: one of the book's entries, or one of This PC's.
public sealed record PronunciationRow(Guid Id, PronunciationRow.Source Kind, Pronunciation? Book, ListedPronunciation? Listed)
{
    public enum Source
    {
        /// The book's own entry.
        Book,
        /// This PC's, used by this book.
        Computer,
        /// From another computer: kept in the book's copy, not in This PC's list.
        BookCopy,
        /// This PC's, but the book has its own entry for the word.
        Replaced,
        /// This PC's, but the book has This PC's list turned off.
        NotUsed,
    }

    public string Term => Book?.Term ?? Listed?.Term ?? "";
    public string SayItAs => Book?.SayItAs ?? Listed?.SayItAs ?? "";
    public string Notes => Book?.Notes ?? Listed?.Notes ?? "";
    public bool MatchCase => Book?.MatchCase ?? Listed?.MatchCase ?? false;
    public bool IsUsed => Kind is Source.Book or Source.Computer or Source.BookCopy;
    public Pronunciation AsPronunciation => Book ?? new Pronunciation { Term = Term, SayItAs = SayItAs, MatchCase = MatchCase };

    /// The tag in the List column, and its hover help.
    public (string Text, string Help) Tag => Kind switch
    {
        Source.Book => ("This Book", "Saved in this book"),
        Source.Computer => ("This PC", "From This PC’s list, used by every book on this PC"),
        Source.BookCopy => ("Another Computer", "From the list on the computer this book was saved on. This book keeps a copy, so it sounds the same here. Right-click to add it to This PC’s list."),
        Source.Replaced => ("This PC · Not Used", "This book has its own entry for this word, and the book’s wins"),
        _ => ("This PC · Off", "This book has Use This PC’s Pronunciations turned off"),
    };
}

public sealed class PronunciationsPage : Page
{
    public override PageKind Kind => PageKind.Pronunciations;

    /// Term, List, Say It As (with the play button), Notes, Count, Match Case, Confirmed.
    const string Columns = "1.5*,130,1.8*,2*,50,84,78";
    public const string TwoLists = "This Book’s entries are saved in the book. This PC’s are used by every book on this PC. When both have a word, this book’s wins.";

    readonly TextBox search = new() { PlaceholderText = "Find a term", Width = 170 };
    readonly ToggleButton showDismissed = new() { Content = "Show Dismissed" };
    readonly Button suggest;
    readonly StackPanel toolbar;
    readonly ListBox list;
    readonly HashSet<Guid> selection = [];
    readonly ToggleButton[] filterButtons;
    readonly CheckBox useComputer = new() { Content = "Use This PC’s Pronunciations" };
    readonly Button addBook;
    readonly TextBlock heading = Ui.Text("", 13, FontWeight.SemiBold);
    readonly TextBlock explainer = Ui.Text("", 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 3);
    PronunciationFilter filter = PronunciationFilter.All;
    bool updating, pendingRefresh;
    DockPanel? layout;
    static ComputerPronunciations Computer => ComputerPronunciations.Shared;

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

        filterButtons = [new ToggleButton(), new ToggleButton(), new ToggleButton()];
        for (int i = 0; i < filterButtons.Length; i++)
        {
            var f = (PronunciationFilter)i;
            filterButtons[i].Padding = new Thickness(10, 3);
            filterButtons[i].Click += (_, _) => Show(f);
        }
        useComputer.IsCheckedChanged += (_, _) =>
        {
            if (updating) return;
            bool on = useComputer.IsChecked == true;
            if (on == Document.Project.Settings.UsesComputerPronunciations) return;
            SetUsesComputer(Document, on);
        };
        ToolTip.SetTip(useComputer, "On: this book uses This PC’s list for words it has no entry of its own for. Turn it off for a book whose names are said differently.");
        addBook = Ui.Button("Add This Book’s to This PC…", () => _ = ComputerPronunciations.AddBook(this, Document),
            tip: "Put this book’s entries in This PC’s list, so every book on this PC uses them");

        list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            // The template is also asked with no item while containers are made.
            ItemTemplate = new FuncDataTemplate<PronunciationRow>((r, _) => r is null ? new Panel() : Row(r)),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(Look.Lg, 3)) },
        });
        list.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            selection.Clear();
            foreach (var r in list.SelectedItems?.OfType<PronunciationRow>() ?? []) selection.Add(r.Id);
        };
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            foreach (var item in MenuItems()) menu.Items.Add(item);
            if (menu.Items.Count == 0) e.Cancel = true;
        };
        list.ContextMenu = menu;
        // This PC's list changes from Settings, other books and the agent: redraw while shown.
        AttachedToVisualTree += (_, _) => Computer.Changed += ComputerChanged;
        DetachedFromVisualTree += (_, _) => Computer.Changed -= ComputerChanged;
        Refresh(true);
    }

    void ComputerChanged() => Refresh(false);

    /// "Use This PC’s Pronunciations" for a book, one undo step (the page's checkbox and the
    /// Book menu).
    public static void SetUsesComputer(ProjectDocument document, bool on) =>
        document.UpdateSettings(on ? "Use This PC’s Pronunciations" : "Don’t Use This PC’s Pronunciations", s => s.UsesComputerPronunciations = on);

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

    /// The selected rows (for tests).
    public IReadOnlyCollection<Guid> Selected => selection;
    /// The rows listed now, in order (for tests).
    public IReadOnlyList<PronunciationRow> Rows => list.ItemsSource as IReadOnlyList<PronunciationRow> ?? [];
    /// The book's entries listed now, in order (for tests).
    public IReadOnlyList<Pronunciation> Listed => Rows.Where(r => r.Book is not null).Select(r => r.Book!).ToList();
    public PronunciationFilter Filter => filter;

    InnerVoice.App.Windows.ProjectWindow? Window => TopLevel.GetTopLevel(this) as InnerVoice.App.Windows.ProjectWindow;

    /// Non-dismissed entries (all, with Show Dismissed): the narrator's own first, then the
    /// most used, then by term.
    List<PronunciationRow> BookRows()
    {
        bool dismissed = showDismissed.IsChecked == true;
        return Document.Project.Pronunciations
            .Where(p => dismissed || !p.Dismissed)
            .OrderByDescending(p => p.AddedByUser)
            .ThenByDescending(p => p.Occurrences)
            .ThenBy(p => p.Term, StringComparer.Ordinal)
            .Select(p => new PronunciationRow(p.Id, PronunciationRow.Source.Book, p, null))
            .ToList();
    }

    /// This PC's entries as this book sees them: used, from another computer (the book's copy),
    /// replaced by the book's own entry, or not used (the book turned the list off).
    List<PronunciationRow> ComputerRows()
    {
        var project = Document.Project;
        var own = PronunciationLists.BookKeys(project);
        bool on = project.Settings.UsesComputerPronunciations;
        var rows = new List<PronunciationRow>();
        foreach (var e in Computer.List.Sorted)
        {
            var kind = !on ? PronunciationRow.Source.NotUsed : own.Contains(e.Key) ? PronunciationRow.Source.Replaced : PronunciationRow.Source.Computer;
            rows.Add(new PronunciationRow(e.Id, kind, null, e));
        }
        if (on)
            foreach (var (e, source) in PronunciationLists.ComputerEntries(project, Computer.List))
                if (source == PronunciationSource.BookCopy)
                    rows.Add(new PronunciationRow(e.Id, own.Contains(e.Key) ? PronunciationRow.Source.Replaced : PronunciationRow.Source.BookCopy, null, e));
        return rows;
    }

    List<PronunciationRow> AllRows()
    {
        var all = filter switch
        {
            PronunciationFilter.Book => BookRows(),
            PronunciationFilter.Computer => ComputerRows(),
            _ => [.. BookRows(), .. ComputerRows().Where(r => r.Kind is PronunciationRow.Source.Computer or PronunciationRow.Source.BookCopy)],
        };
        var q = (search.Text ?? "").Trim();
        return q.Length == 0 ? all : all.Where(r => PronunciationText.Contains(r.Term, q) || PronunciationText.Contains(r.SayItAs, q)).ToList();
    }

    /// Shows All, This Book's or This PC's.
    public void Show(PronunciationFilter f)
    {
        filter = f;
        Refresh(false);
    }

    /// This PC's list may have changed while another page showed.
    public override void Shown()
    {
        Refresh(false);
        TakeFocus();
    }

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
        if (Document.Project.Pronunciations.Count == 0 && Computer.List.Entries.Count == 0 && (search.Text ?? "").Trim().Length == 0)
        {
            var actions = Ui.Row(Look.Sm, Ui.Button("Add Term", Add), Ui.Button("Find Terms", FindTerms));
            Content = Ui.Empty("No Pronunciations",
                "When an AI voice says a word wrong, add it here with a spelling of how it should sound. Every voice uses it from then on. InnerVoice also lists names, places and made-up words it finds.",
                actions);
            return;
        }
        var rows = AllRows();
        updating = true;
        list.ItemsSource = rows;
        list.SelectedItems?.Clear();
        foreach (var r in rows.Where(r => selection.Contains(r.Id))) list.SelectedItems?.Add(r);
        UpdateHeader();
        updating = false;
        layout ??= Layout();
        if (Content != layout) Content = layout;
    }

    void UpdateHeader()
    {
        var project = Document.Project;
        heading.Text = project.ShowsVoices ? "Teach the AI voices how to say hard words" : "How to say the hard words";
        explainer.Text = project.ShowsVoices
            ? "Type how a word should sound in Say It As, with capitals for the stressed part (BAR-nuh-bee), and every voice says it that way. One entry also covers plurals and possessives, like Barnaby's. Click the play button to hear it."
            : "Keep how each name and unusual word is said, with capitals for the stressed part (BAR-nuh-bee). Click the play button to hear it.";
        string[] titles = ["All", $"This Book ({project.Pronunciations.Count(p => !p.Dismissed)})", $"This PC ({Computer.List.Entries.Count})"];
        for (int i = 0; i < filterButtons.Length; i++)
        {
            filterButtons[i].Content = titles[i];
            filterButtons[i].IsChecked = (int)filter == i;
        }
        useComputer.IsChecked = project.Settings.UsesComputerPronunciations;
        addBook.IsEnabled = PronunciationLists.BookEntries(project).Count > 0;
    }

    DockPanel Layout()
    {
        var dock = new DockPanel();
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in filterButtons) filters.Children.Add(b);
        var editList = Ui.Button("Edit This PC’s List…", () => InnerVoice.App.Windows.SettingsWindow.Show(Window, "Pronunciations"),
            tip: "Add, change, import or export This PC’s pronunciations in Settings");
        var controls = new DockPanel { LastChildFill = false };
        var left = Ui.Row(Look.Md, filters, useComputer);
        DockPanel.SetDock(left, Dock.Left);
        controls.Children.Add(left);
        var right = Ui.Row(Look.Sm, addBook, editList);
        DockPanel.SetDock(right, Dock.Right);
        controls.Children.Add(right);
        var header = new Border
        {
            Background = Look.Brush(Look.Chrome),
            BorderBrush = Look.Brush(Look.Separator),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(Look.Lg, Look.Sm),
            Child = Ui.Column(Look.Sm,
                Ui.Column(2, heading, explainer, Ui.Text(TwoLists, 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 2)),
                controls),
        };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = Look.Sm, Margin = new Thickness(Look.Lg, Look.Sm, Look.Lg, Look.Xs) };
        string[] titles = ["Term", "List", "Say It As", "Notes", "Count", "Match Case", "Confirmed"];
        for (int i = 0; i < titles.Length; i++)
        {
            var t = Ui.Caption(titles[i]);
            t.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(t, i);
            columns.Children.Add(t);
        }
        DockPanel.SetDock(columns, Dock.Top);
        dock.Children.Add(columns);
        dock.Children.Add(list);
        return dock;
    }

    Control Row(PronunciationRow r)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = Look.Sm, Background = Brushes.Transparent };
        var (tagText, tagHelp) = r.Tag;
        var tag = Ui.Caption(tagText);
        tag.VerticalAlignment = VerticalAlignment.Center;
        if (r.Kind == PronunciationRow.Source.Book) tag.Foreground = Look.Brush(Look.Accent);
        ToolTip.SetTip(tag, tagHelp);
        Grid.SetColumn(tag, 1);
        row.Children.Add(tag);
        return r.Book is { } p ? BookRow(row, p) : ListedRow(row, r);
    }

    /// One of the book's entries: edited in place.
    Control BookRow(Grid row, Pronunciation p)
    {
        var id = p.Id;
        var term = Field(p.Term, "Term", v => Update(id, "Edit Pronunciation", x => x.Term = v));
        term.FontWeight = FontWeight.Medium;
        row.Children.Add(term);

        var say = Field(p.SayItAs, "e.g. BAR-nuh-bee", v => Update(id, "Edit Pronunciation", x => x.SayItAs = v));
        var sayCell = new DockPanel();
        // Not focusable: pressing it leaves the field being typed in alone, and it says what's
        // typed there now, saved or not.
        sayCell.Children.Add(PlayButton(p.SayItAs.Length == 0, () =>
        {
            if (Document.Project.Pronunciations.FirstOrDefault(x => x.Id == id) is not { } now) return;
            var heard = now.Copy();
            heard.SayItAs = say.Text ?? now.SayItAs;
            Audition.Hear(heard, Document);
        }));
        sayCell.Children.Add(say);
        Grid.SetColumn(sayCell, 2);
        row.Children.Add(sayCell);

        var notes = Field(p.Notes, "Origin, stress, source", v => Update(id, "Edit Pronunciation", x => x.Notes = v));
        notes.Foreground = Look.Brush(Look.SecondaryInk);
        Grid.SetColumn(notes, 3);
        row.Children.Add(notes);

        var count = Ui.Caption(p.Occurrences > 0 ? $"{p.Occurrences}" : "—");
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 4);
        row.Children.Add(count);

        var matchCase = Check(p.MatchCase, "Only when the capitals match, so \"Will\" the name doesn't change \"will\"", "Match Case",
            on => Update(id, "Match Case", x => x.MatchCase = on));
        Grid.SetColumn(matchCase, 5);
        row.Children.Add(matchCase);

        var confirmed = Check(p.Confirmed, "Checked with the author or rights holder", "Confirmed", on =>
        {
            if (on && Document.Project.Pronunciations.FirstOrDefault(x => x.Id == id) is { Confirmed: false }) Activity.Record(ActivityKey.Pronunciations);
            Update(id, "Confirm Pronunciation", x => x.Confirmed = on);
        });
        Grid.SetColumn(confirmed, 6);
        row.Children.Add(confirmed);

        if (p.Dismissed) row.Opacity = 0.6;
        // Typing in a field picks its row, so the right-click menu acts on it.
        row.AddHandler(GotFocusEvent, (_, _) => Select(id), handledEventsToo: true);
        return row;
    }

    /// One of This PC's (or the book's copy of another computer's): shown, not edited here.
    Control ListedRow(Grid row, PronunciationRow r)
    {
        var ink = Look.Brush(r.IsUsed ? Look.Ink : Look.SecondaryInk);
        var term = Ui.Text(r.Term, 13, FontWeight.Medium, ink);
        term.VerticalAlignment = VerticalAlignment.Center;
        term.Margin = new Thickness(Look.Sm, 0, 0, 0);
        if (r.Kind == PronunciationRow.Source.Replaced) term.TextDecorations = TextDecorations.Strikethrough;
        row.Children.Add(term);

        var sayCell = new DockPanel();
        var heard = r.AsPronunciation;
        sayCell.Children.Add(PlayButton(r.SayItAs.Length == 0, () => Audition.Hear(heard, Document)));
        var say = Ui.Text(r.SayItAs, 13, brush: ink);
        say.VerticalAlignment = VerticalAlignment.Center;
        say.Margin = new Thickness(Look.Sm, 0, 0, 0);
        sayCell.Children.Add(say);
        Grid.SetColumn(sayCell, 2);
        row.Children.Add(sayCell);

        var notes = Ui.Text(r.Notes, 13, brush: Look.Brush(Look.SecondaryInk), maxLines: 1);
        notes.VerticalAlignment = VerticalAlignment.Center;
        notes.Margin = new Thickness(Look.Sm, 0, 0, 0);
        Grid.SetColumn(notes, 3);
        row.Children.Add(notes);

        var count = Ui.Caption("—");
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 4);
        row.Children.Add(count);

        var matchCase = new CheckBox { IsChecked = r.MatchCase, IsEnabled = false, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(matchCase, "Match Case");
        Grid.SetColumn(matchCase, 5);
        row.Children.Add(matchCase);
        ToolTip.SetTip(row, r.Kind == PronunciationRow.Source.BookCopy
            ? "Right-click to add it to This PC’s list"
            : "Change it in Settings › Pronunciations, or right-click › Change for This Book Only");
        return row;
    }

    Control PlayButton(bool empty, Action play)
    {
        var button = new Button
        {
            Content = "▶", Focusable = false, Padding = new Thickness(6, 2), Margin = new Thickness(Look.Xs, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, empty ? "Hear how the narrator's voice says it now" : "Hear it in the narrator's voice, with this spelling");
        Avalonia.Automation.AutomationProperties.SetName(button, "Hear it");
        button.Click += (_, _) => play();
        DockPanel.SetDock(button, Dock.Right);
        return button;
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
        if (Rows.FirstOrDefault(r => r.Id == id) is { } item) list.SelectedItems?.Add(item);
        updating = false;
    }

    /// Selects rows (for tests, and after Change for This Book Only).
    public void SelectRows(IEnumerable<Guid> ids)
    {
        selection.Clear();
        selection.UnionWith(ids);
        Refresh(false);
    }

    /// Selects an entry (Fix Pronunciation… in the manuscript), showing it even when it was
    /// dismissed or the search hides it.
    void TakeFocus()
    {
        if (Workspace.FocusPronunciation is not { } id) return;
        Workspace.FocusPronunciation = null;
        search.Text = "";
        if (filter == PronunciationFilter.Computer) filter = PronunciationFilter.All;
        if (Document.Project.Pronunciations.FirstOrDefault(p => p.Id == id) is { Dismissed: true }) showDismissed.IsChecked = true;
        selection.Clear();
        selection.Add(id);
        Refresh(false);
        if (Rows.FirstOrDefault(r => r.Id == id) is { } item) list.ScrollIntoView(item);
    }

    /// The right-click menu, for the selected rows.
    public IEnumerable<Control> MenuItems()
    {
        var picked = Rows.Where(r => selection.Contains(r.Id)).ToList();
        var book = picked.Where(r => r.Book is not null).Select(r => r.Book!).ToList();
        var listed = picked.Where(r => r.Book is null).ToList();
        if (book.Count == 1 && book[0].FirstParagraphID is { } pid)
            yield return Item("Show First Use in Manuscript", () => Workspace.Show(pid));
        if (book.Count > 0)
        {
            var add = Item(book.Count > 1 ? $"Add {book.Count} to This PC’s List" : "Add to This PC’s List", () =>
            {
                var entries = book.Select(p => new ListedPronunciation(PronunciationText.Trim(p.Term), p.SayItAs, p.Notes, p.MatchCase)).ToList();
                _ = AddToComputer(entries, "This Book’s");
            });
            add.IsEnabled = book.Any(p => PronunciationText.Trim(p.SayItAs).Length > 0);
            yield return add;
            yield return new Separator();
            var set = book.Select(p => p.Id).ToHashSet();
            yield return Item(book.Count > 1 ? $"Dismiss {book.Count} Terms" : "Dismiss", () =>
                Document.Change("Dismiss Terms", p => { foreach (var x in p.Pronunciations.Where(x => set.Contains(x.Id))) x.Dismissed = true; }));
            yield return Item("Delete", () =>
            {
                selection.ExceptWith(set);
                Document.Change("Delete Terms", p => p.Pronunciations.RemoveAll(x => set.Contains(x.Id)));
            });
        }
        if (listed.Count > 0)
        {
            if (book.Count > 0) yield return new Separator();
            if (listed.Count == 1 && listed[0].Kind != PronunciationRow.Source.Replaced)
                yield return Item("Change for This Book Only", () => OverrideInBook(listed[0]));
            var copies = listed.Where(r => r.Kind == PronunciationRow.Source.BookCopy).Select(r => r.Listed!).ToList();
            if (copies.Count > 0)
                yield return Item("Add to This PC’s List", () => _ = AddToComputer(copies, "This Book’s"));
            var mine = listed.Where(r => r.Kind != PronunciationRow.Source.BookCopy).ToList();
            if (mine.Count > 0)
                yield return Item(mine.Count > 1 ? $"Remove {mine.Count} from This PC’s List…" : "Remove from This PC’s List…", () => _ = RemoveFromComputer(mine));
        }
    }

    async Task AddToComputer(IReadOnlyList<ListedPronunciation> entries, string incoming)
    {
        if (await Computer.AddAsking(this, entries, incoming) is { } r && (r.Replaced > 0 || r.Kept > 0))
            await Dialogs.Alert(this, "This PC’s list is updated.", ComputerPronunciations.Describe(r));
    }

    /// A This PC word this book should say its own way: copied into the book's list, where it
    /// can be changed (and wins from then on).
    public void OverrideInBook(PronunciationRow row)
    {
        if (row.Listed is not { } e) return;
        var p = new Pronunciation { Term = e.Term, SayItAs = e.SayItAs, Notes = e.Notes, AddedByUser = true, MatchCase = e.MatchCase };
        Document.Change("Change Pronunciation for This Book", project => project.Pronunciations.Add(p.Copy()));
        filter = PronunciationFilter.Book;
        SelectRows([p.Id]);
    }

    async Task RemoveFromComputer(List<PronunciationRow> rows)
    {
        var title = rows.Count == 1 ? $"Remove “{rows[0].Term}” from This PC’s list?" : $"Remove {rows.Count} words from This PC’s list?";
        var message = $"Every book on this PC stops using {(rows.Count == 1 ? "it" : "them")}. Books keep their own entries.";
        if (!await Dialogs.Confirm(this, title, message, "Remove")) return;
        Computer.Remove(rows.Select(r => r.Id).ToHashSet());
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
        if (filter == PronunciationFilter.Computer) filter = PronunciationFilter.All;
        selection.Clear();
        selection.Add(p.Id);
        Document.Change("Add Term", project => project.Pronunciations.Add(p.Copy()));
        if (Rows.FirstOrDefault(r => r.Id == p.Id) is { } item) list.ScrollIntoView(item);
    }

    void FindTerms() => Document.Change("Find Terms", p => PronunciationFinder.Refresh(p, SpellCheck.IsKnown));

    void SuggestSpellings()
    {
        if (Window is { } w) _ = w.RunClaude(AIRunner.ClaudeJob.Spellings);
    }
}
