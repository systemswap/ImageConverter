using System.Diagnostics;
using System.Runtime.InteropServices;
using ImageMagick;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Wie viel vom Rechner die Berechnungen nehmen dürfen (Einstellungen › Leistung), damit er bedienbar bleibt:
/// <list type="bullet">
/// <item><b>Niedrige Priorität</b> (Vorgabe): andere Programme haben Vorrang; ist der Rechner sonst frei, wird trotzdem mit
/// voller Kraft gerechnet.</item>
/// <item><b>Obergrenze</b> in Prozent der CPU: wirkt auf die eigenen parallelen Schleifen (<see cref="Options"/>), die
/// KI-Laufzeit (<see cref="OnnxThreads"/>) und ImageMagick. Gemessen (Ryzen 9 7950X3D, PiSA-SR): mit 8 statt 16 Kernen
/// nur rund ein Viertel langsamer.</item>
/// </list>
/// </summary>
public static class ComputeLimits
{
    /// <summary>Höchstens so viel Prozent der CPU (10 … 100).</summary>
    public static int Percent { get; private set; } = 100;

    /// <summary>Für jede parallele Schleife des Programms — begrenzt die Zahl gleichzeitiger Stränge.</summary>
    public static ParallelOptions Options { get; } = new();

    /// <summary>Logische Prozessoren (mit Hyperthreading doppelt so viele wie Kerne).</summary>
    public static int LogicalProcessors => Environment.ProcessorCount;

    /// <summary>Echte Kerne (ohne Hyperthreading-Zwillinge); so zählt auch die KI-Laufzeit.</summary>
    public static int PhysicalCores { get; } = CountPhysicalCores();

    /// <summary>Erlaubte Kerne nach der Obergrenze (mindestens einer).</summary>
    public static int AllowedCores => Math.Max(1, (int)Math.Round(PhysicalCores * Percent / 100.0));

    /// <summary>Stränge der KI-Laufzeit: null = ihre Vorgabe (alle echten Kerne), sonst die erlaubten Kerne.</summary>
    public static int? OnnxThreads => Percent >= 100 ? null : AllowedCores;

    /// <summary>Nach einer Änderung der Obergrenze (die KI öffnet ihre Modelle dann neu).</summary>
    public static event EventHandler? Changed;

    /// <summary>Obergrenze setzen (beim Start und aus den Einstellungen).</summary>
    public static void SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 10, 100);
        var threads = percent >= 100 ? -1 : Math.Max(1, (int)Math.Round(LogicalProcessors * percent / 100.0));
        var changed = percent != Percent;
        Percent = percent;
        Options.MaxDegreeOfParallelism = threads;
        // ImageMagick nur einmal, beim Start, bevor es rechnet: Es legt Puffer je Strang nach der Grenze zu Beginn einer
        // Operation an — wird sie währenddessen erhöht, schreiben die zusätzlichen Stränge über das Ende hinaus
        // (gemessen: „free(): invalid pointer“, Absturz; siehe docs/FEHLERGEDAECHTNIS.md). Spätere Änderungen gelten für
        // ImageMagick ab dem nächsten Start.
        if (MagickPercent is null)
        {
            try
            {
                ResourceLimits.Thread = (ulong)(threads > 0 ? threads : LogicalProcessors);
                MagickPercent = percent;
            }
            catch (Exception e) when (e is TypeInitializationException or DllNotFoundException)
            {
                // ImageMagick fehlt (Tests ohne native Bibliothek)
            }
        }
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Grenze, mit der ImageMagick beim Start eingestellt wurde (null: noch nicht).</summary>
    public static int? MagickPercent { get; private set; }

    /// <summary>Die Grenze wurde nach dem Start geändert — für ImageMagick gilt sie erst beim nächsten Start.</summary>
    public static bool MagickNeedsRestart => MagickPercent is { } m && m != Percent;

    /// <summary>Ist die niedrige Priorität gesetzt?</summary>
    public static bool LowPriority { get; private set; }

    /// <summary>
    /// Ganzes Programm auf niedrige Priorität. Unter Linux gilt „nice“ je Strang: alle vorhandenen werden umgestellt, neue
    /// erben den Wert von dem Strang, der sie anlegt. Zurück auf normal geht unter Linux ohne Administratorrechte nicht
    /// — das Ausschalten wirkt dort erst nach einem Neustart.
    /// </summary>
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

    /// <summary>Windows: zurück auf normal (unter Linux nicht möglich, siehe <see cref="LowerPriority"/>).</summary>
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

    /// <summary>„nice“-Wert unter Linux: deutlich hinter allem, was der Benutzer gerade tut.</summary>
    private const int NiceValue = 10;

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, uint who, int prio);

    // ------------------------------------------------------------ Kerne zählen

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
            // unten: Schätzung
        }
        return Math.Max(1, Environment.ProcessorCount);
    }

    /// <summary>Linux: verschiedene (Paket, Kern)-Paare der Prozessoren.</summary>
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

    /// <summary>Windows: Einträge „Prozessorkern“ aus GetLogicalProcessorInformation.</summary>
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
