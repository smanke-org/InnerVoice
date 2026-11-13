using Avalonia;
using InnerVoice.Agent;
using InnerVoice.App.Agent;

namespace InnerVoice.App;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One InnerVoice per person (per test copy): a second start (a double-clicked project,
        // an innervoice:// link, the Start menu) hands what it was given to the running one.
        if (AgentServer.ShouldListen)
        {
            var handover = Handover(args);
            if (AgentPipe.Forward(handover)) return 0;
            // Another copy is still opening (not listening yet): wait for it rather than open a
            // second window (seen when a slow first start made the CLI start InnerVoice twice).
            firstCopy = AgentPipe.ClaimFirstCopy(out bool first);
            if (!first)
            {
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(500);
                    if (AgentPipe.Forward(handover)) return 0;
                }
            }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        GC.KeepAlive(firstCopy);
        return 0;
    }

    /// Kept for the app's lifetime (AgentPipe.ClaimFirstCopy).
    static Mutex? firstCopy;

    /// Files as full paths (the running copy has another working folder), links as they are.
    static List<string> Handover(string[] args) => args
        .Where(a => !a.StartsWith("--"))
        .Select(a => AgentUrl.IsLink(a) ? a : Path.GetFullPath(a))
        .ToList();

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
