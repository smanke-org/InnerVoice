using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.UI;

namespace InnerVoice.App.Help;

/// The Help window: topics on the left, a search box above them that filters by every
/// word on a page, and the chosen page on the right. One window, reused.
public sealed class HelpWindow : Window
{
    static HelpWindow? shown;

    readonly TextBox search = new() { PlaceholderText = "Search Help" };
    readonly ListBox list = new();
    readonly TextBlock noMatches = Ui.Caption("No matches");
    readonly ContentControl page = new();
    readonly Border sidebar = new();
    readonly Border divider = Ui.Divider(vertical: true);
    HelpTopic selected = HelpTopic.GettingStarted;
    bool updating;

    /// Opens Help on `topic`.
    public static void Show(HelpTopic topic = HelpTopic.GettingStarted)
    {
        shown ??= new HelpWindow();
        shown.Open(topic);
        if (!shown.IsVisible) ((Window)shown).Show();
        if (shown.WindowState == WindowState.Minimized) shown.WindowState = WindowState.Normal;
        shown.Activate();
    }

    HelpWindow()
    {
        Title = "InnerVoice Help";
        Width = 860;
        Height = 620;
        MinWidth = 640;
        MinHeight = 400;
        FontFamily = Look.UIFamily;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        list.ItemTemplate = new FuncDataTemplate<HelpTopic>((topic, _) =>
        {
            var t = Ui.Text(HelpContent.Title(topic), 13);
            t.Margin = new Thickness(0, 2);
            return t;
        });
        list.SelectionChanged += (_, _) =>
        {
            if (updating || list.SelectedItem is not HelpTopic topic || topic == selected) return;
            selected = topic;
            ShowPage();
        };
        search.Margin = new Thickness(Look.Md, Look.Md, Look.Md, Look.Sm);
        search.TextChanged += (_, _) => Filter();
        noMatches.HorizontalAlignment = HorizontalAlignment.Center;
        noMatches.Margin = new Thickness(0, Look.Lg);

        var left = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        DockPanel.SetDock(noMatches, Dock.Top);
        left.Children.Add(noMatches);
        left.Children.Add(list);
        sidebar.Child = left;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,Auto,*") };
        grid.Children.Add(sidebar);
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        Grid.SetColumn(page, 2);
        grid.Children.Add(page);
        Content = grid;

        Closed += (_, _) => shown = null;
        ActualThemeVariantChanged += (_, _) => Restyle();
        Restyle();
        Filter();
    }

    void Open(HelpTopic topic)
    {
        selected = topic;
        if (!string.IsNullOrEmpty(search.Text)) search.Text = ""; // Filter() runs and shows the page.
        else Filter();
    }

    string Query => (search.Text ?? "").Trim();

    /// Lists the topics that match the search, keeping a matching page on screen.
    void Filter()
    {
        var query = Query;
        var matches = HelpContent.All.Where(t => HelpContent.Matches(t, query)).ToList();
        if (matches.Count > 0 && !matches.Contains(selected)) selected = matches[0];
        updating = true;
        list.ItemsSource = matches;
        list.SelectedItem = matches.Contains(selected) ? selected : null;
        updating = false;
        noMatches.IsVisible = matches.Count == 0;
        ShowPage();
    }

    /// Colors come from Look, which follows the light or dark appearance.
    void Restyle()
    {
        Background = Look.Brush(Look.Paper);
        sidebar.Background = Look.Brush(Look.Chrome);
        divider.Background = Look.Brush(Look.Separator);
        noMatches.Foreground = Look.Brush(Look.SecondaryInk);
        ShowPage();
    }

    void ShowPage()
    {
        if (list.ItemsSource is IEnumerable<HelpTopic> items && !items.Any())
        {
            page.Content = Ui.Empty("No Matches", "Try another word, or clear the search to see every topic.");
            return;
        }
        var query = Query;
        var column = new StackPanel
        {
            Spacing = Look.Md,
            Margin = new Thickness(Look.Section, Look.Xxl),
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var title = Ui.Text(HelpContent.Title(selected), 26, FontWeight.SemiBold, Look.Brush(Look.Ink));
        title.Margin = new Thickness(0, 0, 0, Look.Xs);
        column.Children.Add(title);
        foreach (var block in HelpContent.Blocks(selected)) column.Children.Add(View(block, query));
        var scroll = Ui.Scroll(column);
        page.Content = scroll;
        scroll.Offset = default;
    }

    static SelectableTextBlock Body(string text) => new()
    {
        Text = text,
        FontSize = 14,
        LineHeight = 21,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Look.Brush(Look.Ink),
    };

    static Control View(HelpBlock block, string query) => block switch
    {
        HelpBlock.Text t => Body(t.Value),
        HelpBlock.Heading h => Heading(h.Value),
        HelpBlock.Tip tip => new Border
        {
            Background = Look.Brush(Look.Chrome),
            BorderBrush = Look.Brush(Look.Accent),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Padding = new Thickness(Look.Md),
            Child = Body(tip.Value),
        },
        HelpBlock.Steps s => StepsList(s.Items),
        HelpBlock.Keys k => KeysTable(k.Rows, query),
        _ => new Panel(),
    };

    static TextBlock Heading(string text)
    {
        var t = Ui.Text(text, 17, FontWeight.SemiBold, Look.Brush(Look.Ink), TextWrapping.Wrap);
        t.Margin = new Thickness(0, Look.Sm, 0, 0);
        return t;
    }

    static Control StepsList(string[] items)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), RowSpacing = Look.Sm };
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var number = Ui.Text($"{i + 1}.", 14, FontWeight.SemiBold, Look.Brush(Look.SecondaryInk));
            Grid.SetRow(number, i);
            grid.Children.Add(number);
            var text = Body(items[i]);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
        }
        return grid;
    }

    /// The same key column width in every table on every page; rows that match the search
    /// are tinted.
    static Control KeysTable((string Keys, string What)[] rows, string query)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("212,*"), RowSpacing = Look.Xs };
        var hitBrush = Look.Brush(Color.Parse("#EAB308").WithAlpha(Look.IsDark ? 0.25 : 0.2));
        for (int i = 0; i < rows.Length; i++)
        {
            var (keys, what) = rows[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bool hit = query.Length > 0 && (keys + " " + what).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            if (hit)
            {
                var band = new Border { Background = hitBrush, CornerRadius = new CornerRadius(4), Margin = new Thickness(-4, -2) };
                Grid.SetRow(band, i);
                Grid.SetColumnSpan(band, 2);
                grid.Children.Add(band);
            }
            var key = new Border
            {
                Background = Look.Brush(Look.Chrome),
                BorderBrush = Look.Brush(Look.Separator),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(Look.Sm, 2),
                Margin = new Thickness(0, 0, Look.Lg, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.Text(keys, 13, FontWeight.SemiBold, Look.Brush(Look.Ink), TextWrapping.Wrap),
            };
            Grid.SetRow(key, i);
            grid.Children.Add(key);
            var text = Body(what);
            text.Margin = new Thickness(0, 1, 0, 0);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
        }
        return grid;
    }
}
