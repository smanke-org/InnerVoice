using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InnerVoice.App.AI;
using InnerVoice.App.UI;
using InnerVoice.Core.AI;

namespace InnerVoice.App.Windows;

/// Settings: General (appearance, reading text), AI (the model), Teleprompter, Activity.
public sealed partial class SettingsWindow : Window
{
    static SettingsWindow? shown;
    readonly TabControl tabs = new();

    public static SettingsWindow Show(Window? owner, string tab = "General")
    {
        shown ??= new SettingsWindow();
        shown.Closed += (_, _) => shown = null;
        shown.Select(tab);
        if (!shown.IsVisible) shown.Show();
        shown.Activate();
        return shown;
    }

    SettingsWindow()
    {
        Title = "InnerVoice Settings";
        Width = 760;
        Height = 640;
        MinWidth = 600;
        MinHeight = 420;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        tabs.Margin = new Thickness(Look.Lg, Look.Sm);
        Content = tabs;
        Build();
        ModelManager.Shared.Changed += Build;
        AppSettings.Changed += Build;
        ClaudeAccount.Shared.Changed += Build;
        Closed += (_, _) => { ModelManager.Shared.Changed -= Build; AppSettings.Changed -= Build; ClaudeAccount.Shared.Changed -= Build; };
    }

    void Select(string tab)
    {
        foreach (var item in tabs.Items.OfType<TabItem>())
            if ((item.Header as string) == tab) tabs.SelectedItem = item;
    }

    void Build()
    {
        var selected = (tabs.SelectedItem as TabItem)?.Header as string ?? "General";
        tabs.Items.Clear();
        tabs.Items.Add(new TabItem { FontSize = 16, Header = "General", Content = Ui.Scroll(General()) });
        tabs.Items.Add(new TabItem { FontSize = 16, Header = "AI", Content = Ui.Scroll(AIPane()) });
        tabs.Items.Add(new TabItem { FontSize = 16, Header = "Teleprompter", Content = Ui.Scroll(TeleprompterPane()) });
        AddActivityTab(tabs);
        Select(selected);
    }

    static StackPanel Pane() => new() { Spacing = Look.Lg, Margin = new Thickness(Look.Lg) };

    static Control Choice<T>(string label, T current, (string Title, T Value)[] options, Action<T> set) where T : struct
    {
        var box = new ComboBox { ItemsSource = options.Select(o => o.Title).ToList(), Width = 200 };
        box.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value.Equals(current)));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && !options[box.SelectedIndex].Value.Equals(current)) set(options[box.SelectedIndex].Value); };
        return Row(label, box);
    }

    static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), ColumnSpacing = Look.Md };
        var l = Ui.Text(label, 13);
        l.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    Control General()
    {
        var s = AppSettings.Current;
        var pane = Pane();
        pane.Children.Add(Choice("Appearance", s.Appearance, [("System", Appearance.System), ("Light", Appearance.Light), ("Dark", Appearance.Dark)],
            v => { AppSettings.Update(x => x.Appearance = v); App.Current.ApplyAppearance(); }));
        pane.Children.Add(Ui.Headline("Reading"));
        var size = new NumericUpDown { Minimum = (decimal)AppSettings.MinReadingSize, Maximum = (decimal)AppSettings.MaxReadingSize, Value = (decimal)s.ReadingFontSize, Increment = 1, Width = 140 };
        size.ValueChanged += (_, e) => { if (e.NewValue is { } v && (double)v != AppSettings.Current.ReadingFontSize) AppSettings.Update(x => x.ReadingFontSize = (double)v); };
        pane.Children.Add(Row("Text size", size));
        pane.Children.Add(Choice("Typeface", s.ReadingSerif, [("Serif", true), ("Sans serif", false)], v => AppSettings.Update(x => x.ReadingSerif = v)));
        pane.Children.Add(Choice("Line spacing", s.LineSpacing, [("Single", LineSpacing.Single), ("Double", LineSpacing.Double)], v => AppSettings.Update(x => x.LineSpacing = v)));
        pane.Children.Add(Choice("Margin shows", s.MarginNames, [("Initials", MarginNames.Initials), ("First name", MarginNames.FirstName), ("Full name", MarginNames.FullName)],
            v => AppSettings.Update(x => x.MarginNames = v)));
        pane.Children.Add(Choice("Steps bar", s.StepsHidden, [("Shown", false), ("Hidden", true)], v => AppSettings.Update(x => x.StepsHidden = v)));
        return pane;
    }

    Control TeleprompterPane()
    {
        var s = AppSettings.Current;
        var pane = Pane();
        pane.Children.Add(Choice("Next paragraph key", s.RecordNextKey,
            [("Space", NextKey.Space), ("Down Arrow", NextKey.Down), ("Page Down", NextKey.PageDown), ("Enter", NextKey.Return)],
            v => AppSettings.Update(x => x.RecordNextKey = v)));
        pane.Children.Add(Ui.Caption("→ always moves on and ← goes back. Foot pedals that send keys work too: set the pedal to send the key above.", TextWrapping.Wrap));
        var size = new NumericUpDown { Minimum = 18, Maximum = 64, Value = (decimal)s.RecordFontSize, Increment = 2, Width = 140 };
        size.ValueChanged += (_, e) => { if (e.NewValue is { } v && (double)v != AppSettings.Current.RecordFontSize) AppSettings.Update(x => x.RecordFontSize = (double)v); };
        pane.Children.Add(Row("Text size", size));
        return pane;
    }

    // MARK: AI

    Control AIPane()
    {
        var mm = ModelManager.Shared;
        var hw = mm.Hardware;
        var pane = Pane();
        pane.Children.Add(Ui.Text("The model reads the book to find who speaks each line and what the book says about each character. The downloaded models run on this PC: with them your manuscript never leaves it, and the download is the only time InnerVoice goes online for the AI. Claude runs in the cloud, and each book asks before anything is sent.", 13, wrap: TextWrapping.Wrap));
        pane.Children.Add(Ui.Caption(hw.HasGpu
            ? $"This PC: {hw.MemoryGB} GB of memory and a graphics card ({hw.GpuName}) with {hw.GpuGB:0} GB. Models that fit the card run fastest."
            : $"This PC: {hw.MemoryGB} GB of memory and no graphics card the AI can use, so models run on the processor. Smaller models finish much sooner.", TextWrapping.Wrap));
        if (!LlamaLibrary.IsAvailable)
            pane.Children.Add(Ui.Text("The AI engine is missing from this copy of InnerVoice. Reinstall InnerVoice to use Tag Speakers and Build Summaries.", 13, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap));
        foreach (var m in ModelCatalog.All.OrderBy(m => m.Bytes)) pane.Children.Add(ModelRow(m, hw));
        pane.Children.Add(ClaudeRow());
        var other = Ui.Row(Look.Sm,
            Ui.Button("Use a GGUF File…", async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose a GGUF Model", FileTypeFilter = [new FilePickerFileType("GGUF models") { Patterns = ["*.gguf"] }],
                });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) mm.ChooseFile(path);
            }),
            Ui.Button("Change Models Folder…", async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for Models" });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } dir) { AppSettings.Update(s => s.ModelsFolder = dir); mm.Refresh(); Build(); }
            }));
        pane.Children.Add(other);
        if (mm.Selection.StartsWith("file:")) pane.Children.Add(Ui.Caption($"Using {mm.SelectionName} ({mm.Selection["file:".Length..]})", TextWrapping.Wrap));
        pane.Children.Add(Ui.Caption($"Models are kept in {ModelManager.ModelsFolder}", TextWrapping.Wrap));
        return pane;
    }

    Control ModelRow(CatalogModel m, Hardware hw)
    {
        var mm = ModelManager.Shared;
        var advice = ModelCatalog.Advice(m, hw);
        bool chosen = mm.Selection == "catalog:" + m.Id;
        bool have = mm.Downloaded.Contains(m.Id);
        var color = advice.Fit switch
        {
            Fit.Recommended => Look.Recorded,
            Fit.Good => Look.Accent,
            Fit.Tight => Look.Retake,
            _ => Look.Attention,
        };
        var title = Ui.Row(Look.Sm, Ui.Text(m.Name, 14, FontWeight.SemiBold), Ui.Pill(advice.Title, color), Ui.Caption($"{m.SizeDescription} · {m.License}"));
        if (chosen) title.Children.Add(Ui.Pill("In use", Look.Gray));
        var col = Ui.Column(Look.Xs, title, Ui.Text(m.Blurb, 12, wrap: TextWrapping.Wrap), Ui.Caption(advice.Note, TextWrapping.Wrap));
        var actions = Ui.Row(Look.Sm);
        if (mm.Downloads.TryGetValue(m.Id, out var state))
        {
            switch (state)
            {
                case ModelManager.DownloadState.Downloading d:
                    actions.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = d.Fraction, Width = 200 });
                    actions.Children.Add(Ui.Caption($"{d.Bytes / 1_000_000_000.0:0.0} of {m.SizeDescription}"));
                    actions.Children.Add(Ui.Button("Cancel", () => mm.CancelDownload(m)));
                    break;
                case ModelManager.DownloadState.Verifying:
                    actions.Children.Add(Ui.Caption("Checking the download…"));
                    break;
                case ModelManager.DownloadState.Failed f:
                    actions.Children.Add(Ui.Text(f.Message, 12, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap));
                    actions.Children.Add(Ui.Button("Try Again", () => { mm.Downloads.Remove(m.Id); mm.Download(m); }));
                    break;
            }
        }
        else if (have)
        {
            if (!chosen) actions.Children.Add(Ui.Button("Use This Model", () => mm.Selection = "catalog:" + m.Id, primary: true));
            actions.Children.Add(Ui.Button("Delete…", async () =>
            {
                if (await Dialogs.Confirm(this, $"Delete {m.Name}?", $"This frees {m.SizeDescription}. You can download it again later.", "Delete")) mm.Delete(m);
            }));
        }
        else
        {
            actions.Children.Add(Ui.Button($"Download ({m.SizeDescription})", () => mm.Download(m), primary: advice.Fit == Fit.Recommended));
        }
        col.Children.Add(actions);
        return Ui.Card(col, Look.Md);
    }
}

public sealed partial class SettingsWindow
{
    partial void AddActivityTabCore(TabControl tabs);
    void AddActivityTab(TabControl tabs) => AddActivityTabCore(tabs);
}
