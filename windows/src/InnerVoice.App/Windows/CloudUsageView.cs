using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.AI;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.AI;
using InnerVoice.Core.Speech;
using ScottPlot.Avalonia;

namespace InnerVoice.App.Windows;

/// Which cloud services this book is using right now: Claude when it's the chosen model,
/// ElevenLabs only when this book has it turned on. The Cloud Usage button shows only when
/// there's at least one. The Mac's CloudInUse.
public static class CloudInUse
{
    public static bool Claude() => ModelManager.Shared.SelectionIsClaude;

    /// The ElevenLabs key while ElevenLabs is set up (Credential Manager, with the narrator's
    /// yes); the engine sets this when it starts. null: not set up, so never in use.
    public static Func<string?> ElevenLabsKey { get; set; } = () => null;

    /// The book shows voices and allows ElevenLabs, a key is set up, and ElevenLabs is the
    /// book's engine or one of Mix Engines'.
    public static bool ElevenLabs(Project project)
    {
        if (!project.ShowsVoices || project.Settings.CloudVoicesAllowed is null || string.IsNullOrWhiteSpace(ElevenLabsKey())) return false;
        var engine = Speech.VoiceSetup.EngineInUse(project);
        return engine == VoiceEngine.Elevenlabs || (engine == VoiceEngine.Mixed && EngineSwitches.Shared.IsOn(VoiceEngine.Elevenlabs));
    }

    /// Whether Gemini TTS is set up (a key and the narrator's yes); the engine sets this.
    public static Func<bool> GeminiSetUp { get; set; } = () => false;

    /// The book shows voices and allows Google, Gemini TTS is set up, and it's the book's
    /// engine or one of Mix Engines'.
    public static bool Gemini(Project project)
    {
        if (!project.ShowsVoices || project.Settings.GoogleVoicesAllowed is null || !GeminiSetUp()) return false;
        var engine = Speech.VoiceSetup.EngineInUse(project);
        return engine == VoiceEngine.Gemini || (engine == VoiceEngine.Mixed && EngineSwitches.Shared.IsOn(VoiceEngine.Gemini));
    }

    /// Whether Speechify is set up (a key and the narrator's yes); the engine sets this.
    public static Func<bool> SpeechifySetUp { get; set; } = () => false;

    /// The book shows voices and allows Speechify, Speechify is set up, and it's the book's
    /// engine or one of Mix Engines'.
    public static bool Speechify(Project project)
    {
        if (!project.ShowsVoices || project.Settings.SpeechifyVoicesAllowed is null || !SpeechifySetUp()) return false;
        var engine = Speech.VoiceSetup.EngineInUse(project);
        return engine == VoiceEngine.Speechify || (engine == VoiceEngine.Mixed && EngineSwitches.Shared.IsOn(VoiceEngine.Speechify));
    }

    public static bool Any(Project project) => Claude() || ElevenLabs(project) || Gemini(project) || Speechify(project);
}

/// The toolbar's Cloud Usage popover: for each cloud model, tokens in and out (characters for
/// ElevenLabs), requests and the cost at list price, with charts by day (ScottPlot), for This
/// Book or All Books. The Mac's CloudUsageView.
public sealed class CloudUsageView : UserControl
{
    readonly Project project;
    static readonly string[] Palette = ["#4F46E5", "#0D9488", "#D97706", "#0284C7", "#7C3AED", "#16A34A"];
    static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    /// This Book or All Books, remembered while the app runs (the Mac keeps it in a default).
    static bool allBooks;
    ElevenLabsCredits? credits;
    string? creditsProblem;
    bool creditsAsked;

    public CloudUsageView(Project project)
    {
        this.project = project;
        Width = 480;
        Build();
    }

    static string Money(double d) => d.ToString(d < 1 ? "$0.00##" : "$#,##0.00", Us);

    /// Which services show: each one while it's in use, and always once it has usage here
    /// (this book, or every book): an engine switched off later still cost what it cost.
    public static (bool Claude, bool ElevenLabs, bool Gemini, bool Speechify) Shown(Project project, string? book, IEnumerable<CloudUsageRecord> records)
    {
        var used = records.Where(r => book is null || r.Book == book).Select(r => r.Service).ToHashSet();
        return (CloudInUse.Claude() || used.Contains(CloudService.Claude),
                CloudInUse.ElevenLabs(project) || used.Contains(CloudService.Elevenlabs),
                CloudInUse.Gemini(project) || used.Contains(CloudService.Google),
                CloudInUse.Speechify(project) || used.Contains(CloudService.Speechify));
    }

    void Build()
    {
        var book = allBooks ? null : project.UsageKey();
        var (showClaude, showEleven, showGemini, showSpeechify) = Shown(project, book, CloudUsageStore.Shared.Records);
        var records = CloudUsageStore.Shared.Records
            .Where(r => (r.Service == CloudService.Claude && showClaude) || (r.Service == CloudService.Elevenlabs && showEleven)
                        || (r.Service == CloudService.Google && showGemini) || (r.Service == CloudService.Speechify && showSpeechify)).ToList();
        var services = new[] { showClaude ? "Anthropic" : null, showEleven ? "ElevenLabs" : null, showGemini ? "Google" : null, showSpeechify ? "Speechify" : null }.OfType<string>().ToList();
        var totals = CloudUsage.Totals(records, book);
        var days = CloudUsage.ByDay(records, book);
        var mm = ModelManager.Shared;
        string? currentClaude = mm.SelectionIsClaude ? mm.Selection[Claude.SelectionPrefix.Length..] : null;

        var panel = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Lg) };
        var switcher = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var (label, value) in new[] { ("This Book", false), ("All Books", true) })
        {
            var b = new ToggleButton { Content = label, IsChecked = allBooks == value, Padding = new Thickness(10, 3) };
            b.Click += (_, _) => { allBooks = value; Build(); };
            switcher.Children.Add(b);
        }
        var head = new DockPanel();
        DockPanel.SetDock(switcher, Dock.Right);
        head.Children.Add(switcher);
        var badge = SettingsWindow.CloudBadge();
        ToolTip.SetTip(badge, "Uses the internet: text is sent to " + (services.Count <= 2 ? string.Join(" and ", services) : string.Join(", ", services.SkipLast(1)) + " and " + services[^1]));
        head.Children.Add(Ui.Row(Look.Sm, Ui.Text("Cloud Usage", 16, FontWeight.SemiBold), badge));
        panel.Children.Add(head);

        if (showClaude)
        {
            var claude = totals.Where(t => t.Service == CloudService.Claude).ToList();
            var col = new StackPanel { Spacing = Look.Sm };
            if (claude.Count == 0) col.Children.Add(Empty("Nothing sent to Claude yet" + (allBooks ? "." : " for this book.")));
            foreach (var t in claude)
            {
                col.Children.Add(ModelRow(ClaudeAccount.Shared.NameOf(t.Model), t.Model == currentClaude,
                    [("Tokens in", t.InputTokens + t.CacheReadTokens + t.CacheWriteTokens), ("Tokens out", t.OutputTokens), ("Requests", t.Requests)],
                    t.Cost is { } c ? Money(c) : "Price not known"));
                if (CloudUsageStore.Shared.ClaudeTokensLeft(t.Model) is { } left)
                    col.Children.Add(Ui.Caption($"{left.ToString("N0", Us)} tokens left in this minute's rate limit"));
            }
            if (claude.Count > 1)
                col.Children.Add(Ui.Text($"All Claude models: {Money(claude.Select(c => c.Cost ?? 0).Sum())}", 13, FontWeight.SemiBold));
            col.Children.Add(Ui.Caption("Costs are worked out from Anthropic's list prices and may differ from your bill. Your account's remaining credit is in the Claude Console.", TextWrapping.Wrap));
            panel.Children.Add(Section("Claude", col));
        }

        if (showEleven)
        {
            var el = totals.Where(t => t.Service == CloudService.Elevenlabs).ToList();
            var col = new StackPanel { Spacing = Look.Sm };
            if (el.Count == 0) col.Children.Add(Empty("No ElevenLabs voices made yet" + (allBooks ? "." : " for this book.")));
            foreach (var t in el)
                col.Children.Add(ModelRow(t.Model, true, [("Characters", t.Characters), ("Requests", t.Requests)], null));
            if (credits is { } cr)
            {
                col.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, cr.Limit), Value = cr.Used, Height = 6 });
                var tier = cr.Tier.Length == 0 ? "" : char.ToUpperInvariant(cr.Tier[0]) + cr.Tier[1..] + " ";
                col.Children.Add(Ui.Caption($"Your {tier}plan: {cr.Used.ToString("N0", Us)} of {cr.Limit.ToString("N0", Us)} credits used, {cr.Left.ToString("N0", Us)} left this period", TextWrapping.Wrap));
            }
            else if (creditsProblem is { } problem) col.Children.Add(Ui.Caption(problem, TextWrapping.Wrap));
            else
            {
                col.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, HorizontalAlignment = HorizontalAlignment.Left });
                LoadCredits();
            }
            panel.Children.Add(Section("ElevenLabs", col));
        }

        if (showGemini)
        {
            var g = totals.Where(t => t.Service == CloudService.Google).ToList();
            var col = new StackPanel { Spacing = Look.Sm };
            if (g.Count == 0) col.Children.Add(Empty("No Gemini voices made yet" + (allBooks ? "." : " for this book.")));
            foreach (var t in g)
                col.Children.Add(ModelRow(t.Model, t.Model == Speech.GeminiVoices.Shared.Model.Raw(),
                    [("Characters", t.Characters), ("Seconds of audio", (long)(t.AudioSeconds ?? 0)), ("Requests", t.Requests)],
                    t.Cost is { } c ? c.ToString("$#,##0.00##", Us) : "Price not known"));
            col.Children.Add(Ui.Caption("Costs are worked out from Google’s paid-tier list prices (which double on January 1, 2027) and may differ from your bill. Your spending is in Google AI Studio.", TextWrapping.Wrap));
            panel.Children.Add(Section("Gemini TTS", col));
        }

        if (showSpeechify)
        {
            var sp = totals.Where(t => t.Service == CloudService.Speechify).ToList();
            var col = new StackPanel { Spacing = Look.Sm };
            if (sp.Count == 0) col.Children.Add(Empty("No Speechify voices made yet" + (allBooks ? "." : " for this book.")));
            foreach (var t in sp)
                col.Children.Add(ModelRow(t.Model, true,
                    [("Characters", t.Characters), ("Seconds of audio", (long)(t.AudioSeconds ?? 0)), ("Requests", t.Requests)], null));
            col.Children.Add(Ui.Caption("Characters are what Speechify billed. Your plan’s allowance is on the Speechify API platform.", TextWrapping.Wrap));
            panel.Children.Add(Section("Speechify", col));
        }

        if (days.Count > 0)
        {
            // One color per model, the same in every chart, with one legend above them.
            var colors = days.Select(Label).Distinct().Select((m, i) => (m, Palette[i % Palette.Length])).ToDictionary(x => x.m, x => x.Item2);
            var legend = new WrapPanel();
            foreach (var (model, hex) in colors)
                legend.Children.Add(Ui.Row(Look.Xs, Ui.Dot(hex), Ui.Caption(model)).Also(r => r.Margin = new Thickness(0, 0, Look.Md, 0)));
            panel.Children.Add(legend);
            // Tokens and characters on separate charts: a day of Claude's millions of tokens
            // would flatten the voices' characters to nothing.
            var tokenDays = days.Where(d => d.Service == CloudService.Claude).ToList();
            var voiceDays = days.Where(d => d.Service != CloudService.Claude).ToList();
            if (tokenDays.Count > 0)
            {
                panel.Children.Add(Ui.Headline("Claude by day"));
                panel.Children.Add(Chart(tokenDays, colors, r => r.TotalTokens, "Tokens", 130, money: false));
            }
            if (voiceDays.Count > 0)
            {
                panel.Children.Add(Ui.Headline("Voices by day"));
                panel.Children.Add(Chart(voiceDays, colors, r => r.Characters, "Characters spoken", 130, money: false));
            }
            var costDays = days.Where(d => d.Cost is not null).ToList();
            if (costDays.Count > 0)
            {
                panel.Children.Add(Ui.Headline("Cost by day"));
                panel.Children.Add(Chart(costDays, colors, r => r.Cost ?? 0, "Dollars", 140, money: true));
            }
        }
        Content = panel;
    }

    void LoadCredits()
    {
        if (creditsAsked || AppPaths.IsTestRun || CloudInUse.ElevenLabsKey() is not { } key) return;
        creditsAsked = true;
        _ = Task.Run(async () =>
        {
            try { credits = await ElevenLabsCredits.Read(key); }
            catch (Exception e) { creditsProblem = $"Couldn't read your ElevenLabs credits: {e.Message}"; }
            Avalonia.Threading.Dispatcher.UIThread.Post(Build);
        });
    }

    static string Label(CloudUsageRecord r) => r.Service switch
    {
        CloudService.Claude => ClaudeAccount.Shared.NameOf(r.Model),
        CloudService.Elevenlabs => $"ElevenLabs ({r.Model})",
        CloudService.Speechify => $"Speechify ({r.Model})",
        _ => $"Gemini ({r.Model})",
    };

    /// Bars by day, stacked by model, in the app's colors for the light or dark look.
    static Control Chart(List<CloudUsageRecord> days, Dictionary<string, string> colors, Func<CloudUsageRecord, double> value, string axis, double height, bool money)
    {
        var plot = new AvaPlot { Height = height };
        var plt = plot.Plot;
        var dayList = days.Select(d => d.Day).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var models = days.Select(Label).Distinct().ToList();
        var stack = new double[dayList.Count];
        var bars = new List<ScottPlot.Bar>();
        for (int m = 0; m < models.Count; m++)
        {
            var color = ScottPlot.Color.FromHex(colors[models[m]]);
            foreach (var r in days.Where(d => Label(d) == models[m]))
            {
                int x = dayList.IndexOf(r.Day);
                double v = value(r);
                bars.Add(new ScottPlot.Bar { Position = x, ValueBase = stack[x], Value = stack[x] + v, FillColor = color, LineWidth = 0 });
                stack[x] += v;
            }
        }
        plt.Add.Bars(bars);
        var labels = dayList.Select(d => DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("MMM d", Us) : d).ToArray();
        plt.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(Enumerable.Range(0, dayList.Count).Select(i => (double)i).ToArray(), labels);
        if (money) plt.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = v => v.ToString("$0.##", Us) };
        plt.Axes.Left.Label.Text = axis;
        plt.Axes.Left.Label.FontSize = 12;
        plt.Axes.Left.TickLabelStyle.FontSize = 11;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plt.Axes.Margins(bottom: 0);
        plt.Legend.IsVisible = false;
        var ink = ScottPlot.Color.FromHex(Look.IsDark ? "#ECECEE" : "#1D1D1F");
        var paper = ScottPlot.Color.FromHex(Look.IsDark ? "#1E1E20" : "#FFFFFF");
        plt.FigureBackground.Color = paper;
        plt.DataBackground.Color = paper;
        plt.Axes.Color(ink);
        plt.Grid.MajorLineColor = ink.WithAlpha(.12);
        plot.UserInputProcessor.Disable();
        plot.Refresh();
        return plot;
    }

    static Control Section(string title, Control content) => new Border
    {
        Background = Look.Brush(Look.Chrome),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(Look.Md),
        Child = Ui.Column(Look.Xs, Ui.Headline(title), content),
    };

    static TextBlock Empty(string text) => Ui.Text(text, 13, brush: Look.Brush(Look.SecondaryInk));

    static Control ModelRow(string name, bool current, (string Label, long Value)[] lines, string? cost)
    {
        var top = new DockPanel();
        if (cost is not null)
        {
            var c = Ui.Text(cost, 13, FontWeight.SemiBold);
            DockPanel.SetDock(c, Dock.Right);
            top.Children.Add(c);
        }
        var nameRow = Ui.Row(Look.Sm, Ui.Text(name, 13, FontWeight.Medium));
        if (current) nameRow.Children.Add(Ui.Caption("In use"));
        top.Children.Add(nameRow);
        var numbers = Ui.Row(Look.Lg);
        foreach (var (label, value) in lines)
            numbers.Children.Add(Ui.Column(0, Ui.Text(value.ToString("N0", Us), 13), Ui.Caption(label)));
        return Ui.Column(2, top, numbers);
    }
}
