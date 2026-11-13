using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace InnerVoice.App.UI;

/// Small modal questions and file pickers.
public static class Dialogs
{
    static Window? Owner(Visual v) => TopLevel.GetTopLevel(v) as Window;

    public static async Task<bool> Confirm(Visual from, string title, string message, string action, bool destructive = true, string cancel = "Cancel")
    {
        var owner = Owner(from);
        bool result = false;
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var ok = new Button { Content = action, IsDefault = !destructive };
        ok.Classes.Add(destructive ? "danger" : "accent");
        var no = new Button { Content = cancel, IsCancel = true, IsDefault = destructive };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        var buttons = Ui.Row(Look.Sm, no, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children = { Ui.Headline(title), Ui.Text(message, 13, wrap: Avalonia.Media.TextWrapping.Wrap), buttons },
        };
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
        return result;
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
        Patterns = ["*.docx", "*.pdf", "*.rtf", "*.txt", "*.text", "*.md", "*.markdown", "*.odt", "*.epub", "*.html", "*.htm", "*.xhtml"],
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
