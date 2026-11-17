using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace MaxPlanner.Presentation.Common;

/// <summary>Puts plain text on the clipboard (works unpackaged). Call on the UI thread.</summary>
public static class ClipboardText
{
    /// <summary>Copies <paramref name="text"/>; false when there was nothing to copy or the clipboard refused it.</summary>
    public static bool Set(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);

            // Keeps the text on the clipboard after the app closes.
            Clipboard.Flush();
            return true;
        }
        catch (COMException)
        {
            // Another process holds the clipboard open; the copy is lost, as it would be in any app. Nothing to undo.
            return false;
        }
    }
}
