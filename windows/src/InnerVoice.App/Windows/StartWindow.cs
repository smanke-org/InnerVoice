using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InnerVoice.App.UI;

namespace InnerVoice.App.Windows;

/// The window shown when no project is open: drop a manuscript, open a project, recent
/// projects, and the sample book.
public sealed class StartWindow : Window
{
    public StartWindow()
    {
        Title = "InnerVoice";
        Width = 760;
        Height = 500;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Build();
        AppSettings.Changed += Build;
        Closed += (_, _) => AppSettings.Changed -= Build;
    }

    void Build()
    {
        var left = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Section), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Ui.Text("InnerVoice", 30, FontWeight.Bold));
        left.Children.Add(Ui.Text("Who's speaking, for audiobook narrators.", 14, brush: Look.Brush(Look.SecondaryInk)));
        var drop = new Border
        {
            Margin = new Thickness(0, Look.Lg),
            Padding = new Thickness(Look.Xl, Look.Xxl),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Look.Brush(Look.Accent.WithAlpha(0.6)),
            BorderThickness = new Thickness(1.5),
            Background = Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.12 : 0.05)),
            Child = new TextBlock
            {
                Text = "Drop a manuscript here\nWord, PDF, RTF, text, Markdown, OpenDocument, EPUB or HTML",
                TextAlignment = TextAlignment.Center, Foreground = Look.Brush(Look.SecondaryInk), TextWrapping = TextWrapping.Wrap,
            },
        };
        left.Children.Add(drop);
        left.Children.Add(Ui.Row(Look.Sm,
            Ui.Button("New from Manuscript…", () => _ = App.Current.NewFromManuscript(this), primary: true),
            Ui.Button("Open Project…", () => _ = App.Current.OpenWithPicker(this))));
        left.Children.Add(new Button { Content = "Open the Sample Book", Classes = { "link" }, Padding = new Thickness(0) }.Also(b => b.Click += (_, _) => App.Current.OpenSample()));

        var right = new StackPanel { Spacing = Look.Xs, Margin = new Thickness(Look.Lg, Look.Section) };
        right.Children.Add(Ui.Headline("Recent Projects"));
        var recents = AppSettings.Current.RecentProjects.Where(File.Exists).Take(10).ToList();
        if (recents.Count == 0) right.Children.Add(Ui.Caption("Projects you open show up here."));
        foreach (var path in recents)
        {
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = Ui.Column(0, Ui.Text(Path.GetFileNameWithoutExtension(path), 13, FontWeight.SemiBold, maxLines: 1),
                    Ui.Text(Path.GetDirectoryName(path) ?? "", 11, brush: Look.Brush(Look.SecondaryInk), maxLines: 1)),
            };
            ToolTip.SetTip(b, path);
            b.Click += (_, _) => App.Current.OpenPath(path);
            right.Children.Add(b);
        }
        var side = new Border { Width = 280, Background = Look.Brush(Look.Chrome), Child = Ui.Scroll(right), BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(1, 0, 0, 0) };
        Background = Look.Brush(Look.Paper);
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        dock.Children.Add(side);
        dock.Children.Add(left);
        Content = dock;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        foreach (var f in files ?? [])
            if (f.TryGetLocalPath() is { } path) App.Current.OpenPath(path);
    }
}

static class Fluent
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
