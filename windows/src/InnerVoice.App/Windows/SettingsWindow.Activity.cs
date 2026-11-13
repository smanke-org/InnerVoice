using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Settings › Activity: what InnerVoice did for you across books. Numbers only, never
/// anything from a book.
public sealed partial class SettingsWindow
{
    partial void AddActivityTabCore(TabControl tabs) =>
        tabs.Items.Add(new TabItem { FontSize = 16, Header = "Activity", Content = Ui.Scroll(ActivityPane()) });

    static Control ActivityPane()
    {
        var log = Activity.Log;
        var pane = new StackPanel { Spacing = Look.Lg, Margin = new Thickness(Look.Lg) };
        pane.Children.Add(Ui.Text("What InnerVoice has done for you on this PC. Only numbers are kept, never anything from a book.", 13, wrap: TextWrapping.Wrap));
        var periods = new (string Title, DateTime? Since)[] { ("Today", DateTime.Today), ("Last 7 days", DateTime.Today.AddDays(-6)), ("Last 30 days", DateTime.Today.AddDays(-29)), ("All time", null) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,*,*,*"), ColumnSpacing = Look.Md, RowSpacing = Look.Xs };
        var rows = new (string Title, Func<Dictionary<string, int>, string> Value)[]
        {
            ("Lines tagged for you", t => $"{t.GetValueOrDefault(ActivityKey.LinesRules) + t.GetValueOrDefault(ActivityKey.LinesAI):N0}"),
            ("Time saved (estimate)", t => Duration((t.GetValueOrDefault(ActivityKey.LinesRules) + t.GetValueOrDefault(ActivityKey.LinesAI)) * ActivityKey.SecondsPerLineByHand)),
            ("Guesses you kept", t => $"{t.GetValueOrDefault(ActivityKey.GuessesKept):N0}"),
            ("Guesses you changed", t => $"{t.GetValueOrDefault(ActivityKey.GuessesChanged):N0}"),
            ("Lines you assigned", t => $"{t.GetValueOrDefault(ActivityKey.LinesByHand):N0}"),
            ("Summary facts found", t => $"{t.GetValueOrDefault(ActivityKey.Facts):N0}"),
            ("Pronunciations confirmed", t => $"{t.GetValueOrDefault(ActivityKey.Pronunciations):N0}"),
            ("Lines carried to a revision", t => $"{t.GetValueOrDefault(ActivityKey.LinesCarriedOver):N0}"),
        };
        for (int r = 0; r <= rows.Length; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < periods.Length; c++) Put(grid, Ui.Caption(periods[c].Title), 0, c + 1);
        var totals = periods.Select(p => log.Totals(p.Since)).ToList();
        for (int r = 0; r < rows.Length; r++)
        {
            Put(grid, Ui.Text(rows[r].Title, 13), r + 1, 0);
            for (int c = 0; c < periods.Length; c++) Put(grid, Ui.Text(rows[r].Value(totals[c]), 13), r + 1, c + 1);
        }
        pane.Children.Add(grid);
        var accuracy = ModelAccuracy.All(log.Totals());
        if (accuracy.Count > 0)
        {
            pane.Children.Add(Ui.Headline("AI accuracy by model"));
            pane.Children.Add(Ui.Caption("Of the AI's guesses you reviewed, how many you kept.", TextWrapping.Wrap));
            foreach (var a in accuracy)
                pane.Children.Add(Ui.Row(Look.Md, Ui.Text(a.Model, 13).Also(t => t.Width = 220), Ui.Text($"{a.Rate:P0}", 13, FontWeight.SemiBold), Ui.Caption($"{a.Kept:N0} kept of {a.Reviewed:N0}")));
        }
        if (log.FirstDay is { } first && ActivityLog.DateOf(first) is { } d)
            pane.Children.Add(Ui.Caption($"Counting since {d:MMMM d, yyyy}."));
        return pane;
    }

    static void Put(Grid g, Control c, int row, int col)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(0, (int)t.TotalMinutes)} min";
    }
}
