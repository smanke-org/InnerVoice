using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace InnerVoice.App.Controls;

/// A text field bound to the book that types into a draft and saves on a pause, Enter (for
/// one-line fields), or leaving the field: one undo step per edit, and no whole-book
/// rebuild per keystroke (the Mac's DraftTextField).
public sealed class DraftTextBox : TextBox
{
    readonly Action<string> commit;
    string committed;
    readonly DispatcherTimer pause = new() { Interval = TimeSpan.FromSeconds(1.2) };

    protected override Type StyleKeyOverride => typeof(TextBox);

    public DraftTextBox(string text, Action<string> commit, bool multiline = false, string? watermark = null)
    {
        this.commit = commit;
        committed = text;
        Text = text;
        PlaceholderText = watermark;
        AcceptsReturn = multiline;
        TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
        pause.Tick += (_, _) => { pause.Stop(); Commit(); };
        TextChanged += (_, _) => { pause.Stop(); pause.Start(); };
        LostFocus += (_, _) => { pause.Stop(); Commit(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !multiline) { pause.Stop(); Commit(); e.Handled = true; }
            if (e.Key == Key.Escape) { Text = committed; pause.Stop(); }
        };
    }

    void Commit()
    {
        var t = Text ?? "";
        if (t == committed) return;
        committed = t;
        commit(t);
    }
}
