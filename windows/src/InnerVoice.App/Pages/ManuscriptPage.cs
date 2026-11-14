using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// The colored manuscript, with the reading look in the top bar and Find.
public sealed partial class ManuscriptPage : Page
{
    public override PageKind Kind => PageKind.Manuscript;
    public readonly ManuscriptCanvas Canvas = new();
    readonly ScrollViewer scroller;
    readonly TextBox findBox = new() { PlaceholderText = "Find in manuscript", Width = 260 };
    readonly TextBlock findCount = Ui.Caption("");
    readonly Border findBar = new() { IsVisible = false };
    readonly Border banner = new();
    readonly StackPanel toolbar;
    readonly ComboBox styleBox = new() { ItemsSource = new[] { "Highlight", "Outline", "Underline" }, Width = 120 };
    readonly ToggleButton initials = new() { Content = "Initials" };
    bool updating;

    public ManuscriptPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        Canvas.Document = document;
        scroller = Ui.Scroll(Canvas);
        var grid = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        grid.Children.Add(banner);
        DockPanel.SetDock(findBar, Dock.Top);
        grid.Children.Add(findBar);
        DockPanel.SetDock(notice, Dock.Top);
        grid.Children.Add(notice);
        grid.Children.Add(scroller);
        var layers = new Grid();
        layers.Children.Add(grid);
        layers.Children.Add(anchorLayer);
        Content = layers;

        styleBox.SelectionChanged += (_, _) =>
        {
            if (updating || styleBox.SelectedIndex < 0) return;
            var style = (ColorStyle)styleBox.SelectedIndex;
            if (Document.Project.Settings.Style != style) Document.UpdateSettings(style.Title(), s => s.Style = style);
        };
        ToolTip.SetTip(styleBox, "How lines show their color");
        initials.IsCheckedChanged += (_, _) =>
        {
            if (updating) return;
            bool on = initials.IsChecked == true;
            if (Document.Project.Settings.ShowInitials != on) Document.UpdateSettings(on ? "Show Initials" : "Hide Initials", s => s.ShowInitials = on);
        };
        ToolTip.SetTip(initials, "Show each speaker in the margin");
        var smaller = Ui.Button("A−", () => AppSettings.Update(s => s.ReadingFontSize = Math.Max(AppSettings.MinReadingSize, s.ReadingFontSize - 1)), tip: "Smaller text (Ctrl+−)");
        var larger = Ui.Button("A+", () => AppSettings.Update(s => s.ReadingFontSize = Math.Min(AppSettings.MaxReadingSize, s.ReadingFontSize + 1)), tip: "Bigger text (Ctrl+=)");
        findBox.KeyDown += (_, e) =>
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (e.Key is Key.Enter or Key.F3) { FindNext(shift ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        findBox.TextChanged += (_, _) => UpdateSearch(jump: true);
        findCount.MinWidth = 70;
        findCount.VerticalAlignment = VerticalAlignment.Center;
        var prev = Ui.Button("▲", () => FindNext(-1), tip: "Previous match (Shift+Enter, Shift+F3)");
        var next = Ui.Button("▼", () => FindNext(1), tip: "Next match (Enter, F3)");
        var close = Ui.Button("✕", CloseFind, tip: "Close (Esc)");
        findBar.Padding = new Thickness(12, 6);
        findBar.Child = Ui.Row(Look.Sm, findBox, findCount, prev, next, close);
        var find = Ui.Button("Find", () => FocusFind(), tip: "Find in the manuscript (Ctrl+F)");
        toolbar = Ui.Row(Look.Sm, styleBox, initials, smaller, larger, find);

        Canvas.LineClicked += OnLineClicked;
        Canvas.ContextRequestedAt += OnContext;
        Canvas.MarginNameClicked += OnMarginNameClicked;
        Canvas.NarrationClicked += (_, mods) =>
        {
            if (!mods.HasFlag(KeyModifiers.Shift) && Canvas.SelectedLines.Count > 0) { Canvas.SelectedLines.Clear(); Canvas.InvalidateVisual(); }
        };
        workspace.ShowParagraphRequested += id => Avalonia.Threading.Dispatcher.UIThread.Post(() => Canvas.ScrollToParagraph(id, flash: true), Avalonia.Threading.DispatcherPriority.Background);
        workspace.SelectLineRequested += id =>
        {
            Canvas.SelectedLines.Clear();
            Canvas.SelectedLines.Add(id);
            Canvas.InvalidateVisual();
        };
        workspace.ShowChapterRequested += c => Avalonia.Threading.Dispatcher.UIThread.Post(() => Canvas.ScrollToChapter(c), Avalonia.Threading.DispatcherPriority.Background);
        Refresh(true);
    }

    public override Control Toolbar => toolbar;

    public override string Subtitle
    {
        get
        {
            var p = Document.Project;
            int needs = p.LinesNeedingYou;
            return $"{p.WordCount:N0} words · {p.Lines.Count:N0} lines" + (needs > 0 ? $" · {needs:N0} to review" : "");
        }
    }

    public override void Refresh(bool textChanged)
    {
        updating = true;
        styleBox.SelectedIndex = (int)Document.Project.Settings.Style;
        initials.IsChecked = Document.Project.Settings.ShowInitials;
        updating = false;
        UpdateBanner();
        if (textChanged) UpdateSearch();
    }

    void UpdateBanner()
    {
        var p = Document.Project;
        if (p.Lines.Count > 0 && p.LastTagged is null && p.UntaggedCount > 0)
        {
            banner.Child = new Border
            {
                Padding = new Thickness(Look.Lg, Look.Sm),
                Background = Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.22 : 0.10)),
                Child = Ui.Row(Look.Md, Ui.Text($"InnerVoice found {p.Lines.Count:N0} lines. Tag Speakers to find who says each one.", 13),
                    Ui.Button("Tag Speakers", Workspace.RequestTag, primary: true)),
            };
            banner.IsVisible = true;
        }
        else banner.IsVisible = false;
    }

    void OnLineClicked(Line line, Rect rect, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Shift) || mods.HasFlag(KeyModifiers.Control))
        {
            if (!Canvas.SelectedLines.Add(line.Id)) Canvas.SelectedLines.Remove(line.Id);
            Canvas.InvalidateVisual();
            ShowPicked();
            return;
        }
        Canvas.SelectedLines.Clear();
        Canvas.SelectedLines.Add(line.Id);
        Canvas.InvalidateVisual();
        ShowLinePopover(line, rect);
    }

    /// A name in the margin stands for its whole paragraph: one pick confirms or changes
    /// every line in it. Ctrl-click adds the paragraph to the picked lines (or takes it out);
    /// a click on a picked paragraph's name then asks once for every picked line.
    void OnMarginNameClicked(Line line, Rect rect, KeyModifiers mods)
    {
        var ids = Document.Project.Lines.Where(l => l.ParagraphID == line.ParagraphID).Select(l => l.Id).ToList();
        if (ids.Count == 0) ids = [line.Id];
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta))
        {
            if (ids.All(Canvas.SelectedLines.Contains)) Canvas.SelectedLines.ExceptWith(ids);
            else Canvas.SelectedLines.UnionWith(ids);
            Canvas.InvalidateVisual();
            ShowPicked();
            return;
        }
        if (Canvas.SelectedLines.Count > ids.Count && ids.All(Canvas.SelectedLines.Contains))
        {
            ShowLinePopover(line, rect, PickedInOrder());
            return;
        }
        Canvas.SelectedLines.Clear();
        Canvas.SelectedLines.UnionWith(ids);
        Canvas.InvalidateVisual();
        ShowLinePopover(line, rect, ids);
    }

    // MARK: Find

    void UpdateSearch(bool jump = false)
    {
        Canvas.SearchHits.Clear();
        Canvas.CurrentHit = -1;
        var q = findBox.Text ?? "";
        if (q.Trim().Length > 0)
        {
            foreach (var m in InnerVoice.Core.TextSearch.Matches(q, Document.Project)) Canvas.SearchHits.Add((m.ParagraphID, m.Range));
            // Typing lands on the first match from the top of the view, as it's found.
            if (jump && Canvas.SearchHits.Count > 0) FindNext(1);
        }
        findCount.Text = q.Trim().Length == 0 ? "" : Canvas.SearchHits.Count == 0 ? "No matches"
            : Canvas.CurrentHit >= 0 ? $"{Canvas.CurrentHit + 1:N0} of {Canvas.SearchHits.Count:N0}" : $"{Canvas.SearchHits.Count:N0} found";
        Canvas.InvalidateVisual();
    }

    public void FindNext(int step)
    {
        if (Canvas.SearchHits.Count == 0) return;
        Canvas.CurrentHit = ((Canvas.CurrentHit + step) % Canvas.SearchHits.Count + Canvas.SearchHits.Count) % Canvas.SearchHits.Count;
        findCount.Text = $"{Canvas.CurrentHit + 1:N0} of {Canvas.SearchHits.Count:N0}";
        var (paragraph, range) = Canvas.SearchHits[Canvas.CurrentHit];
        Canvas.ScrollToSpan(paragraph, range);
        Canvas.InvalidateVisual();
    }

    /// Shows the find bar (Ctrl+F) and puts the cursor in it.
    public void FocusFind()
    {
        findBar.IsVisible = true;
        findBox.Focus();
        findBox.SelectAll();
        UpdateSearch();
    }

    /// Esc: hides the bar and clears the highlights.
    void CloseFind()
    {
        findBar.IsVisible = false;
        findBox.Text = "";
        UpdateSearch();
        Canvas.Focus();
    }

    public override void Shown() => Canvas.Focus();
}
