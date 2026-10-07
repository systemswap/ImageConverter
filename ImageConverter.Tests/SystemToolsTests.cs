using System.ComponentModel;
using System.Diagnostics;
using ImageConverter.Core.Printing;
using ImageConverter.Services;

namespace ImageConverter.Tests;

/// <summary>
/// Aufrufe externer Programme: Dateimanager und Druckdienst. Fehlt ein Programm oder hängt
/// es, darf ImageConverter weder abstürzen noch einfrieren.
/// </summary>
[Collection(nameof(SystemToolsTests))]
[CollectionDefinition(nameof(SystemToolsTests), DisableParallelization = true)]   // ändert PATH
public class SystemToolsTests
{
    [Fact]
    public void OpenFolder_WithoutFileManager_ReportsInsteadOfCrashing()
    {
        if (OperatingSystem.IsWindows()) return;   // explorer.exe findet Windows auch ohne PATH im Systemordner

        var path = Environment.GetEnvironmentVariable("PATH");
        var empty = Directory.CreateTempSubdirectory("ic-leer-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("PATH", empty);

            // So stürzte „Ordner öffnen“ bis 0.4.20 ab:
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
        if (OperatingSystem.IsWindows()) return;   // CUPS-Weg; sleep gibt es nur unter Unix

        var clock = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => SystemPrinters.RunWithExit("sleep", ["30"], timeoutMs: 300));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"wartete {clock.Elapsed}");
    }
}
