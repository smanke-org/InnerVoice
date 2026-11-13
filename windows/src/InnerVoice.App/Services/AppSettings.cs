using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerVoice.App;

public enum Appearance { System, Light, Dark }
public enum LineSpacing { Single, Double }
public enum MarginNames { Initials, FirstName, FullName }
public enum NextKey { Space, Down, PageDown, Return }

/// The app's own settings (not the book's): one JSON file in AppPaths.Support, like the
/// Mac's UserDefaults. Test runs never save.
public sealed class AppSettings
{
    public static AppSettings Current { get; private set; } = Load();
    public static event Action? Changed;

    public Appearance Appearance { get; set; } = Appearance.System;
    public double ReadingFontSize { get; set; } = 16;
    public bool ReadingSerif { get; set; } = true;
    public LineSpacing LineSpacing { get; set; } = LineSpacing.Single;
    public MarginNames MarginNames { get; set; } = MarginNames.Initials;
    /// The Teleprompter's text size and the key that moves on (named for its old page, Record).
    public double RecordFontSize { get; set; } = 30;
    public NextKey RecordNextKey { get; set; } = NextKey.Space;
    /// The paragraph each book's Teleprompter was last on, by the project file's full path.
    public Dictionary<string, Guid> TeleprompterPlaces { get; set; } = new();
    public bool StepsHidden { get; set; }
    /// Paragraphs shown before and after each line on the Review page (0–5).
    public int ReviewContext { get; set; } = 2;
    public string? ModelID { get; set; }
    public string? ModelsFolder { get; set; }
    /// When the narrator agreed that book text sent to Claude goes to Anthropic (Set Up Claude).
    /// The key itself is in Windows Credential Manager, never here.
    public DateTime? ClaudeConsent { get; set; }
    /// The Claude models the key can use, from Anthropic's model list (no book text is sent).
    public List<InnerVoice.Core.AI.ClaudeModelInfo>? ClaudeModels { get; set; }
    /// The narrator checked "Don't show the estimate again" before a Claude run.
    public bool SkipClaudeCostQuestion { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;

    public const double MinReadingSize = 12, MaxReadingSize = 26;

    static string FilePath => Path.Combine(AppPaths.Support, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    static AppSettings Load()
    {
        if (AppPaths.IsTestRun) return new AppSettings();
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    /// Changes settings and saves them.
    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke();
        Save();
    }

    /// Changes settings and saves them without redrawing anything, for values only one place
    /// reads (the Teleprompter's place in each book).
    public static void UpdateQuietly(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }

    static void Save()
    {
        if (AppPaths.IsTestRun) return;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json)); } catch { }
    }

    public static void AddRecent(string path)
    {
        Update(s =>
        {
            s.RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            s.RecentProjects.Insert(0, path);
            if (s.RecentProjects.Count > 12) s.RecentProjects.RemoveRange(12, s.RecentProjects.Count - 12);
        });
    }
}
