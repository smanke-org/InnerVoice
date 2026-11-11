namespace InnerVoice.App;

/// Where InnerVoice keeps its own files. Test runs (INNERVOICE_TEST) keep everything in
/// memory or in INNERVOICE_SUPPORT_DIR, never in the narrator's settings.
public static class AppPaths
{
    public static bool IsTestRun => Environment.GetEnvironmentVariable("INNERVOICE_TEST") is { Length: > 0 };

    public static string Support
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("INNERVOICE_SUPPORT_DIR");
            var dir = !string.IsNullOrEmpty(custom) ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerVoice");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Models => Path.Combine(Support, "Models");
}
