namespace InnerVoice.App;

/// Where InnerVoice keeps its own files. Test runs (INNERVOICE_TEST) keep everything in
/// memory or in INNERVOICE_SUPPORT_DIR, never in the narrator's settings. A test copy for the
/// agent CLI (APP_STORE=<folder>, InnerVoice.Agent/AgentPipe.cs) is a test run too, and keeps
/// its files in that folder.
public static class AppPaths
{
    public static bool IsTestRun => Environment.GetEnvironmentVariable("INNERVOICE_TEST") is { Length: > 0 } || InnerVoice.Agent.AgentPipe.IsTestRun;

    public static string Support
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("INNERVOICE_SUPPORT_DIR") is { Length: > 0 } s ? s : InnerVoice.Agent.AgentPipe.Store;
            var dir = !string.IsNullOrEmpty(custom) ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerVoice");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Models => Environment.GetEnvironmentVariable("APP_MODELS") is { Length: > 0 } m ? m : Path.Combine(Support, "Models");
}
