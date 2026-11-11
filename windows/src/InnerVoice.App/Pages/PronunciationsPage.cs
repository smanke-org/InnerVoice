using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Names, places and made-up words, with how to say them, notes and recorded samples.
public sealed class PronunciationsPage : Page
{
    public override PageKind Kind => PageKind.Pronunciations;
    readonly TextBox search = new() { PlaceholderText = "Find a term", Width = 180 };
    readonly CheckBox showDismissed = new() { Content = "Show Dismissed" };
    readonly StackPanel toolbar;
    bool pendingRefresh;

    public PronunciationsPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        search.TextChanged += (_, _) => Refresh(false);
        showDismissed.IsCheckedChanged += (_, _) => Refresh(false);
        ToolTip.SetTip(showDismissed, "Show terms you said need no entry");
        toolbar = Ui.Row(Look.Sm, search, showDismissed,
            Ui.Button("Find Terms", FindTerms, tip: "Look through the manuscript for unusual names and words again"),
            Ui.Button("Add Term", Add, tip: "Add a term to the guide"));
        SampleAudio.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Refresh(false));
        SampleAudio.Finished += data => { if (recordingFor is { } id) SaveSample(id, data); };
        Refresh(true);
    }

    public override Control Toolbar => toolbar;
    public override string Subtitle
    {
        get
        {
            var all = Document.Project.Pronunciations.Where(p => !p.Dismissed).ToList();
            return $"{all.Count(p => p.SayItAs.Length > 0)} of {all.Count} done";
        }
    }

    public void FindTerms()
    {
        int before = Document.Project.Pronunciations.Count;
        Document.Change("Find Terms", p => PronunciationFinder.Refresh(p, SpellCheck.IsKnown));
        Activity.Record(ActivityKey.Pronunciations, Math.Max(0, Document.Project.Pronunciations.Count - before));
    }

    void Add()
    {
        var p = new Pronunciation { Term = "New Term", AddedByUser = true };
        Document.Change("Add Term", x => x.Pronunciations.Add(p));
    }

    void Update(Guid id, string name, Action<Pronunciation> body) =>
        Document.Change(name, p => { if (p.Pronunciations.Find(x => x.Id == id) is { } t) body(t); });

    public override void Refresh(bool textChanged)
    {
        // Don't pull a field out from under the narrator while they type in it.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is DraftTextBox tb && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(this, tb))
        {
            if (!pendingRefresh)
            {
                pendingRefresh = true;
                tb.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { pendingRefresh = false; Refresh(false); });
            }
            return;
        }
        var q = search.Text ?? "";
        var rows = Document.Project.Pronunciations
            .Where(p => showDismissed.IsChecked == true || !p.Dismissed)
            .Where(p => q.Length == 0 || p.Term.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.SayItAs.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(p => p.AddedByUser).ThenByDescending(p => p.Occurrences).ThenBy(p => p.Term, StringComparer.Ordinal).ToList();
        if (rows.Count == 0 && q.Length == 0)
        {
            Content = Ui.Empty("No Pronunciations", "InnerVoice lists names, places and made-up words it finds. Add your own with Add Term.",
                Ui.Row(Look.Sm, Ui.Button("Find Terms", FindTerms, primary: true), Ui.Button("Add Term", Add)));
            return;
        }
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(Look.Xl) };
        list.Children.Add(Header());
        foreach (var p in rows) list.Children.Add(Row(p));
        if (!SampleAudio.IsSupported) list.Children.Add(Ui.Caption("Recording and playing samples works on Windows.").Also(t => t.Margin = new Thickness(0, Look.Md)));
        Content = Ui.Scroll(list);
    }

    static Grid Columns() => new() { ColumnDefinitions = new ColumnDefinitions("170,190,*,90,56,80"), ColumnSpacing = Look.Sm };

    static Control Header()
    {
        var g = Columns();
        string[] titles = ["Term", "Say It As", "Notes", "Sample", "Count", "Confirmed"];
        for (int i = 0; i < titles.Length; i++)
        {
            var t = Ui.Caption(titles[i]);
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return new Border { Child = g, Padding = new Thickness(0, 0, 0, Look.Xs), BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    Control Row(Pronunciation p)
    {
        var g = Columns();
        var id = p.Id;
        void Add(Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }
        Add(new DraftTextBox(p.Term, v => Update(id, "Edit Pronunciation", x => x.Term = v)) { FontWeight = FontWeight.SemiBold }, 0);
        Add(new DraftTextBox(p.SayItAs, v => Update(id, "Edit Pronunciation", x => x.SayItAs = v), watermark: "e.g. SHEV-on"), 1);
        Add(new DraftTextBox(p.Notes, v => Update(id, "Edit Pronunciation", x => x.Notes = v), watermark: "Origin, stress, source"), 2);
        Add(SampleControls(p), 3);
        Add(Ui.Caption(p.Occurrences > 0 ? $"{p.Occurrences}" : "—").Also(t => t.VerticalAlignment = VerticalAlignment.Center), 4);
        var confirmed = new CheckBox { IsChecked = p.Confirmed, HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(confirmed, "Checked with the author or rights holder");
        confirmed.IsCheckedChanged += (_, _) =>
        {
            bool on = confirmed.IsChecked == true;
            if (on == p.Confirmed) return;
            if (on) Activity.Record(ActivityKey.Pronunciations);
            Update(id, "Confirm Pronunciation", x => x.Confirmed = on);
        };
        Add(confirmed, 5);
        var menu = new ContextMenu();
        if (p.FirstParagraphID is { } pid)
        {
            var show = new MenuItem { Header = "Show First Use in Manuscript" };
            show.Click += (_, _) => Workspace.Show(pid);
            menu.Items.Add(show);
        }
        var dismiss = new MenuItem { Header = p.Dismissed ? "Bring Back" : "Dismiss" };
        dismiss.Click += (_, _) => Update(id, p.Dismissed ? "Bring Back Term" : "Dismiss Terms", x => x.Dismissed = !x.Dismissed);
        menu.Items.Add(dismiss);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => Document.Change("Delete Terms", x => x.Pronunciations.RemoveAll(t => t.Id == id));
        menu.Items.Add(delete);
        var row = new Border { Child = g, Padding = new Thickness(0, 2), ContextMenu = menu, Opacity = p.Dismissed ? 0.5 : 1 };
        return row;
    }

    Guid? recordingFor;

    Control SampleControls(Pronunciation p)
    {
        bool recording = SampleAudio.RecordingID == p.Id;
        var row = Ui.Row(Look.Xs);
        var record = new Button { Content = recording ? "■" : "●", Foreground = Look.Brush(recording ? Look.Attention : Look.SecondaryInk), Padding = new Thickness(8, 2) };
        ToolTip.SetTip(record, recording ? "Stop recording" : "Record a short sample of how to say it");
        record.IsEnabled = SampleAudio.IsSupported;
        record.Click += async (_, _) =>
        {
            if (recording) { SaveSample(p.Id, SampleAudio.Stop()); return; }
            recordingFor = p.Id;
            if (SampleAudio.Start(p.Id) is { } error) await Dialogs.Alert(this, "Couldn't Record", error);
        };
        row.Children.Add(record);
        if (p.AudioFile is { } file && Document.Audio.TryGetValue(file, out var data))
        {
            var play = new Button { Content = "▶", Padding = new Thickness(8, 2), IsEnabled = SampleAudio.IsSupported };
            ToolTip.SetTip(play, "Play the sample");
            play.Click += async (_, _) => { if (SampleAudio.Play(data) is { } error) await Dialogs.Alert(this, "Couldn't Play the Sample", error); };
            row.Children.Add(play);
        }
        return row;
    }

    void SaveSample(Guid id, byte[]? data)
    {
        recordingFor = null;
        if (data is null) return;
        var name = $"{id.ToString().ToUpperInvariant()}.m4a";
        Document.Change("Record Sample", p =>
        {
            Document.SetAudio(name, data);
            if (p.Pronunciations.Find(x => x.Id == id) is { } t) t.AudioFile = name;
        });
    }
}
