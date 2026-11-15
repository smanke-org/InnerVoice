using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace InnerVoice.App.UI;

/// Small modal questions and file pickers.
public static class Dialogs
{
    static Window? Owner(Visual v) => TopLevel.GetTopLevel(v) as Window;

    /// `cancelIsDefault`: Return cancels even though the action isn't destructive (a warning).
    public static async Task<bool> Confirm(Visual from, string title, string message, string action, bool destructive = true, string cancel = "Cancel",
                                           bool cancelIsDefault = false) =>
        (await ConfirmWithCheck(from, title, message, action, null, destructive, cancel, cancelIsDefault)).Ok;

    /// A question with a checkbox under it ("Don't show the estimate again").
    public static async Task<(bool Ok, bool Checked)> ConfirmWithCheck(Visual from, string title, string message, string action, string? checkLabel,
                                                                       bool destructive = false, string cancel = "Cancel",
                                                                       bool cancelIsDefault = false)
    {
        var owner = Owner(from);
        bool result = false;
        var check = checkLabel is null ? null : new CheckBox { Content = checkLabel };
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        bool cancelFirst = destructive || cancelIsDefault;
        var ok = new Button { Content = action, IsDefault = !cancelFirst };
        if (destructive) ok.Classes.Add("danger");
        else if (!cancelIsDefault) ok.Classes.Add("accent");
        var no = new Button { Content = cancel, IsCancel = true, IsDefault = cancelFirst };
        if (cancelIsDefault && !destructive) no.Classes.Add("accent");
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        var buttons = Ui.Row(Look.Sm, no, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children = { Ui.Headline(title), Ui.Text(message, 13, wrap: Avalonia.Media.TextWrapping.Wrap) },
        };
        if (check is not null) ((StackPanel)dialog.Content).Children.Add(check);
        ((StackPanel)dialog.Content).Children.Add(buttons);
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
        return (result, check?.IsChecked == true);
    }

    public static async Task Alert(Visual from, string title, string message)
    {
        var owner = Owner(from);
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children = { Ui.Headline(title), Ui.Text(message, 13, wrap: Avalonia.Media.TextWrapping.Wrap), ok },
        };
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
    }

    /// A question with several answers, then Cancel, left to right: the index of the answer
    /// picked, or -1 for Cancel. `defaultIndex` is the answer Enter picks (-1: none).
    /// `stacked`: one answer per row, for a long list (an engine each).
    public static async Task<int> Choose(Visual from, string title, string message, string[] actions, string cancel = "Cancel", int defaultIndex = 0,
                                         bool stacked = false)
    {
        var owner = Owner(from);
        int result = -1;
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var buttons = stacked ? Ui.Column(Look.Sm) : Ui.Row(Look.Sm);
        buttons.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        for (int i = 0; i < actions.Length; i++)
        {
            int index = i;
            var b = new Button { Content = actions[i], IsDefault = i == defaultIndex };
            if (stacked) { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            if (i == defaultIndex) b.Classes.Add("accent");
            b.Click += (_, _) => { result = index; dialog.Close(); };
            buttons.Children.Add(b);
        }
        var no = new Button { Content = cancel, IsCancel = true };
        if (stacked) { no.HorizontalAlignment = HorizontalAlignment.Stretch; no.HorizontalContentAlignment = HorizontalAlignment.Center; }
        no.Click += (_, _) => dialog.Close();
        buttons.Children.Add(no);
        var content = new StackPanel { Margin = new Thickness(Look.Xl), Spacing = Look.Md };
        var head = Ui.Headline(title);
        head.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(head);
        if (message.Length > 0) content.Children.Add(Ui.Text(message, 13, wrap: Avalonia.Media.TextWrapping.Wrap));
        content.Children.Add(buttons);
        dialog.Content = content;
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
        return result;
    }

    /// Asks for one line of text; null when cancelled.
    public static async Task<string?> Ask(Visual from, string title, string message, string action, string initial = "", string? watermark = null)
    {
        var owner = Owner(from);
        string? result = null;
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var box = new TextBox { Text = initial, PlaceholderText = watermark };
        var ok = new Button { Content = action, IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { result = box.Text ?? ""; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = Ui.Row(Look.Sm, cancel, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var content = new StackPanel { Margin = new Thickness(Look.Xl), Spacing = Look.Md };
        content.Children.Add(Ui.Headline(title));
        if (message.Length > 0) content.Children.Add(Ui.Text(message, 13, wrap: Avalonia.Media.TextWrapping.Wrap));
        content.Children.Add(box);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
        return result;
    }

    public static readonly FilePickerFileType Projects = new("InnerVoice Projects") { Patterns = ["*.innervoice"] };
    public static readonly FilePickerFileType Manuscripts = new("Manuscripts")
    {
        Patterns = ["*.docx", "*.pdf", "*.rtf", "*.txt", "*.text", "*.md", "*.markdown", "*.odt", "*.pages", "*.epub", "*.html", "*.htm", "*.xhtml"],
    };

    public static async Task<string?> OpenFile(Visual from, string title, params FilePickerFileType[] types)
    {
        var top = TopLevel.GetTopLevel(from);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = types });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SaveFile(Visual from, string title, string suggestedName, string extension, FilePickerFileType type, string? folder = null)
    {
        var top = TopLevel.GetTopLevel(from);
        if (top is null) return null;
        var options = new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension, FileTypeChoices = [type],
            ShowOverwritePrompt = true,
        };
        if (folder is not null && Directory.Exists(folder)) options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await top.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }
}
