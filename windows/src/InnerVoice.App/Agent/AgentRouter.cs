using Avalonia;
using Avalonia.Threading;
using InnerVoice.Agent;
using InnerVoice.App.UI;

namespace InnerVoice.App.Agent;

/// Runs requests from the pipe inside the app, on the UI thread (the Mac's AgentRouter).
///   {"command": "lines assign", "args": {"ids": "3f2a9c1e", "to": "Mara"}}   one command
///   {"activate": ["C:\Books\Harbor.innervoice", "innervoice://open/cast"]}   a second copy of
///       the app, or `innervoice <file>`, hands over what it was asked to open
public static class AgentRouter
{
    public static async Task<string> Handle(string request)
    {
        JObject reply;
        try
        {
            if (Json.Parse(request) is not JObject o) throw AgentError.Usage("bad request");
            if (o.TryGetValue("activate", out var items))
            {
                reply = new JObject { ["ok"] = true, ["result"] = await Activate((items as IEnumerable<object?> ?? []).OfType<string>().ToList()) };
            }
            else
            {
                if (!o.TryGetValue("command", out var c) || c is not string name) throw AgentError.Usage("bad request");
                var args = o.TryGetValue("args", out var a) && a is JObject ao
                    ? ao.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value as string ?? Json.Write(kv.Value).Trim('"'))
                    : [];
                reply = new JObject { ["ok"] = true, ["result"] = await Run(name, args) };
            }
        }
        catch (AgentError e) { reply = e.Json; }
        catch (Exception e) { reply = AgentError.Invalid(e.Message).Json; }
        return Json.Write(reply);
    }

    public static Task<object?> Run(string name, Dictionary<string, string> args)
    {
        if (AgentCommands.Named(name) is null || !AgentHandlers.All.TryGetValue(name, out var handler))
            throw AgentError.Usage($"unknown command \"{name}\". Run `innervoice help`");
        return handler(new Args(args));
    }

    /// Opens what another copy of the app (or `innervoice <file>`) was given, then brings
    /// InnerVoice to the front.
    static async Task<object?> Activate(List<string> items)
    {
        object? result = new JObject();
        foreach (var item in items)
        {
            if (AgentUrl.IsLink(item)) result = await OpenLink(item);
            else App.Current.OpenPath(item);
        }
        if (!items.Any(AgentUrl.IsLink)) App.Current.BringToFront();
        return result;
    }

    /// An innervoice:// link: runs its command (only the ones that show things) and brings the
    /// window to the front. A link that can't be followed says why, in a window.
    public static async Task<object?> OpenLink(string url)
    {
        try
        {
            var request = AgentUrl.Parse(url);
            object? result = new JObject();
            if (request.Command is { } name) result = await Run(name, request.Args);
            App.Current.BringToFront();
            return result;
        }
        catch (AgentError e)
        {
            App.Current.BringToFront();
            var owner = (Visual?)App.Current.FrontProject ?? App.Current.StartWindow;
            if (owner is not null) _ = Dialogs.Alert(owner, "Couldn't Follow the Link", $"{url}: {e.Message}.");
            throw;
        }
    }
}

/// The pipe the CLI, the MCP server, links and a second copy of the app talk to.
public static class AgentServer
{
    static CancellationTokenSource? stop;

    /// The person's own copy and agent test copies (APP_STORE) listen; unit tests and snapshot
    /// runs don't, unless a test starts it.
    public static bool ShouldListen => AgentPipe.IsTestRun || !AppPaths.IsTestRun;

    /// INNERVOICE_DEMO=1 on a test copy opens the sample book at launch.
    public static bool OpensDemo => AgentPipe.IsTestRun && Environment.GetEnvironmentVariable("INNERVOICE_DEMO") == "1";

    public static string? ListeningOn { get; private set; }

    public static void Start(string? name = null)
    {
        Stop();
        stop = new CancellationTokenSource();
        var token = stop.Token;
        ListeningOn = name ?? AgentPipe.Name;
        var pipe = ListeningOn;
        _ = Task.Run(() => AgentPipe.Listen(request => Dispatcher.UIThread.InvokeAsync(() => AgentRouter.Handle(request)), token, pipe));
    }

    public static void Stop()
    {
        stop?.Cancel();
        stop = null;
        ListeningOn = null;
    }
}
