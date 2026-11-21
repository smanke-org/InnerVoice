// The Mac's Speech/CloudGate.swift: a book whose engine is a cloud one (ElevenLabs, Gemini
// TTS, Speechify) speaks only once the book allows its text to go to that company.

using Avalonia;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Speech;

/// Every play, preview and export asks here first: the permission question (Don't Send is
/// the default), and if the narrator says no, nothing plays and they're told how to play on
/// this PC instead.
public static class CloudGate
{
    /// The book speaks with a cloud engine it hasn't allowed yet.
    public static bool NeedsAsk(ProjectDocument document)
    {
        var p = document.Project;
        var e = p.EngineInUse();
        return p.ShowsVoices && e.IsCloud() && !p.Settings.AllowsCloud(e);
    }

    /// Whether the book may speak with its engine now (asking when it hasn't said yet). Test
    /// runs never ask: nothing is sent.
    public static async Task<bool> Allows(ProjectDocument document, Visual? from = null)
    {
        if (!NeedsAsk(document)) return true;
        if (AppPaths.IsTestRun) return false;
        from ??= App.Current.Projects.FirstOrDefault(w => w.Document == document) ?? (Visual?)App.Current.FrontProject;
        if (from is null) return false;
        var project = document.Project;
        var e = project.EngineInUse();
        var company = e.CloudCompany() ?? "the cloud";
        var servers = company.EndsWith('s') ? company + "’" : company + "’s";
        var a = "AEIOU".Contains(e.Title()[0]) ? "an" : "a";
        if (await Dialogs.Confirm(from, $"Send This Book to {company}?",
                $"“{project.Title}” speaks with {e.Title()}, which makes its voices on {servers} servers. Every line {a} {e.Title()} voice speaks is sent there as text; a table read or an exported book sends the whole manuscript. Allow it only if you have the right to share this book.",
                $"Send to {company}", destructive: false, cancel: "Don’t Send", cancelIsDefault: true))
        {
            document.SetCloudAllowed(e, true);
            return true;
        }
        await Dialogs.Alert(from, "Nothing Will Play",
            $"“{project.Title}” speaks with {e.Title()}, which needs your permission to send its text to {company}. To play on this PC instead, open the Voices page and choose an engine that runs on this PC, such as Windows Voices, Pocket TTS or Qwen3-TTS, from the engine menu in the toolbar. To allow {company} later, play again and choose Send.");
        return false;
    }

    /// For commands that play at once: true when nothing needs asking. Otherwise it asks in
    /// the background, runs `again` after a yes, and returns false.
    public static bool Ready(ProjectDocument document, Action again)
    {
        if (!NeedsAsk(document)) return true;
        _ = AskThen();
        return false;

        async Task AskThen()
        {
            if (await Allows(document)) again();
        }
    }
}
