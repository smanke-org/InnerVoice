// Ties the voices' windows together without each knowing the others: Clone a Voice from the
// Book menu and the Voices page, and ElevenLabs' Voice Design on the Voices page.

using Avalonia.Controls;
using InnerVoice.App.Pages;
using InnerVoice.App.UI;
using InnerVoice.App.Windows;
using InnerVoice.Core;

namespace InnerVoice.App.Speech;

public static class VoiceHooks
{
    static bool installed;

    /// A voice model was downloaded or removed: pages that list engines redraw.
    public static event Action? EnginesUpdated;
    public static void EnginesChanged() => EnginesUpdated?.Invoke();

    public static void Install()
    {
        if (installed) return;
        installed = true;
        ProjectWindow.OpenCloneVoiceWindow = (window, document, characterID) =>
        {
            var w = new CloneVoiceWindow(document);
            if (characterID is { } id) w.Prepare("", rights: false, useFor: id);
            _ = w.ShowDialog(window);
        };
        VoicesPage.CloudDesign = Design;
    }

    /// Design a Voice from Voice Notes (ElevenLabs): only on the narrator's click, only for a
    /// book that allows ElevenLabs; it uses credits and a voice slot.
    static Control? Design(VoicesPage page, BookCharacter c)
    {
        var document = page.Document;
        if (!ElevenLabsEngine.DesignOffered(document.Project)) return null;
        var description = ElevenLabsEngine.DesignDescription(c, document.Project, document.Index);
        var status = Ui.Caption("");
        var id = c.Id;
        Button? go = null;
        go = Ui.Button("Design Voice", async () =>
        {
            go!.IsEnabled = false;
            status.Text = $"Designing {c.FirstName}’s voice with ElevenLabs…";
            try
            {
                var info = await ElevenLabsEngine.Shared.DesignVoice(document.Project.Character(id) ?? c, document.Project, CancellationToken.None);
                document.SetVoice(info.Choice, id);
                status.Text = "";
            }
            catch (Exception e)
            {
                status.Text = e.Message;
                go.IsEnabled = true;
            }
        });
        return Ui.Card(Ui.Column(Look.Sm,
            Ui.Headline("Design a Voice from Voice Notes"),
            Ui.Caption($"ElevenLabs designs a new voice for {c.FirstName} from this description, which includes {c.FirstName}’s Voice Notes, and adds it to your ElevenLabs voices. The description is sent to ElevenLabs; designing uses credits and one of your plan’s voice slots.", Avalonia.Media.TextWrapping.Wrap),
            Ui.Text(description, wrap: Avalonia.Media.TextWrapping.Wrap),
            Ui.Caption("Edit Voice Notes to change what’s sent."),
            Ui.Row(Look.Sm, go, status)), Look.Md);
    }
}
