using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace InnerVoice.App.Agent;

/// innervoice:// links. The Store package declares them in its manifest (windows.protocol);
/// a copy run from a folder writes them for this user (HKCU\Software\Classes\innervoice) each
/// launch, so the link always starts the copy that ran last. Either way Windows starts
/// InnerVoice.exe with the link, and a running InnerVoice gets it through the pipe
/// (Program.Main hands it over).
public static class ProtocolRegistration
{
    public static void RegisterIfNeeded()
    {
        if (!OperatingSystem.IsWindows() || AppPaths.IsTestRun || IsPackaged) return;
        try { Register(Environment.ProcessPath!); }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine($"innervoice:// not registered: {e.Message}"); }
    }

    [SupportedOSPlatform("windows")]
    static void Register(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\innervoice");
        key.SetValue("", "URL:InnerVoice");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
    }

    /// Running from the Store package (it has a package identity).
    public static bool IsPackaged
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                uint length = 0;
                // APPMODEL_ERROR_NO_PACKAGE (15700): not packaged. ERROR_INSUFFICIENT_BUFFER: packaged.
                return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700;
            }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    [DllImport("kernel32.dll")]
    static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
