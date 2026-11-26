using Avalonia.Controls;
using Avalonia.Media;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Settings › Voices › Models and Voices (the Mac's ServiceCatalogSection): when each cloud
/// service's lists were last read from its API, what's new, Refresh, and a warning when the
/// chosen model was retired. Shown once any cloud service is set up.
public sealed partial class SettingsWindow
{
    Control? CatalogsCard()
    {
        var catalogs = ServiceCatalogs.Shared;
        var services = Enum.GetValues<CatalogService>().Where(ServiceCatalogs.IsSetUp).ToList();
        if (services.Count == 0) return null;
        var col = Ui.Column(Look.Sm, Ui.Text("Models and Voices", 14, FontWeight.SemiBold));
        foreach (var s in services)
        {
            var st = catalogs.State(s);
            var parts = new List<string>
            {
                st.LastRefreshed is { } t ? $"Updated {t.ToLocalTime():MMM d, h:mm tt}" : "Not refreshed yet",
            };
            if (st.Models.Count > 0) parts.Add($"{st.Models.Count} model{(st.Models.Count == 1 ? "" : "s")}");
            if (st.Voices.Count > 0) parts.Add($"{st.Voices.Count:N0} voices");
            var row = Ui.Row(Look.Sm, Ui.Text(s.Title(), 13, FontWeight.SemiBold), Ui.Caption(string.Join(" · ", parts)));
            if (st.NewSummary() is { } fresh)
            {
                row.Children.Add(Ui.Pill(fresh, Look.Gray));
                row.Children.Add(Ui.Button("Got It", () => catalogs.MarkSeen(s)));
            }
            var refresh = Ui.Button(catalogs.IsRefreshing(s) ? "Refreshing…" : "Refresh", () => _ = catalogs.Refresh(s),
                tip: $"Read {s.Title()}’s models and voices again now");
            refresh.IsEnabled = !catalogs.IsRefreshing(s);
            row.Children.Add(refresh);
            col.Children.Add(row);
            if (catalogs.RetiredWarning(s) is { } warning)
                col.Children.Add(Ui.Text("⚠ " + warning, 12, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap));
            if (catalogs.Failure(s) is { } failure)
                col.Children.Add(Ui.Text($"Couldn’t refresh: {failure}", 12, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap));
        }
        col.Children.Add(Note("InnerVoice reads each service’s models and voices from its own API once a day, so new ones appear without an update. Only the lists are fetched; no book text is sent. Your chosen model never changes on its own. Gemini’s voices are built in because Google doesn’t list them."));
        return Ui.Card(col, Look.Md);
    }
}
