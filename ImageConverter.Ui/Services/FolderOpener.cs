using System;
using System.Diagnostics;

namespace ImageConverter.Services;

/// <summary>
/// Opens a folder in the system file manager (Explorer, Nautilus, Dolphin …). If the helper
/// program is missing (e.g. xdg-open on a minimal Linux system), it reports this instead of
/// crashing the program.
/// </summary>
public static class FolderOpener
{
    /// <summary>Tries to open <paramref name="folder"/> in the file manager.</summary>
    /// <param name="folder">Path of the folder to open.</param>
    /// <returns><c>false</c> if no file manager could be started.</returns>
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
                // not present or not startable: try the next candidate
            }
        }
        return false;
    }

    private static (string File, string[] Args)[] Candidates(string folder) =>
        OperatingSystem.IsWindows() ? [("explorer.exe", [folder])]
        : OperatingSystem.IsMacOS() ? [("open", [folder])]
        : [("xdg-open", [folder]), ("gio", ["open", folder])];
}
