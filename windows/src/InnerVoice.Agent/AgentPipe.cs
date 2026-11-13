// Ported from Sources/InnerVoice/Agent/AgentSocket.swift: one JSON line in, one JSON line
// out, over a named pipe instead of a Unix socket. The app listens; the CLI, the MCP server,
// `innervoice://` links and a second copy of the app (which hands its files to the first)
// connect.
//
// Test copies: APP_STORE=<folder> makes a copy that keeps its settings and models there, only
// opens projects in the temporary folder, and listens on a pipe of its own (named from the
// folder), so a test never reaches the person's own running InnerVoice. APP_AGENT_PIPE names
// the pipe directly. Unlike the Mac, Release builds honor them too: CI runs the tests on a
// Release build, and a test must never fall back to the real pipe.

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace InnerVoice.Agent;

public static class AgentPipe
{
    /// The test copy's folder (APP_STORE), or null for the person's own InnerVoice.
    public static string? Store => Environment.GetEnvironmentVariable("APP_STORE") is { Length: > 0 } s ? Path.GetFullPath(s) : null;

    /// A test copy (APP_STORE set): the CLI only ever starts test copies, which never open
    /// real projects.
    public static bool IsTestRun => Store is not null;

    /// The pipe's name: \\.\pipe\<Name> on Windows.
    public static string Name
    {
        get
        {
            if (Environment.GetEnvironmentVariable("APP_AGENT_PIPE") is { Length: > 0 } custom) return custom;
            if (Store is { } store)
            {
                // Short, so the socket path .NET uses for pipes on macOS and Linux stays under 104 bytes.
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(store.ToLowerInvariant()));
                return "InnerVoice.test." + Convert.ToHexString(hash)[..12].ToLowerInvariant();
            }
            var user = new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return "InnerVoice.Agent." + (user.Length > 0 ? user : "user");
        }
    }

    public static string Display => OperatingSystem.IsWindows() ? $@"\\.\pipe\{Name}" : Name;

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // MARK: The app's side

    /// Answers requests until `stop`, each with `handle` (which the app runs on its UI
    /// thread). Only this user's processes can connect.
    public static async Task Listen(Func<string, Task<string>> handle, CancellationToken stop, string? name = null)
    {
        name ??= Name;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e)
            {
                Trace.WriteLine($"AGENT could not listen on {name}: {e.Message}");
                return;
            }
            try { await server.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { await server.DisposeAsync(); return; }
            catch (IOException) { await server.DisposeAsync(); continue; }
            _ = Serve(server, handle);
        }
    }

    static async Task Serve(NamedPipeServerStream server, Func<string, Task<string>> handle)
    {
        await using (server)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var request = await ReadLine(server, timeout.Token);
                if (request is null) return;
                var reply = await handle(request);
                var bytes = Utf8.GetBytes(reply + "\n");
                await server.WriteAsync(bytes);
                await server.FlushAsync();
                if (OperatingSystem.IsWindows()) server.WaitForPipeDrain();
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    /// One line, read byte by byte so nothing past the newline is taken from the pipe.
    static async Task<string?> ReadLine(Stream stream, CancellationToken token)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, token);
            if (n == 0) break;
            if (one[0] == (byte)'\n') return Utf8.GetString(buffer.ToArray());
            buffer.WriteByte(one[0]);
            if (buffer.Length > 16 * 1024 * 1024) return null;
        }
        return buffer.Length > 0 ? Utf8.GetString(buffer.ToArray()) : null;
    }

    // MARK: The CLI's side

    /// Sends one request and returns the app's answer. When nothing answers and `launch` is
    /// true, starts InnerVoice and waits up to 10 seconds for it.
    public static string Send(string request, bool launch)
    {
        if (Exchange(request, connectMs: 500) is { } reply) return reply;
        if (!launch) throw AgentError.Unavailable("InnerVoice is not running. Open it, or leave out --no-launch");
#if DEBUG
        if (!IsTestRun) throw AgentError.Unavailable("InnerVoice is not running. A Debug build starts only test copies (set APP_STORE)");
#endif
        StartApp();
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(200);
            if (Exchange(request, connectMs: 100) is { } answer) return answer;
        }
        throw AgentError.Unavailable($"InnerVoice did not answer within 10 seconds on {Display}");
    }

    /// The answer, or null when no InnerVoice is listening.
    public static string? Exchange(string request, int connectMs, string? name = null, int answerSeconds = 120)
    {
        using var client = new NamedPipeClientStream(".", name ?? Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try { client.Connect(connectMs); }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return null; }
        try
        {
            var bytes = Utf8.GetBytes(request + "\n");
            client.Write(bytes);
            client.Flush();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(answerSeconds));
            return ReadLine(client, timeout.Token).GetAwaiter().GetResult()
                ?? throw AgentError.Invalid("the app sent no answer");
        }
        catch (OperationCanceledException) { throw AgentError.Invalid($"InnerVoice didn't answer within {answerSeconds} seconds"); }
        catch (IOException e) { throw AgentError.Invalid($"lost the connection to InnerVoice: {e.Message}"); }
    }

    /// A second copy of the app (a double-clicked project, an `innervoice://` link) hands its
    /// arguments to the running one. True when one took them.
    public static bool Forward(IEnumerable<string> arguments, string? name = null)
    {
        try
        {
            var reply = Exchange(Json.Write(new JObject { ["activate"] = arguments.ToList() }), connectMs: 300, name: name, answerSeconds: 10);
            return reply is not null && Json.Parse(reply) is JObject o && o.TryGetValue("ok", out var ok) && ok is true;
        }
        catch (AgentError) { return false; }
    }

    // MARK: Starting the app

    /// The app next to the CLI: the CLI lives in the app's cli\ folder. INNERVOICE_APP names it.
    public static string? AppPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("INNERVOICE_APP") is { Length: > 0 } custom) return File.Exists(custom) ? custom : null;
            var exe = OperatingSystem.IsWindows() ? "InnerVoice.exe" : "InnerVoice";
            var here = AppContext.BaseDirectory;
            foreach (var dir in new[] { Path.Combine(here, ".."), here })
            {
                var path = Path.GetFullPath(Path.Combine(dir, exe));
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }

    /// Starts InnerVoice in the background (--agent: it doesn't take the focus). A test copy
    /// gets this process's environment, APP_STORE and INNERVOICE_DEMO included.
    static void StartApp()
    {
        try
        {
            if (AppPath is { } path)
            {
                var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
                start.ArgumentList.Add("--agent");
                Process.Start(start);
            }
            else if (!IsTestRun && OperatingSystem.IsWindows())
            {
                // The Store copy, from elsewhere: its link starts it.
                Process.Start(new ProcessStartInfo("innervoice://") { UseShellExecute = true });
            }
            else
            {
                throw AgentError.Unavailable("could not find InnerVoice next to the CLI. Set INNERVOICE_APP to the app's path");
            }
        }
        catch (AgentError) { throw; }
        catch (Exception e) { throw AgentError.Unavailable($"could not start InnerVoice: {e.Message}"); }
    }
}
