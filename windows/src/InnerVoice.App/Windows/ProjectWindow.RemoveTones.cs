// Book > Remove Tones…, the Mac's Speech/RemoveTones.swift: clears tones by who set them,
// as one undoable change.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

public sealed partial class ProjectWindow
{
    public async Task RemoveTones()
    {
        if (!Document.Project.ShowsVoices || Runner.IsRunning) return;
        var (automatic, narrator) = ToneRemoval.Counts(Document.Project);
        if (automatic + narrator == 0)
        {
            await Dialogs.Alert(this, "No Tones to Remove", "No line in this book has a tone yet.");
            return;
        }
        var choice = await AskRemoveTones(automatic, narrator);
        if (choice is not { IsEmpty: false } c) return;
        Document.Change("Remove Tones", p => c.Apply(p));
    }

    /// The warning with its two checkboxes. Cancel is the default button, so Enter never
    /// removes anything. Null when cancelled.
    async Task<ToneRemoval?> AskRemoveTones(int automatic, int narrator)
    {
        ToneRemoval? result = null;
        var dialog = new Window
        {
            Title = "Remove Tones?", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var auto = new CheckBox { Content = $"Tones from Tag Speakers ({automatic:N0} lines)", IsChecked = automatic > 0, IsEnabled = automatic > 0 };
        var own = new CheckBox { Content = $"Tones you set yourself ({narrator:N0} lines)", IsChecked = false, IsEnabled = narrator > 0 };
        var remove = new Button { Content = "Remove Tones" };
        remove.Classes.Add("danger");
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        void Update() => remove.IsEnabled = auto.IsChecked == true || own.IsChecked == true;
        auto.IsCheckedChanged += (_, _) => Update();
        own.IsCheckedChanged += (_, _) => Update();
        Update();
        remove.Click += (_, _) => { result = new ToneRemoval(auto.IsChecked == true, own.IsChecked == true); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = Ui.Row(Look.Sm, cancel, remove);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children =
            {
                Ui.Headline("Remove Tones?"),
                Ui.Text("Lines without a tone are read evenly. Getting tones back means running Tag Speakers again, which can take a long time for a whole book. " +
                        "Tones you remove from your own choices can't be tagged back; you'd set them again by hand.\n\n" +
                        "You can undo this with Edit › Undo while the book is open.", 13, wrap: Avalonia.Media.TextWrapping.Wrap),
                new StackPanel { Spacing = 6, Children = { auto, own } },
                buttons,
            },
        };
        dialog.Opened += (_, _) => cancel.Focus();
        await dialog.ShowDialog(this);
        return result;
    }
}
