using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InnerVoice.App.UI;

namespace InnerVoice.App.Windows;

/// The Windows releases, newest first: the About window lists the last ten.
public static class ReleaseNotes
{
    public sealed record Release(string Version, string Date, string[] Notes);

    public static readonly Release[] All =
    [
        new("0.1.0", "2026-11-10", [
            "First Windows version: the Narration Tools from InnerVoice for Mac.",
            "Opens Word, PDF, RTF, text, Markdown, OpenDocument, EPUB and HTML manuscripts.",
            "Tag Speakers and Build Summaries with a local AI model; nothing leaves your PC.",
            "Projects open on both Windows and the Mac.",
        ]),
    ];
}

public sealed class AboutWindow : Window
{
    public static void Show(Window owner) => new AboutWindow().ShowDialog(owner);

    AboutWindow()
    {
        Title = "About InnerVoice";
        Width = 520;
        Height = 560;
        CanResize = false;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var col = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Xxl) };
        try
        {
            var image = new Image { Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://InnerVoice/Assets/AppIcon-512.png"))), Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(image);
        }
        catch { }
        col.Children.Add(Ui.Text("InnerVoice", 24, FontWeight.Bold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        col.Children.Add(Ui.Caption($"Version {App.Version} for Windows").Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        col.Children.Add(Ui.Caption("© 2026 Steve Manke. All rights reserved.").Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        col.Children.Add(Ui.Headline("What's New"));
        foreach (var r in ReleaseNotes.All.Take(10))
        {
            col.Children.Add(Ui.Text($"{r.Version} · {r.Date}", 13, FontWeight.SemiBold));
            foreach (var n in r.Notes) col.Children.Add(Ui.Text("• " + n, 12, wrap: TextWrapping.Wrap));
        }
        col.Children.Add(Ui.Headline("Acknowledgments"));
        foreach (var credit in Credits.All)
        {
            var row = new DockPanel();
            var link = new HyperlinkButton { Content = "License", NavigateUri = credit.License, FontSize = 12, Padding = new Thickness(Look.Xs, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(link, Dock.Right);
            row.Children.Add(link);
            row.Children.Add(Ui.Caption(credit.Text, TextWrapping.Wrap));
            col.Children.Add(row);
        }
        col.Children.Add(Ui.Caption("Uses llama.cpp (MIT), ONNX Runtime (MIT), Avalonia (MIT), PdfPig (Apache 2.0), AngleSharp (MIT), RtfPipe (MIT), QuestPDF (Community license), Open XML SDK (MIT), the Anthropic C# SDK (Apache 2.0) and ScottPlot (MIT).", TextWrapping.Wrap));
        Content = Ui.Scroll(col);
    }
}

/// Work by others that ships with InnerVoice or that it downloads, credited as their licenses
/// ask (Creative Commons Attribution needs the creator, the license, a link and any changes).
/// Shown in About and in Help › Voices & Listening. The Mac's `Credits`, with the Windows
/// downloads (the ONNX conversion of Pocket TTS instead of the MLX one).
public static class Credits
{
    public sealed record Credit(string Text, Uri License);

    static readonly Uri CcBy4 = new("https://creativecommons.org/licenses/by/4.0/");

    public static readonly IReadOnlyList<Credit> All =
    [
        new("Pocket TTS voices: the Pocket TTS model and its Mimi codec by Kyutai (kyutai.org), CC BY 4.0, in KevinAHM’s ONNX conversion (KevinAHM/pocket-tts-onnx).", CcBy4),
        new("The stock voice Alba: a clip by Alba MacKenna from Kyutai’s voice collection (kyutai/tts-voices), CC BY 4.0, converted to 24 kHz mono.", CcBy4),
        new("The other eight stock voices were spoken by Orpheus TTS by Canopy Labs, Apache 2.0.", new Uri("https://www.apache.org/licenses/LICENSE-2.0")),
    ];

    /// Help › Voices & Listening › Credits (the Mac's words, with the Windows download).
    public const string HelpText =
        "Pocket TTS is made by Kyutai and shared under the Creative Commons Attribution 4.0 license (CC BY 4.0); InnerVoice downloads KevinAHM’s ONNX conversion of it. Its Mimi audio codec is Kyutai’s too. The stock voice Alba is a clip by Alba MacKenna from Kyutai’s voice collection, CC BY 4.0, converted to 24 kHz mono. The other eight stock voices were spoken by Orpheus TTS (Canopy Labs, Apache 2.0). About InnerVoice lists these with links to the licenses.";
}
