// Ported from ExportSheet in Sources/InnerVoice/Export/Exporter.swift (File › Export…).

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InnerVoice.App.Document;
using InnerVoice.App.UI;

namespace InnerVoice.App.Export;

/// Picks what to export and how, then asks where to save it.
public sealed class ExportWindow : Window
{
    readonly ProjectDocument document;
    readonly Exporter.Options options = new();
    readonly StackPanel content = new() { Margin = new Thickness(Look.Xl), Spacing = Look.Lg };
    string? error;

    public static Task Show(Window owner, ProjectDocument document) => new ExportWindow(document).ShowDialog(owner);

    public ExportWindow(ProjectDocument document)
    {
        this.document = document;
        Title = "Export";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        FontFamily = Look.UIFamily;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = content;
        document.Changed += OnChanged;
        Closed += (_, _) => document.Changed -= OnChanged;
        Build();
    }

    void OnChanged(bool _)
    {
        // A narrator removed elsewhere can't stay picked.
        if (options.NarratorID is { } id && document.Project.Narrators.All(n => n.Id != id)) options.NarratorID = null;
        Build();
    }

    void Build()
    {
        content.Children.Clear();
        content.Children.Add(Ui.Text("Export", 17, FontWeight.SemiBold));

        var kinds = new StackPanel { Spacing = Look.Xs };
        foreach (var kind in Exporter.Kinds)
        {
            var radio = new RadioButton { Content = kind.Title(), GroupName = "ExportKind", IsChecked = options.Kind == kind };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true || options.Kind == kind) return;
                options.Kind = kind;
                if (!Exporter.Formats(kind).Contains(options.Format)) options.Format = Exporter.Format.Pdf;
                Later(Build);
            };
            kinds.Children.Add(radio);
        }
        content.Children.Add(kinds);
        content.Children.Add(Ui.Caption(options.Kind.Detail(), TextWrapping.Wrap));

        var form = new StackPanel { Spacing = Look.Md };
        var formats = Ui.Row(Look.Md);
        foreach (var format in Exporter.Formats(options.Kind))
        {
            var radio = new RadioButton { Content = format.Title(), GroupName = "ExportFormat", IsChecked = options.Format == format };
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) options.Format = format; };
            formats.Children.Add(radio);
        }
        form.Children.Add(Row("Format", formats));
        if (options.Kind == Exporter.Kind.NarratorScript)
        {
            var narrators = document.Project.Narrators;
            var picker = new ComboBox
            {
                ItemsSource = new[] { "Every Character" }.Concat(narrators.Select(n => n.Name)).ToList(),
                SelectedIndex = options.NarratorID is { } id ? narrators.FindIndex(n => n.Id == id) + 1 : 0,
                MinWidth = 220,
            };
            picker.SelectionChanged += (_, _) =>
                options.NarratorID = picker.SelectedIndex > 0 ? narrators[picker.SelectedIndex - 1].Id : null;
            form.Children.Add(Row("Narrator", picker));
            var cues = new CheckBox { Content = "Include cue lines", IsChecked = options.IncludeCues };
            cues.IsCheckedChanged += (_, _) => options.IncludeCues = cues.IsChecked == true;
            var summaries = new CheckBox { Content = "Include character summaries", IsChecked = options.IncludeSummaries };
            summaries.IsCheckedChanged += (_, _) => options.IncludeSummaries = summaries.IsChecked == true;
            form.Children.Add(cues);
            form.Children.Add(summaries);
            if (narrators.Count == 0)
                form.Children.Add(Ui.Caption("Add narrators and cast characters to them to export one narrator's lines.", TextWrapping.Wrap));
        }
        content.Children.Add(Ui.Card(form));

        if (error is not null) content.Children.Add(Ui.Text(error, 13, brush: Look.Brush(Look.Retake), wrap: TextWrapping.Wrap));

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        var export = new Button { Content = "Export…", IsDefault = true };
        export.Classes.Add("accent");
        export.Click += async (_, _) => await Export();
        var buttons = Ui.Row(Look.Sm, cancel, export);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        content.Children.Add(buttons);
    }

    /// Rebuilding inside a radio button's own event would remove it mid-click.
    static void Later(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = Look.Md };
        var l = Ui.Text(label, 13);
        l.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    /// "<Book> - <Export> - <Narrator>", like the Mac, without characters Windows forbids in names.
    public static string SuggestedName(ProjectDocument document, Exporter.Options options)
    {
        var narrator = document.Project.Narrators.FirstOrDefault(n => n.Id == options.NarratorID)?.Name;
        var name = $"{document.Project.Title} - {options.Kind.Title()}{(narrator is null ? "" : $" - {narrator}")}";
        foreach (var bad in "<>:\"/\\|?*") name = name.Replace(bad, '-');
        return name.Trim().TrimEnd('.');
    }

    async Task Export()
    {
        var ext = options.Format.Extension();
        var type = new FilePickerFileType(options.Format == Exporter.Format.Word ? "Word Document" : options.Format.Title())
        {
            Patterns = [$"*.{ext}"],
        };
        var path = await Dialogs.SaveFile(this, "Export", $"{SuggestedName(document, options)}.{ext}", ext, type);
        if (path is null) return;
        try
        {
            Exporter.Write(options, document, path);
        }
        catch (Exception e)
        {
            error = $"Unable to export: {e.Message}";
            Build();
            await Dialogs.Alert(this, "Unable to Export", e.Message);
            return;
        }
        Close();
        Reveal(path);
    }

    /// Shows the new file in File Explorer, like the Mac selects it in the Finder.
    static void Reveal(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch { }
    }
}
