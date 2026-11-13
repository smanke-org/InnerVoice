// Use Voices from Another Project (the Mac's Views/SeriesVoicesSheet.swift): an earlier book
// in the series is read (never opened as a window), its voiced characters are matched to
// this book's (the narrator checks every match), and one change gives them the same voices.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

public sealed class SeriesVoicesWindow : Window
{
    readonly ProjectDocument document;
    readonly ContentControl body = new();
    Project? old;
    List<SeriesVoices.Match> matches = [];
    readonly CheckBox notes = new() { Content = "Bring their Voice Notes too", IsChecked = true };
    readonly CheckBox narrator = new() { Content = "Narrator", IsChecked = true };
    readonly CheckBox engine = new() { IsChecked = true };

    public static async Task Show(Window owner, ProjectDocument document) => await new SeriesVoicesWindow(document).ShowDialog(owner);

    public SeriesVoicesWindow(ProjectDocument document)
    {
        this.document = document;
        Title = "Use Voices from Another Project";
        Width = 560;
        Height = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        Content = new Border { Padding = new Thickness(Look.Xl), Child = body };
        ShowChooser(null);
    }

    void ShowChooser(string? error)
    {
        var column = new StackPanel { Spacing = Look.Md };
        column.Children.Add(Ui.Title("Use Voices from Another Project"));
        column.Children.Add(Ui.Caption($"Choose an earlier book in the series. You’ll match its characters to the ones in “{document.Project.Title}” before anything changes.", Avalonia.Media.TextWrapping.Wrap));
        if (error is not null) column.Children.Add(Ui.Text(error, brush: Look.Brush(Look.Attention), wrap: Avalonia.Media.TextWrapping.Wrap));
        var recent = AppSettings.Current.RecentProjects.Where(p => File.Exists(p) && !SamePath(p, document.FilePath)).Take(8).ToList();
        if (recent.Count > 0)
        {
            column.Children.Add(Ui.Headline("Recent Projects"));
            foreach (var path in recent)
            {
                var b = Ui.Button(Path.GetFileNameWithoutExtension(path), () => _ = Load(path));
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                column.Children.Add(b);
            }
        }
        var buttons = Ui.Row(Look.Sm, Ui.Button("Cancel", Close), Ui.Button("Choose Project…", async () =>
        {
            var path = await Dialogs.OpenFile(this, "Choose an earlier book in the series", Dialogs.Projects);
            if (path is not null) await Load(path);
        }, primary: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(Ui.Scroll(column));
        body.Content = dock;
    }

    static bool SamePath(string a, string? b) => b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// Reads the other project without opening it.
    public static Project ReadProject(string path)
    {
        var p = ProjectJson.Decode(ProjectArchive.Read(File.ReadAllBytes(path)).ProjectJson);
        LegacyVoices.Migrate(p); // an older book's Qwen3-TTS voices, as Pocket TTS ones (as the Mac's readProject)
        return p;
    }

    async Task Load(string path)
    {
        if (SamePath(path, document.FilePath))
        {
            ShowChooser("That’s this project. Choose an earlier book in the series.");
            return;
        }
        body.Content = Ui.Empty("Reading the project…", "");
        try
        {
            old = await Task.Run(() => ReadProject(path));
            matches = SeriesVoices.Suggest(old, new BookIndex(old), document.Project);
            ShowMatches();
        }
        catch
        {
            ShowChooser($"Unable to read “{Path.GetFileNameWithoutExtension(path)}”. Choose an InnerVoice project.");
        }
    }

    void ShowMatches()
    {
        if (old is null) return;
        var here = document.Project.Characters.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var column = new StackPanel { Spacing = Look.Sm };
        column.Children.Add(Ui.Title($"Voices from “{old.Title}”"));
        column.Children.Add(Ui.Caption("Check who each character is in this book. Matched characters get the same voices as before, as your own choices, so casting won’t change them. Characters left out are cast as usual.", Avalonia.Media.TextWrapping.Wrap));
        for (int i = 0; i < matches.Count; i++)
        {
            int k = i;
            var m = matches[i];
            var source = old.Character(m.Source)!;
            var names = new List<string> { "Not in this book" };
            names.AddRange(here.Select(c => c.Name));
            var box = new ComboBox { ItemsSource = names, Width = 220, SelectedIndex = m.Target is { } t ? here.FindIndex(c => c.Id == t) + 1 : 0 };
            box.SelectionChanged += (_, _) =>
            {
                Guid? target = box.SelectedIndex <= 0 ? null : here[box.SelectedIndex - 1].Id;
                matches[k] = new SeriesVoices.Match(m.Source, target, target is null ? null : SeriesVoices.How.Chosen);
            };
            if (m.How == SeriesVoices.How.FirstName) ToolTip.SetTip(box, "Only the first name matches. Check this is the same character.");
            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(Ui.Row(Look.Sm, Ui.Dot(source.ColorHex), Ui.Text(source.Name + (m.How == SeriesVoices.How.FirstName ? "  ?" : ""))));
            column.Children.Add(row);
        }
        column.Children.Add(Ui.Divider());
        column.Children.Add(notes);
        column.Children.Add(Ui.Caption("Casting and ElevenLabs’ Voice Design read what they say about how a character sounds. It replaces the Voice Notes of the matched characters here.", Avalonia.Media.TextWrapping.Wrap));
        narrator.IsVisible = old.Settings.NarratorVoices is { Count: > 0 };
        column.Children.Add(narrator);
        if (old.Settings.VoiceEngine is { } e && e != document.Project.Settings.Engine)
        {
            engine.Content = $"Use the same voice engine ({e.Title()})";
            column.Children.Add(engine);
        }
        else engine.IsChecked = false;
        var use = Ui.Button("Use These Voices", Apply, primary: true);
        var buttons = Ui.Row(Look.Sm, Ui.Button("Back", () => ShowChooser(null)), Ui.Button("Cancel", Close), use);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, Look.Md, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(Ui.Scroll(column));
        body.Content = dock;
    }

    void Apply()
    {
        if (old is null) return;
        var options = new SeriesVoices.Options(notes.IsChecked == true, narrator.IsChecked == true && narrator.IsVisible, engine.IsChecked == true);
        var from = old;
        var chosen = matches.ToList();
        // One change, one Undo.
        document.Change("Use Voices from Another Project", p => SeriesVoices.Apply(chosen, from, p, options));
        Close();
    }
}
