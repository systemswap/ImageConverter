using System.ComponentModel;
using System.Diagnostics;
using ImageConverter.Core.Printing;
using ImageConverter.Services;

namespace ImageConverter.Tests;

/// <summary>
/// Calls to external programs: file manager and print service. If a program is missing or
/// hangs, ImageConverter must neither crash nor freeze.
/// </summary>
[Collection(nameof(SystemToolsTests))]
[CollectionDefinition(nameof(SystemToolsTests), DisableParallelization = true)]   // changes PATH
public class SystemToolsTests
{
    [Fact]
    public void OpenFolder_WithoutFileManager_ReportsInsteadOfCrashing()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows finds explorer.exe in the system folder even without PATH

        var path = Environment.GetEnvironmentVariable("PATH");
        var empty = Directory.CreateTempSubdirectory("ic-leer-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("PATH", empty);

            // Without handling, this exception would crash "Open folder":
            if (!OperatingSystem.IsWindows())
                Assert.Throws<Win32Exception>(() => Process.Start(new ProcessStartInfo("xdg-open", "/tmp") { UseShellExecute = false }));

            Assert.False(FolderOpener.TryOpen(empty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(empty);
        }
    }

    [Fact]
    public void PrintService_Hanging_TimesOutAndIsStopped()
    {
        if (OperatingSystem.IsWindows()) return;   // CUPS path; sleep exists only on Unix

        var clock = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => SystemPrinters.RunWithExit("sleep", ["30"], timeoutMs: 300));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"wartete {clock.Elapsed}");
    }
}
