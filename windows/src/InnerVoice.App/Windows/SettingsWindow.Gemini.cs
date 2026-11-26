using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Windows;

/// Gemini TTS in Settings › Voices: marked Cloud (Google), with Set Up… until it has a key and
/// the narrator's yes, and the model choice once it's on. The Mac's GeminiRow and
/// GeminiSetupSheet (Views/GeminiSettings.swift).
public sealed partial class SettingsWindow
{
    Control GeminiRow()
    {
        var gemini = GeminiVoices.Shared;
        var title = Ui.Row(Look.Sm, Ui.Text("Gemini TTS", 14, FontWeight.SemiBold), GoogleCloudBadge());
        if (gemini.IsSetUp) title.Children.Add(Ui.Pill("Connected", Look.Gray));
        var col = Ui.Column(Look.Xs, title,
            Ui.Text("Thirty voices made on Google’s servers, directed in plain words for each tone. The text they speak is sent to Google and billed to your Gemini API key (about $8 for a 10-hour book with Flash in 2026).", 12, wrap: TextWrapping.Wrap));
        var actions = Ui.Row(Look.Sm);
        if (gemini.IsSetUp)
        {
            actions.Children.Add(Ui.Button("Change…", () => _ = GeminiSetupDialog().ShowDialog(this)));
        }
        else actions.Children.Add(Ui.Button("Set Up…", () => _ = GeminiSetupDialog().ShowDialog(this),
            tip: "Connect your Gemini API key. Its voices are made in Google’s cloud."));
        col.Children.Add(actions);
        if (gemini.IsSetUp && EngineSwitches.Shared.IsOn(VoiceEngine.Gemini))
        {
            var models = gemini.ModelIDs.ToList();
            var fresh = ServiceCatalogs.Shared.State(CatalogService.Gemini).NewModels;
            var box = new ComboBox { ItemsSource = models.Select(id => GeminiTTS.TitleFor(id) + (fresh.Contains(id) ? " — New" : "")).ToList(), Width = 320 };
            box.SelectedIndex = models.IndexOf(gemini.ModelID);
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && models[box.SelectedIndex] != gemini.ModelID) gemini.ModelID = models[box.SelectedIndex]; };
            ToolTip.SetTip(box, "Flash acts the tones best. Flash-Lite costs about two thirds as much. Newer models Google adds appear here after a refresh; their cost is estimated from the closest known model.");
            col.Children.Add(WideRow("Gemini model", box));
        }
        return Ui.Card(col, Look.Md);
    }

    /// Setting up Gemini TTS: what it means for the book's privacy, getting a key, checking it,
    /// and the narrator's yes. Checking the key lists Google's voices only; no book text is sent.
    public static Window GeminiSetupDialog()
    {
        var gemini = GeminiVoices.Shared;
        var dialog = new Window
        {
            Title = "Set Up Gemini TTS", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false,
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
                Ui.Text("Gemini TTS sends your book to Google", 13, FontWeight.SemiBold),
                Ui.Text("Everything else in InnerVoice happens on this PC. Gemini TTS doesn’t: its voices are made on Google’s servers. Every line you play or export with a Gemini voice is sent there as text, so a table read or an exported book sends the whole manuscript to Google, under the Gemini API terms.", 13, wrap: TextWrapping.Wrap),
                Ui.Text("Each book asks before its text is sent the first time, separately from ElevenLabs. Use it only for books you have the right to share.", 13, wrap: TextWrapping.Wrap),
                Link("Gemini API Terms", dialog, GeminiVoices.PrivacyPage)),
        };
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key", Text = gemini.ApiKey ?? "" };
        bool checkedKey = false, checking = false;
        var status = Ui.Text("", 12, wrap: TextWrapping.Wrap);
        status.IsVisible = false;
        var checkKey = new Button { Content = "Check Key" };
        var agree = new CheckBox
        {
            Content = new TextBlock { Text = "I understand that when I use Gemini TTS voices, the text they speak from my books is sent to Google.", TextWrapping = TextWrapping.Wrap },
            IsChecked = gemini.Consented is not null,
        };
        var turnOn = new Button { Content = "Turn On Gemini TTS", IsDefault = true, IsEnabled = false };
        turnOn.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        var disconnect = new Button { Content = "Disconnect", IsVisible = gemini.IsSetUp };
        ToolTip.SetTip(disconnect, "Forget the key. Nothing more is sent to Google.");
        disconnect.Click += (_, _) => { gemini.Disconnect(); dialog.Close(); };
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
                await GeminiVoices.Check(key.Text ?? "");
                checkedKey = true;
                Show("Connected: Google accepted the key", Look.Recorded);
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
            gemini.Save(key.Text ?? "", consent: true);
            EngineSwitches.Shared.Set(VoiceEngine.Gemini, true);
            dialog.Close();
        };
        Update();
        // A key already saved is checked again when the window opens, as on the Mac.
        if (!string.IsNullOrWhiteSpace(key.Text) && (!AppPaths.IsTestRun || GeminiVoices.TestHttp is not null)) _ = Check();
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
                Ui.Row(Look.Sm, Ui.Title("Set Up Gemini TTS"), GoogleCloudBadge()),
                warning,
                Ui.Column(Look.Xs, Ui.Headline("1. Get an API key"),
                    Ui.Text("In Google AI Studio, create a Gemini API key, then copy it. Turn on billing for its project: on the free tier Google may use what you send to improve its products, and the free limits are too small for a book.", 13, wrap: TextWrapping.Wrap),
                    Link("Open Google AI Studio", dialog, GeminiVoices.KeysPage)),
                Ui.Column(Look.Xs, Ui.Headline("2. Paste it here"), keyRow, status,
                    Ui.Caption("The key is kept in Windows Credential Manager.")),
                agree, buttons,
            },
        };
        return dialog;
    }
}
