// The hardware check for the local voices: what this PC has (graphics card, its own video
// memory, memory, processor), whether Pocket TTS or Orpheus will be usable on it, and the
// plain warning shown before a download or the first use on a weak PC.

using System.Runtime.InteropServices;

namespace InnerVoice.Core.Speech.Neural;

/// What this PC has, as far as the voice models care.
public sealed record VoiceHardware(
    int MemoryGB,
    /// The DirectX 12 graphics card with the most video memory of its own; null: none.
    string? GpuName,
    double GpuMemoryGB,
    /// Its DXGI adapter index, for DirectML.
    int GpuIndex,
    string CpuName,
    int CpuThreads,
    string Architecture)
{
    public bool HasGpu => GpuName is not null;

    public override string ToString() =>
        $"{(HasGpu ? $"{GpuName} ({GpuMemoryGB:0.#} GB video memory)" : "No DirectX 12 graphics card")}, {MemoryGB} GB memory, {CpuName} ({CpuThreads} threads, {Architecture})";
}

public enum VoiceFit
{
    /// A DirectX 12 card with enough video memory and enough memory: lines come about as fast as they play, or faster.
    Good,
    /// It works, but a line takes longer to make than to hear (the processor, or a small card).
    Slow,
    /// Too little memory: the model may not load, or the computer slows to a crawl.
    TooWeak,
}

public static class VoiceHardwareCheck
{
    /// The specs Settings › Voices recommends.
    public const double RecommendedGpuGB = 8;
    /// DirectML is used only on a card with this much video memory of its own; integrated
    /// graphics share the PC's memory and are often no faster than the processor.
    public const double MinimumGpuGB = 2;

    /// The voices run on the graphics card here.
    public static bool UsesGpu(VoiceHardware hw) => hw.HasGpu && hw.GpuMemoryGB >= MinimumGpuGB;
    public const int RecommendedMemoryGB = 16;

    public const string Recommended =
        "Pocket TTS is small (about 2 GB of memory while it speaks) and runs well on the processor of most PCs with 8 GB of memory. For Orpheus, the best experience: a DirectX 12 graphics card with 8 GB or more of video memory (NVIDIA GeForce RTX 3060 or AMD Radeon RX 6700 XT class and up) and 16 GB of memory.";

    /// Measured on a Mac's processor (Apple M5 Max, ONNX Runtime CPU, 2026-11-13; see
    /// docs/WINDOWS-PLAN.md): seconds of speech made per second. A PC's processor is likely
    /// slower; these need checking on real PCs, with and without a graphics card.
    public static double CpuSpeed(VoiceEngine e) => e == VoiceEngine.Orpheus ? 0.13 : 6;

    public static VoiceFit Fit(VoiceHardware hw, VoiceEngine engine)
    {
        var pack = VoicePacks.For(engine);
        int minimum = pack?.MinimumMemoryGB ?? 8, recommended = pack?.RecommendedMemoryGB ?? RecommendedMemoryGB;
        double card = pack?.RecommendedGpuGB ?? RecommendedGpuGB;
        if (hw.MemoryGB < minimum) return VoiceFit.TooWeak;
        if (card > 0 && (!hw.HasGpu || hw.GpuMemoryGB < card)) return VoiceFit.Slow;
        if (hw.MemoryGB < recommended) return VoiceFit.Slow;
        return VoiceFit.Good;
    }

    /// Why the engine may be unusably slow on this PC, in plain words; null when it's fine.
    public static string? Warning(VoiceHardware hw, VoiceEngine engine)
    {
        var fit = Fit(hw, engine);
        if (fit == VoiceFit.Good) return null;
        var name = engine.Title();
        var pack = VoicePacks.For(engine);
        double card = pack?.RecommendedGpuGB ?? RecommendedGpuGB;
        string why;
        if (fit == VoiceFit.TooWeak)
            why = $"This PC has {hw.MemoryGB} GB of memory. {name} needs at least {pack?.MinimumMemoryGB ?? 8} GB free for itself, so it may not load at all, and everything else on the PC slows down while it runs.";
        else if (card > 0 && !hw.HasGpu)
            why = $"This PC has no DirectX 12 graphics card, so {name} runs on the processor: {Pace(engine)}.";
        else if (card > 0 && !UsesGpu(hw))
            why = $"The graphics card ({hw.GpuName}) has little or no video memory of its own, so {name} runs on the processor: {Pace(engine)}.";
        else if (card > 0 && hw.GpuMemoryGB < card)
            why = $"The graphics card ({hw.GpuName}) has {hw.GpuMemoryGB:0.#} GB of video memory; {name} works best with {card:0} GB or more. Expect lines to come slower than they play.";
        else
            why = $"This PC has {hw.MemoryGB} GB of memory; {name} works best with {pack?.RecommendedMemoryGB ?? RecommendedMemoryGB} GB or more. Expect lines to come slower than they play.";
        return why + " Windows Voices are fast on any PC, and ElevenLabs runs in the cloud.";
    }

    static string Pace(VoiceEngine e)
    {
        double speed = CpuSpeed(e);
        return speed >= 0.8
            ? "expect each line to take about as long to make as to hear, or longer on a slower processor"
            : $"expect each line to take {Math.Round(1 / speed)} times longer or more to make than to hear";
    }

    // MARK: Detecting

    /// Reads this PC's hardware in this process, graphics card included. The app asks
    /// `Detect(gpu)` with the card from a separate process (`ProbeGpu`), because a graphics
    /// driver that crashes when asked would take the app down with it.
    public static VoiceHardware Detect() => Detect(OperatingSystem.IsWindows() ? Dxgi.BestDirectX12Adapter() : null);

    /// Memory and processor read here; the graphics card as given (null: none, or unknown).
    public static VoiceHardware Detect(GpuInfo? gpu)
    {
        int memory = MemoryGB();
        string cpu = CpuName();
        string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return new VoiceHardware(memory, gpu?.Name, gpu?.MemoryGB ?? 0, gpu?.Index ?? 0, cpu, Environment.ProcessorCount, arch);
    }

    /// The graphics card DirectML would use, as one line for a child process to print:
    /// "none" or "<index>\t<GB>\t<name>". Windows only.
    public static string ProbeGpu() => OperatingSystem.IsWindows() && Dxgi.BestDirectX12Adapter() is { } g
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{g.Index}\t{g.MemoryGB:0.###}\t{g.Name.Replace('\t', ' ').Replace('\n', ' ')}")
        : "none";

    /// Reads `ProbeGpu`'s line back; null for "none" or anything unexpected.
    public static GpuInfo? ParseGpu(string? line)
    {
        var parts = line?.Trim().Split('\t', 3);
        if (parts is not { Length: 3 } || !int.TryParse(parts[0], out var index) || index < 0
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) || gb < 0
            || parts[2].Trim().Length == 0)
            return null;
        return new GpuInfo(index, parts[2].Trim(), gb);
    }

    static int MemoryGB()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status)) return (int)Math.Round(status.TotalPhys / 1_073_741_824.0);
        }
        return (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_073_741_824.0);
    }

    static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string s)
                    return s.Trim();
            }
            catch (Exception) { }
        }
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM processor" : "Processor";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

/// A DirectX 12 graphics card: its DXGI adapter index, name and dedicated video memory.
public sealed record GpuInfo(int Index, string Name, double MemoryGB);

/// The graphics cards DirectX knows (DXGI), and which can run DirectX 12: the ones DirectML uses.
static class Dxgi
{
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    // DXGI_ADAPTER_DESC1, blittable: GetDesc1 writes it through a raw pointer, so it must have
    // exactly the native layout (a marshaled string field here let it write 256 bytes of name
    // over the stack and crash the app).
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    [DllImport("d3d12.dll")]
    static extern int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, in Guid riid, nint device);

    // COM by vtable: IDXGIFactory1::EnumAdapters1 is slot 12, IDXGIAdapter1::GetDesc1 slot 10, Release slot 2.
    public static unsafe GpuInfo? BestDirectX12Adapter()
    {
        try
        {
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0 || factory == 0) return null;
            var vtable = *(nint**)factory;
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtable[12];
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            GpuInfo? best = null;
            for (uint i = 0; ; i++)
            {
                nint adapter;
                if (enumAdapters(factory, i, &adapter) < 0) break;
                var avt = *(nint**)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)avt[10];
                AdapterDesc1 desc = default;
                bool ok = getDesc1(adapter, &desc) >= 0;
                // D3D12CreateDevice with no output pointer only asks whether the card can (S_FALSE).
                bool dx12 = ok && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 && D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_ID3D12Device, 0) >= 0;
                if (dx12)
                {
                    double gb = (ulong)desc.DedicatedVideoMemory / 1_073_741_824.0;
                    var name = new string(desc.Description, 0, 128).TrimEnd('\0').Trim();
                    if (best is null || gb > best.MemoryGB) best = new GpuInfo((int)i, name.Length > 0 ? name : "Graphics card", gb);
                }
                ((delegate* unmanaged[Stdcall]<nint, uint>)avt[2])(adapter);
            }
            release(factory);
            return best;
        }
        catch (Exception) { return null; }
    }
}
