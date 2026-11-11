using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using Page = InnerVoice.App.Pages.Page;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// One window per book: a sidebar (pages, chapters, cast) and the page.
public sealed partial class ProjectWindow : Window
{
    public ProjectDocument Document { get; }
    public Workspace Workspace { get; } = new();
    readonly Dictionary<PageKind, Page> pages = [];
    readonly ContentControl host = new();
    readonly StackPanel sidebar = new() { Spacing = 1, Margin = new Thickness(Look.Sm, Look.Md) };
    readonly TextBlock title = Ui.Title("");
    readonly TextBlock subtitle = Ui.Caption("");
    readonly ContentControl toolbarHost = new();
    readonly Border stepsHost = new();
    readonly Border statusHost = new();
    Page? current;

    public ProjectWindow(ProjectDocument document)
    {
        Document = document;
        Width = AppSettings.Current.WindowWidth;
        Height = AppSettings.Current.WindowHeight;
        MinWidth = 820;
        MinHeight = 520;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        UpdateTitle();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(Look.Xl, Look.Md) };
        header.Children.Add(Ui.Column(0, title, subtitle));
        Grid.SetColumn(toolbarHost, 1);
        toolbarHost.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(toolbarHost);
        var headerBorder = new Border { Child = header, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 0, 0, 1) };

        var main = new DockPanel();
        DockPanel.SetDock(statusHost, Dock.Top);
        main.Children.Add(statusHost);
        DockPanel.SetDock(aiHost, Dock.Top);
        main.Children.Add(aiHost);
        DockPanel.SetDock(stepsHost, Dock.Top);
        main.Children.Add(stepsHost);
        DockPanel.SetDock(headerBorder, Dock.Top);
        main.Children.Add(headerBorder);
        main.Children.Add(host);

        var side = new Border
        {
            Width = 236,
            Background = Look.Brush(Look.Chrome),
            BorderBrush = Look.Brush(Look.Separator),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = SidebarWithVersion(),
        };
        var body = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        body.Children.Add(side);
        body.Children.Add(main);

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        root.Children.Add(body);
        Content = root;
        Background = Look.Brush(Look.Paper);

        Workspace.PageChanged += ShowPage;
        Workspace.TagRequested += () => _ = TagSpeakers();
        Document.Changed += OnChanged;
        Document.Saved += UpdateTitle;
        AppSettings.Changed += RefreshAll;
        ActualThemeVariantChanged += (_, _) => RefreshAll();
        Closing += OnClosing;
        Opened += async (_, _) =>
        {
            ShowPage();
            if (Document.IsReadOnly)
                await Dialogs.Alert(this, "Opened Read-Only", "A newer version of InnerVoice made this project, so this one can show it but won't save over it.");
            else if (Document.Project.IsAIVoicesProject)
                SetStatus("This book is set up for AI voices on the Mac. Its voices are kept; this app shows the narration tools.");
            _ = Document.RecolorAutomaticAsync();
        };
        ShowPage();
    }

    void UpdateTitle() => Title = $"{Document.DisplayName}{(Document.IsDirty && Document.FilePath is null ? " (not saved)" : "")} — InnerVoice";

    void OnChanged(bool textChanged)
    {
        current?.Refresh(textChanged);
        foreach (var p in pages.Values.Where(p => p != current)) p.Tag = "stale";
        RefreshChrome();
        UpdateTitle();
    }

    void RefreshAll()
    {
        Background = Look.Brush(Look.Paper);
        foreach (var p in pages.Values) p.Tag = "stale";
        current?.Refresh(true);
        if (current is not null) current.Tag = null;
        RefreshChrome();
    }

    public Page FindPage(PageKind kind) => Get(kind);

    Page Get(PageKind kind)
    {
        if (pages.TryGetValue(kind, out var p)) return p;
        p = kind switch
        {
            PageKind.Cast => new CastPage(Document, Workspace),
            PageKind.Teleprompter => new TeleprompterPage(Document, Workspace),
            _ => MakeOtherPage(kind),
        };
        pages[kind] = p;
        return p;
    }

    void ShowPage()
    {
        var page = Get(Workspace.Page);
        if (page != current)
        {
            current?.Hidden();
            current = page;
            host.Content = page;
        }
        if (page.Tag as string == "stale") { page.Refresh(true); page.Tag = null; }
        RefreshChrome();
        page.Shown();
    }

    void RefreshChrome()
    {
        if (current is null) return;
        title.Text = current.Kind.Title();
        subtitle.Text = current.Subtitle;
        toolbarHost.Content = current.Toolbar;
        BuildSidebar();
        RefreshSteps();
    }

    // MARK: Sidebar

    /// The sidebar, with the app's version at the bottom (click it for About).
    Control SidebarWithVersion()
    {
        var version = new Button
        {
            Content = Ui.Caption($"InnerVoice {App.Version}"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Look.Lg, Look.Sm),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(version, "About InnerVoice and what's new");
        version.Click += (_, _) => AboutWindow.Show(this);
        var dock = new DockPanel();
        DockPanel.SetDock(version, Dock.Bottom);
        dock.Children.Add(version);
        dock.Children.Add(Ui.Scroll(sidebar));
        return dock;
    }

    void BuildSidebar()
    {
        sidebar.Children.Clear();
        var p = Document.Project;
        sidebar.Children.Add(SectionHeader("Book"));
        foreach (var kind in VisiblePages())
        {
            string? badge = kind switch
            {
                PageKind.Review => p.LinesNeedingYou > 0 ? $"{p.LinesNeedingYou:N0}" : null,
                PageKind.Cast => p.Characters.Count > 0 ? $"{p.Characters.Count}" : null,
                _ => null,
            };
            sidebar.Children.Add(SideItem(kind.Title(), badge, null, current?.Kind == kind, () => Workspace.Go(kind)));
        }

        if (p.Chapters.Count > 1)
        {
            sidebar.Children.Add(SectionHeader("Chapters"));
            var needsByChapter = new Dictionary<int, int>();
            foreach (var l in p.Lines.Where(p.NeedsYou))
                if (Document.Index.Positions.TryGetValue(l.ParagraphID, out var pos)) needsByChapter[pos.Chapter] = needsByChapter.GetValueOrDefault(pos.Chapter) + 1;
            for (int c = 0; c < p.Chapters.Count; c++)
            {
                int chapter = c;
                var n = needsByChapter.GetValueOrDefault(c);
                sidebar.Children.Add(SideItem(p.Chapters[c].ShownTitle(), n > 0 ? $"{n} to review" : null, null, false, () => Workspace.ShowChapter(chapter), attention: n > 0));
            }
        }

        if (p.Characters.Count > 0)
        {
            sidebar.Children.Add(SectionHeader("Cast"));
            foreach (var c in p.Characters.OrderByDescending(x => Document.Index.StatsFor(x.Id)?.Lines ?? 0).Take(8))
            {
                var id = c.Id;
                sidebar.Children.Add(SideItem(c.Name, $"{Document.Index.StatsFor(c.Id)?.Lines ?? 0}", c.ColorHex, false, () =>
                {
                    Workspace.Go(PageKind.Cast);
                    (Get(PageKind.Cast) as CastPage)?.ShowCharacter(id);
                }));
            }
        }
    }

    static Control SectionHeader(string text) =>
        new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.SecondaryInk), Margin = new Thickness(Look.Sm, Look.Md, 0, Look.Xs) };

    static Control SideItem(string text, string? badge, string? dotHex, bool selected, Action click, bool attention = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = Look.Sm };
        if (dotHex is not null) grid.Children.Add(Ui.Dot(dotHex, 9));
        var label = Ui.Text(text, 13, selected ? FontWeight.SemiBold : FontWeight.Normal, maxLines: 1);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (badge is not null)
        {
            var b = Ui.Text(badge, 11, brush: Look.Brush(attention ? Look.Attention : Look.SecondaryInk));
            b.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(Look.Sm, 5),
            Background = selected ? Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.35 : 0.14)) : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };
        button.Click += (_, _) => click();
        return button;
    }

    // MARK: Status

    void SetStatus(string message)
    {
        statusHost.Child = new Border
        {
            Padding = new Thickness(Look.Lg, Look.Sm),
            Background = Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.22 : 0.10)),
            Child = Ui.Row(Look.Md, Ui.Text(message, 13, wrap: TextWrapping.Wrap), Ui.Button("OK", () => statusHost.Child = null)),
        };
    }

    // MARK: Closing

    bool closingConfirmed;

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closingConfirmed) return;
        if (runner?.IsRunning == true)
        {
            e.Cancel = true;
            if (!await Dialogs.Confirm(this, "Stop the AI and Close?", "InnerVoice is still reading this book. What it has done so far is kept.", "Stop and Close")) return;
            runner.Stop();
            closingConfirmed = true;
            await Task.Delay(300);
            Close();
            return;
        }
        if (Document.IsDirty && Document.FilePath is null && !Document.IsReadOnly && !AppPaths.IsTestRun && Document.Project.Chapters.Count > 0)
        {
            e.Cancel = true;
            var choice = await Dialogs.Confirm(this, "Save this project?", "It hasn't been saved yet. Save it, or close without saving.", "Save…", destructive: false);
            if (choice && !await SaveAs()) return;
            closingConfirmed = true;
            Close();
            return;
        }
        Document.FlushAutosave();
        AppSettings.Update(s => { s.WindowWidth = Width; s.WindowHeight = Height; });
        foreach (var p in pages.Values) p.Hidden();
        App.Current.WindowClosed(this);
    }

    public async Task<bool> SaveAs()
    {
        var path = await Dialogs.SaveFile(this, "Save Project", Document.Project.Title, "innervoice", Dialogs.Projects);
        if (path is null) return false;
        if (!path.EndsWith(".innervoice", StringComparison.OrdinalIgnoreCase)) path += ".innervoice";
        if (!Document.TrySave(out var error, path, overwriteNewer: true))
        {
            await Dialogs.Alert(this, "Couldn't Save", error ?? "");
            return false;
        }
        AppSettings.AddRecent(path);
        UpdateTitle();
        return true;
    }

    public async Task Save()
    {
        if (Document.FilePath is null) { await SaveAs(); return; }
        if (!Document.TrySave(out var error))
        {
            if (error == "changed-on-disk")
            {
                if (await Dialogs.Confirm(this, "Replace the Newer Copy?", "This project was changed on another computer or by another app since you opened it. Saving replaces that version with yours.", "Replace"))
                    Document.TrySave(out error, overwriteNewer: true);
                return;
            }
            await Dialogs.Alert(this, "Couldn't Save", error ?? "");
        }
    }
}
