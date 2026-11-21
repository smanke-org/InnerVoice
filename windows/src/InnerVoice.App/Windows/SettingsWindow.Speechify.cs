using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Windows;

/// Speechify in Settings › Voices: marked Cloud (Speechify), with Set Up… until it has a key and
/// the narrator's yes. The Mac's SpeechifyRow and SpeechifySetupSheet
/// (Views/SpeechifySettings.swift).
public sealed partial class SettingsWindow
{
    Control SpeechifyRow()
    {
        var speechify = SpeechifyVoices.Shared;
        var title = Ui.Row(Look.Sm, Ui.Text("Speechify", 14, FontWeight.SemiBold), SpeechifyCloudBadge());
        if (speechify.IsSetUp) title.Children.Add(Ui.Pill("Connected", Look.Gray));
        var col = Ui.Column(Look.Xs, title,
            Ui.Text("Speechify’s voices, made on its servers. The text they speak is sent to Speechify and counted against your Speechify API plan (the free plan includes a monthly allowance of characters). Tones change only the pace, pitch and loudness.", 12, wrap: TextWrapping.Wrap));
        var actions = Ui.Row(Look.Sm);
        if (speechify.IsSetUp)
            actions.Children.Add(Ui.Button("Change…", () => _ = SpeechifySetupDialog().ShowDialog(this)));
        else actions.Children.Add(Ui.Button("Set Up…", () => _ = SpeechifySetupDialog().ShowDialog(this),
            tip: "Connect your Speechify API key. Its voices are made in Speechify’s cloud."));
        col.Children.Add(actions);
        return Ui.Card(col, Look.Md);
    }

    /// Setting up Speechify: what it means for the book's privacy, getting a key, checking it,
    /// and the narrator's yes. Checking the key lists Speechify's voices only; no book text is sent.
    public static Window SpeechifySetupDialog()
    {
        var speechify = SpeechifyVoices.Shared;
        var dialog = new Window
        {
            Title = "Set Up Speechify", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = Look.UIFamily, Background = Look.Brush(Look.Paper),
        };
        static Button Link(string text, Window owner, string url)
        {
            var b = new Button { Content = text, Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent };
            b.Click += (_, _) => _ = owner.Launcher.LaunchUriAsync(new Uri(url));
            return b;
        }
        var warning = new Border
        {
            Background = Look.Brush(Look.Retake.WithAlpha(Look.IsDark ? 0.18 : 0.08)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Look.Md),
            Child = Ui.Column(Look.Xs,
                Ui.Text("Speechify sends your book to Speechify", 13, FontWeight.SemiBold),
                Ui.Text("Everything else in InnerVoice happens on this PC. Speechify doesn’t: its voices are made on Speechify’s servers. Every line you play or export with a Speechify voice is sent there as text, so a table read or an exported book sends the whole manuscript to Speechify, under its API terms.", 13, wrap: TextWrapping.Wrap),
                Ui.Text("Each book asks before its text is sent the first time, separately from ElevenLabs and Google. Use it only for books you have the right to share.", 13, wrap: TextWrapping.Wrap),
                Link("Speechify API Terms", dialog, SpeechifyVoices.TermsPage)),
        };
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key", Text = speechify.ApiKey ?? "" };
        bool checkedKey = false, checking = false;
        var status = Ui.Text("", 12, wrap: TextWrapping.Wrap);
        status.IsVisible = false;
        var checkKey = new Button { Content = "Check Key" };
        var agree = new CheckBox
        {
            Content = new TextBlock { Text = "I understand that when I use Speechify voices, the text they speak from my books is sent to Speechify.", TextWrapping = TextWrapping.Wrap },
            IsChecked = speechify.Consented is not null,
        };
        var turnOn = new Button { Content = "Turn On Speechify", IsDefault = true, IsEnabled = false };
        turnOn.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        var disconnect = new Button { Content = "Disconnect", IsVisible = speechify.IsSetUp };
        ToolTip.SetTip(disconnect, "Forget the key. Nothing more is sent to Speechify.");
        disconnect.Click += (_, _) => { speechify.Disconnect(); dialog.Close(); };
        void Update()
        {
            checkKey.IsEnabled = !string.IsNullOrWhiteSpace(key.Text) && !checking;
            turnOn.IsEnabled = checkedKey && agree.IsChecked == true;
        }
        void Show(string text, Color color)
        {
            status.Text = text;
            status.Foreground = Look.Brush(color);
            status.IsVisible = true;
        }
        async Task Check()
        {
            checking = true;
            checkedKey = false;
            Show("Checking…", Look.Gray);
            Update();
            try
            {
                await SpeechifyVoices.Check(key.Text ?? "");
                checkedKey = true;
                Show("Connected: Speechify accepted the key", Look.Recorded);
            }
            catch (Exception e) { Show(e.Message, Look.Attention); }
            checking = false;
            Update();
        }
        key.TextChanged += (_, _) => { checkedKey = false; status.IsVisible = false; Update(); };
        agree.IsCheckedChanged += (_, _) => Update();
        checkKey.Click += (_, _) => _ = Check();
        turnOn.Click += (_, _) =>
        {
            speechify.Save(key.Text ?? "", consent: true);
            EngineSwitches.Shared.Set(VoiceEngine.Speechify, true);
            dialog.Close();
        };
        Update();
        // A key already saved is checked again when the window opens, as on the Mac.
        if (!string.IsNullOrWhiteSpace(key.Text) && (!AppPaths.IsTestRun || SpeechifyVoices.TestHttp is not null)) _ = Check();
        var keyRow = new DockPanel();
        DockPanel.SetDock(checkKey, Dock.Right);
        checkKey.Margin = new Thickness(Look.Sm, 0, 0, 0);
        keyRow.Children.Add(checkKey);
        keyRow.Children.Add(key);
        var buttons = Ui.Row(Look.Sm, disconnect, cancel, turnOn);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children =
            {
                Ui.Row(Look.Sm, Ui.Title("Set Up Speechify"), SpeechifyCloudBadge()),
                warning,
                Ui.Column(Look.Xs, Ui.Headline("1. Get an API key"),
                    Ui.Text("On the Speechify API platform, sign in, create an API key, then copy it. The API is a separate plan from the Speechify reading app: a Speechify Premium subscription doesn’t include it.", 13, wrap: TextWrapping.Wrap),
                    Link("Open Speechify API Keys", dialog, SpeechifyVoices.KeysPage)),
                Ui.Column(Look.Xs, Ui.Headline("2. Paste it here"), keyRow, status,
                    Ui.Caption("The key is kept in Windows Credential Manager.")),
                agree, buttons,
            },
        };
        return dialog;
    }
}
