using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using InnerVoice.Core.AI;

namespace InnerVoice.App.AI;

/// The narrator's Claude setup: the API key (Windows Credential Manager), their yes to the
/// cloud warning, and the models the key can use. The Mac's ClaudeAccount. Test runs (and a
/// developer's run on the Mac) keep the key in memory only.
public sealed class ClaudeAccount
{
    public static ClaudeAccount Shared { get; } = new();
    public event Action? Changed;

    const string CredentialTarget = "InnerVoice/anthropic";
    string? key;
    bool keyRead;

    ClaudeAccount() { }

    /// Test runs only: the HTTP client underneath, so checks can answer with a fake Anthropic.
    /// Ignored outside test runs.
    public static HttpClient? TestHttp { get; set; }
    static HttpClient? Http => AppPaths.IsTestRun ? TestHttp : null;

    /// Saved models, or the built-in list until the key has listed them.
    public IReadOnlyList<ClaudeModelInfo> Models =>
        AppSettings.Current.ClaudeModels is { Count: > 0 } saved ? saved : Claude.BuiltIn;

    public string NameOf(string id) => Models.FirstOrDefault(m => m.Id == id)?.Name
        ?? Claude.BuiltIn.FirstOrDefault(m => m.Id == id)?.Name ?? id;

    public bool Consented => AppSettings.Current.ClaudeConsent is not null;

    /// Read once and kept in memory: views ask through every redraw.
    public string? ApiKey
    {
        get
        {
            if (!keyRead)
            {
                keyRead = true;
                if (StoresInCredentialManager) key = CredentialStore.Read(CredentialTarget);
            }
            return key;
        }
    }

    public bool IsSetUp => !string.IsNullOrWhiteSpace(ApiKey) && Consented;

    static bool StoresInCredentialManager => OperatingSystem.IsWindows() && !AppPaths.IsTestRun;

    /// Saves the key (empty deletes it) and the narrator's yes.
    public void Save(string newKey, bool consent)
    {
        newKey = newKey.Trim();
        key = newKey.Length == 0 ? null : newKey;
        keyRead = true;
        if (StoresInCredentialManager)
        {
            if (key is null) CredentialStore.Delete(CredentialTarget);
            else CredentialStore.Write(CredentialTarget, key);
        }
        AppSettings.Update(s => s.ClaudeConsent = consent ? DateTime.UtcNow : null);
        Notify();
    }

    /// Forgets the key and the yes; nothing more is sent. Books keep their own yes.
    public void Disconnect()
    {
        Save("", consent: false);
        if (ModelManager.Shared.Selection.StartsWith(Claude.SelectionPrefix)) ModelManager.Shared.Selection = "";
    }

    /// Asks Anthropic which models the key can use (no book text is sent).
    public async Task RefreshModels(CancellationToken ct = default)
    {
        if (ApiKey is not { Length: > 0 } k) throw new ClaudeException(ClaudeException.NotSetUp);
        var found = await Claude.ListModels(k, Http, ct);
        if (found.Count == 0) return;
        await Dispatcher.UIThread.InvokeAsync(() => AppSettings.Update(s => s.ClaudeModels = found));
        Notify();
    }

    /// Claude for this book, or why not. `bookAllowed` is the book's `cloudTextAllowed`.
    public ClaudeTextModel Model(string modelID, bool bookAllowed) =>
        new(ApiKey, Consented, bookAllowed, modelID, NameOf(modelID), Http);

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());
}

/// Generic credentials in Windows Credential Manager (advapi32), stored for this user only.
static class CredentialStore
{
    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    public static string? Read(string target)
    {
        try
        {
            if (!CredRead(target, CredTypeGeneric, 0, out var ptr)) return null;
            try
            {
                var c = Marshal.PtrToStructure<Credential>(ptr);
                if (c.CredentialBlobSize == 0 || c.CredentialBlob == IntPtr.Zero) return null;
                var bytes = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally { CredFree(ptr); }
        }
        catch { return null; }
    }

    public static void Write(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "anthropic",
            };
            if (!CredWrite(ref c, 0)) throw new InvalidOperationException("Windows couldn't save the API key in Credential Manager.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete(string target)
    {
        try { CredDelete(target, CredTypeGeneric, 0); } catch { }
    }
}
