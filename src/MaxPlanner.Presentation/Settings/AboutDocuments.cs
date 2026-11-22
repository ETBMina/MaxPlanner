using System.ComponentModel;
using System.Diagnostics;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// Opens the license text and the third-party notices that sit beside the EXE (ADR 0032). They are plain text, so
/// Notepad shows them whatever the user has associated with the file type (LICENSE has no extension). Only the fixed
/// paths from <see cref="AboutInfo"/> are passed, so no user content is ever opened or run.
/// </summary>
internal static class AboutDocuments
{
    /// <summary>Opens the file in Notepad (or the shell's default app without it); false when it is missing or cannot be opened.</summary>
    public static bool TryOpen(string? path)
    {
        if (!AboutInfo.CanOpenFile(path))
        {
            return false;
        }

        try
        {
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var start = new ProcessStartInfo(notepad) { UseShellExecute = false };
            start.ArgumentList.Add(path!);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            // No Notepad (a stripped-down Windows image): hand the fixed path to the shell instead of failing silently.
            return TryOpenWithShell(path!);
        }
    }

    private static bool TryOpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
