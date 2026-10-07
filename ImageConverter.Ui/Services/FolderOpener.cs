using System;
using System.Diagnostics;

namespace ImageConverter.Services;

/// <summary>
/// Öffnet einen Ordner im Dateimanager des Systems (Explorer, Nautilus, Dolphin …).
/// Fehlt das Hilfsprogramm (z. B. xdg-open auf einem schlanken Linux), meldet es das,
/// statt das Programm abstürzen zu lassen.
/// </summary>
public static class FolderOpener
{
    /// <returns>false, wenn kein Dateimanager gestartet werden konnte.</returns>
    public static bool TryOpen(string folder)
    {
        foreach (var (file, args) in Candidates(folder))
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var _ = Process.Start(psi);
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // nicht vorhanden oder nicht startbar — nächsten Weg versuchen
            }
        }
        return false;
    }

    private static (string File, string[] Args)[] Candidates(string folder) =>
        OperatingSystem.IsWindows() ? [("explorer.exe", [folder])]
        : OperatingSystem.IsMacOS() ? [("open", [folder])]
        : [("xdg-open", [folder]), ("gio", ["open", folder])];
}
