using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using InnerVoice.App.Controls;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Settings › Pronunciations: This PC's list, the one every book uses unless it turned it off
/// (PronunciationLists in Core). Edited here; import and export as CSV, text or JSON.
public sealed partial class SettingsWindow
{
    PronunciationsPane? pronunciationsPane;

    void AddPronunciationsTab(TabControl tabs)
    {
        pronunciationsPane ??= new PronunciationsPane();
        // The pane outlives Build's new tabs, so it keeps what's being typed.
        if (pronunciationsPane.Parent is ContentControl old) old.Content = null;
        tabs.Items.Add(new TabItem { FontSize = 16, Header = "Pronunciations", Content = pronunciationsPane });
    }

    /// The Pronunciations tab (for tests).
    public PronunciationsPane? Pronunciations => pronunciationsPane;
}

public sealed class PronunciationsPane : DockPanel
{
    public const string Footer = "Every book on this PC uses these, unless the book has Use This PC’s Pronunciations turned off. Each book keeps a copy of the ones it uses, so it sounds the same on another computer. When a book has its own entry for a word, the book’s wins.";

    /// Word, Say It As, Match Case, Notes.
    const string Columns = "1.4*,1.6*,84,2*";

    readonly TextBox search = new() { PlaceholderText = "Find a word", Width = 170 };
    readonly ListBox list;
    readonly HashSet<Guid> selection = [];
    readonly Button delete;
    readonly TextBlock count = Ui.Caption("");
    bool updating, pendingRefresh;
    static ComputerPronunciations Computer => ComputerPronunciations.Shared;

    public PronunciationsPane()
    {
        Margin = new Thickness(Look.Lg);
        search.TextChanged += (_, _) => Refresh();
        delete = Ui.Button("Delete", Delete, tip: "Remove the selected words from This PC’s list");
        var tools = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, Look.Sm) };
        var left = Ui.Row(Look.Sm, search, Ui.Button("Add Word", Add, tip: "Add a word to This PC’s list"), delete);
        DockPanel.SetDock(left, Dock.Left);
        tools.Children.Add(left);
        var right = Ui.Row(Look.Sm,
            Ui.Button("Import…", () => _ = Computer.ImportFile(this), tip: "Add words from a CSV, text (word = how to say it), TSV or InnerVoice JSON file"),
            Ui.Button("Export…", () => _ = Computer.ExportFile(this), tip: "Save This PC’s list as CSV (opens in Excel) or JSON (for InnerVoice on another computer)"));
        DockPanel.SetDock(right, Dock.Right);
        tools.Children.Add(right);

        var intro = Ui.Text("This PC’s pronunciations: how every book on this PC says these words.", 13, wrap: TextWrapping.Wrap);
        intro.Margin = new Thickness(0, 0, 0, Look.Sm);
        DockPanel.SetDock(intro, Dock.Top);
        Children.Add(intro);
        DockPanel.SetDock(tools, Dock.Top);
        Children.Add(tools);

        var footer = Ui.Column(Look.Xs, count, Ui.Caption(Footer, TextWrapping.Wrap));
        footer.Margin = new Thickness(0, Look.Sm, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = Look.Sm, Margin = new Thickness(Look.Xl, Look.Xs, Look.Md, Look.Xs) };
        string[] titles = ["Word", "Say It As", "Match Case", "Notes"];
        for (int i = 0; i < titles.Length; i++)
        {
            var t = Ui.Caption(titles[i]);
            t.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderBrush = Look.Brush(Look.Separator),
            BorderThickness = new Thickness(1),
            ItemTemplate = new FuncDataTemplate<ListedPronunciation>((e, _) => e is null ? new Panel() : Row(e)),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(Look.Sm, 2)) },
        });
        list.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            selection.Clear();
            foreach (var e in list.SelectedItems?.OfType<ListedPronunciation>() ?? []) selection.Add(e.Id);
            delete.IsEnabled = selection.Count > 0;
        };
        Children.Add(list);
        AttachedToVisualTree += (_, _) => { Computer.Changed += Refresh; Refresh(); };
        DetachedFromVisualTree += (_, _) => Computer.Changed -= Refresh;
        Refresh();
    }

    /// The words listed now, in order (for tests).
    public IReadOnlyList<ListedPronunciation> Listed => list.ItemsSource as IReadOnlyList<ListedPronunciation> ?? [];

    public void Refresh()
    {
        // Don't pull a text field out from under the narrator while they type in it.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox tb && tb.IsVisible &&
            Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(list, tb))
        {
            if (!pendingRefresh)
            {
                pendingRefresh = true;
                tb.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { pendingRefresh = false; Refresh(); });
            }
            return;
        }
        var q = (search.Text ?? "").Trim();
        var rows = Computer.List.Sorted.Where(e => q.Length == 0 || PronunciationText.Contains(e.Term, q) || PronunciationText.Contains(e.SayItAs, q)).ToList();
        updating = true;
        list.ItemsSource = rows;
        list.SelectedItems?.Clear();
        foreach (var e in rows.Where(e => selection.Contains(e.Id))) list.SelectedItems?.Add(e);
        updating = false;
        selection.IntersectWith(Computer.List.Entries.Select(e => e.Id));
        delete.IsEnabled = selection.Count > 0;
        int n = Computer.List.Entries.Count;
        count.Text = n == 0 ? "No words yet. Add them here, import a file, or add a book’s from its Pronunciations page." : n == 1 ? "1 word" : $"{n:N0} words";
    }

    Control Row(ListedPronunciation e)
    {
        var id = e.Id;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), ColumnSpacing = Look.Sm, Background = Brushes.Transparent };
        var term = Field(e.Term, "Word", v => Computer.Update(id, x => x.Term = v));
        term.FontWeight = FontWeight.Medium;
        row.Children.Add(term);
        var say = Field(e.SayItAs, "e.g. BAR-nuh-bee", v => Computer.Update(id, x => x.SayItAs = v));
        Grid.SetColumn(say, 1);
        row.Children.Add(say);
        var match = new CheckBox { IsChecked = e.MatchCase, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(match, "Only when the capitals match, so \"Will\" the name doesn't change \"will\"");
        Avalonia.Automation.AutomationProperties.SetName(match, "Match Case");
        match.IsCheckedChanged += (_, _) => { if (!updating) Computer.Update(id, x => x.MatchCase = match.IsChecked == true); };
        Grid.SetColumn(match, 2);
        row.Children.Add(match);
        var notes = Field(e.Notes, "Origin, stress, source", v => Computer.Update(id, x => x.Notes = v));
        notes.Foreground = Look.Brush(Look.SecondaryInk);
        Grid.SetColumn(notes, 3);
        row.Children.Add(notes);
        row.AddHandler(GotFocusEvent, (_, _) => Select(id), handledEventsToo: true);
        return row;
    }

    static DraftTextBox Field(string text, string placeholder, Action<string> commit) => new(text, commit, watermark: placeholder)
    {
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    void Select(Guid id)
    {
        if (selection.Count == 1 && selection.Contains(id)) return;
        selection.Clear();
        selection.Add(id);
        updating = true;
        list.SelectedItems?.Clear();
        if (Listed.FirstOrDefault(e => e.Id == id) is { } item) list.SelectedItems?.Add(item);
        updating = false;
        delete.IsEnabled = true;
    }

    public void Add()
    {
        search.Text = "";
        var id = Computer.Add();
        selection.Clear();
        selection.Add(id);
        Refresh();
        if (Listed.FirstOrDefault(e => e.Id == id) is { } item) list.ScrollIntoView(item);
    }

    void Delete()
    {
        if (selection.Count == 0) return;
        var ids = selection.ToHashSet();
        selection.Clear();
        Computer.Remove(ids);
    }
}
