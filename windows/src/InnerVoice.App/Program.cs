using Avalonia;
using InnerVoice.Agent;
using InnerVoice.App.Agent;

namespace InnerVoice.App;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The voices' hardware check asks for the graphics card in a process of its own (this
        // one, started again), so a driver that crashes when asked can't take the app down.
        if (args is [Speech.VoiceModels.ProbeArgument])
        {
            try { Console.Out.WriteLine(Core.Speech.Neural.VoiceHardwareCheck.ProbeGpu()); }
            catch (Exception) { Console.Out.WriteLine("none"); }
            Console.Out.Flush();
            return 0;
        }
        // One InnerVoice per person (per test copy): a second start (a double-clicked project,
        // an innervoice:// link, the Start menu) hands what it was given to the running one.
        if (AgentServer.ShouldListen && AgentPipe.Forward(Handover(args))) return 0;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

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
