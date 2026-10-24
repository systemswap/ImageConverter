using System.Diagnostics;
using System.Runtime.InteropServices;
using ImageMagick;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Controls how much of the computer the processing may use (Settings › Performance), so that the
/// computer stays responsive.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Low priority</b> (default): other programs take precedence; if the computer is otherwise idle,
/// processing still runs at full speed.</item>
/// <item><b>Upper limit</b> in percent of the CPU: applies to the program's own parallel loops
/// (<see cref="Options"/>), the AI runtime (<see cref="OnnxThreads"/>) and ImageMagick. Measured
/// (16-core desktop processor, PiSA-SR): with 8 instead of 16 cores only about a quarter slower.</item>
/// </list>
/// </remarks>
public static class ComputeLimits
{
    /// <summary>Upper limit of CPU usage in percent (10 … 100).</summary>
    public static int Percent { get; private set; } = 100;

    /// <summary>Options for every parallel loop of the program; they limit the number of concurrent threads.</summary>
    /// <remarks>Without a limit, <see cref="ParallelOptions.MaxDegreeOfParallelism"/> is -1; otherwise it is the share of the logical processors.</remarks>
    public static ParallelOptions Options { get; } = new();

    /// <summary>Number of logical processors (twice the number of cores with hyper-threading).</summary>
    public static int LogicalProcessors => Environment.ProcessorCount;

    /// <summary>Number of physical cores (without hyper-threading siblings); the AI runtime counts the same way.</summary>
    public static int PhysicalCores { get; } = CountPhysicalCores();

    /// <summary>Number of physical cores allowed by the upper limit (at least one).</summary>
    public static int AllowedCores => Math.Max(1, (int)Math.Round(PhysicalCores * Percent / 100.0));

    /// <summary>
    /// Thread count for the AI runtime: <see langword="null"/> for its default (all physical cores),
    /// otherwise <see cref="AllowedCores"/>.
    /// </summary>
    public static int? OnnxThreads => Percent >= 100 ? null : AllowedCores;

    /// <summary>Raised after the upper limit has changed (the AI then reopens its models).</summary>
    public static event EventHandler? Changed;

    /// <summary>Sets the upper limit (at start-up and from the settings).</summary>
    /// <param name="percent">Limit in percent of the CPU; clamped to 10 … 100.</param>
    /// <remarks>
    /// ImageMagick is configured only by the first successful call; see <see cref="MagickNeedsRestart"/>.
    /// </remarks>
    public static void SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 10, 100);
        var threads = percent >= 100 ? -1 : Math.Max(1, (int)Math.Round(LogicalProcessors * percent / 100.0));
        var changed = percent != Percent;
        Percent = percent;
        Options.MaxDegreeOfParallelism = threads;
        // Configure ImageMagick only once, at start-up, before it processes anything: it allocates per-thread buffers
        // according to the limit at the start of an operation. If the limit is raised meanwhile, the additional threads
        // write past the end (observed: "free(): invalid pointer", crash; see docs/FEHLERGEDAECHTNIS.md, No. 77).
        // Later changes apply to ImageMagick from the next start.
        if (MagickPercent is null)
        {
            try
            {
                ResourceLimits.Thread = (ulong)(threads > 0 ? threads : LogicalProcessors);
                MagickPercent = percent;
            }
            catch (Exception e) when (e is TypeInitializationException or DllNotFoundException)
            {
                // ImageMagick is missing (tests without the native library).
            }
        }
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Limit in percent that ImageMagick was configured with at start-up; <see langword="null"/> if not yet configured.</summary>
    public static int? MagickPercent { get; private set; }

    /// <summary>Whether the limit was changed after start-up; for ImageMagick it applies only from the next start.</summary>
    public static bool MagickNeedsRestart => MagickPercent is { } m && m != Percent;

    /// <summary>Whether the low priority is set.</summary>
    public static bool LowPriority { get; private set; }

    /// <summary>
    /// Lowers the priority of the whole program.
    /// </summary>
    /// <remarks>
    /// On Linux the nice value applies per thread: all existing threads are changed, and new threads
    /// inherit the value of the thread that creates them. Returning to normal priority is not possible on
    /// Linux without administrator rights, so switching it off takes effect there only after a restart.
    /// Failures are logged, not thrown.
    /// </remarks>
    public static void LowerPriority()
    {
        if (LowPriority) return;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
                    if (int.TryParse(Path.GetFileName(dir), out var tid))
                        _ = setpriority(0, (uint)tid, NiceValue);
            }
            else
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            LowPriority = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or PlatformNotSupportedException or EntryPointNotFoundException)
        {
            AppLog.Write("Niedrige Priorität ließ sich nicht setzen", e.Message);
        }
    }

    /// <summary>Restores normal priority on Windows (not possible on Linux, see <see cref="LowerPriority"/>).</summary>
    /// <returns>
    /// <see langword="true"/> if the priority is normal afterwards; <see langword="false"/> on Linux or if
    /// the change failed.
    /// </returns>
    public static bool TryNormalPriority()
    {
        if (!LowPriority) return true;
        if (OperatingSystem.IsLinux()) return false;
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
            LowPriority = false;
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Nice value on Linux: well behind everything else that is currently running.</summary>
    private const int NiceValue = 10;

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, uint who, int prio);

    // ------------------------------------------------------------ Counting cores

    private static int CountPhysicalCores()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return LinuxCores();
            if (OperatingSystem.IsWindows()) return WindowsCores();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
                                       or EntryPointNotFoundException or DllNotFoundException)
        {
            // fall back to the estimate below
        }
        return Math.Max(1, Environment.ProcessorCount);
    }

    /// <summary>Linux: number of distinct (package, core) pairs of the processors.</summary>
    private static int LinuxCores()
    {
        var cores = new HashSet<string>();
        foreach (var cpu in Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*"))
        {
            var topology = Path.Combine(cpu, "topology");
            var core = Path.Combine(topology, "core_id");
            if (!File.Exists(core)) continue;
            var package = Path.Combine(topology, "physical_package_id");
            cores.Add((File.Exists(package) ? File.ReadAllText(package).Trim() : "0") + ":" + File.ReadAllText(core).Trim());
        }
        return cores.Count > 0 ? cores.Count : Environment.ProcessorCount;
    }

    /// <summary>Windows: number of processor core entries returned by GetLogicalProcessorInformation.</summary>
    private static int WindowsCores()
    {
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return Environment.ProcessorCount;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length)) return Environment.ProcessorCount;
            var size = Marshal.SizeOf<SystemLogicalProcessorInformation>();
            var cores = 0;
            for (var offset = 0; offset + size <= length; offset += size)
                if (Marshal.PtrToStructure<SystemLogicalProcessorInformation>(buffer + offset).Relationship == 0) cores++;
            return cores > 0 ? cores : Environment.ProcessorCount;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemLogicalProcessorInformation
    {
        public UIntPtr ProcessorMask;
        public int Relationship;   // 0 = RelationProcessorCore
        public ulong Reserved1;
        public ulong Reserved2;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);
}
