using System.Runtime.InteropServices;

namespace MaxPlanner.Desktop;

internal static partial class NativeMethods
{
    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;
    private const int SwRestore = 9;

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint windowHandle);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint windowHandle, int command);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint windowHandle);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);

    /// <summary>Shows a native error box; used only before XAML has started.</summary>
    internal static void ShowError(string caption, string message) =>
        _ = MessageBox(0, message, caption, MbOk | MbIconError);

    /// <summary>Restores a minimized window and brings it to the foreground.</summary>
    internal static void BringToFront(nint windowHandle)
    {
        if (IsIconic(windowHandle))
        {
            _ = ShowWindow(windowHandle, SwRestore);
        }

        _ = SetForegroundWindow(windowHandle);
    }
}
