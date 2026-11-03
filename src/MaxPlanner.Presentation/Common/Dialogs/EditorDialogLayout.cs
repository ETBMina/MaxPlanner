using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// Applies <see cref="EditorDialogSizing"/> to an editor dialog: fixes its content width from the window,
/// switches to full size in small or short windows, and re-applies when the window is resized. Long content
/// wraps or scrolls inside; it never widens the dialog.
/// </summary>
public static class EditorDialogLayout
{
    /// <summary>
    /// Call once after InitializeComponent. <paramref name="onLayout"/> runs after each resize with the
    /// window size, for dialogs that reflow their fields (two columns only at L).
    /// </summary>
    public static void Attach(ContentDialog dialog, Action<Windows.Foundation.Size>? onLayout = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // The template reads these when it is applied (on load), so set them before then.
        dialog.Resources["ContentDialogMaxWidth"] = EditorDialogSizing.LargeWidth;
        dialog.Resources["ContentDialogMinWidth"] = EditorDialogSizing.MinimumWidth;

        XamlRoot? attachedRoot = null;

        void Apply()
        {
            if (dialog.XamlRoot is not { } root)
            {
                return;
            }

            if (!ReferenceEquals(attachedRoot, root))
            {
                Detach();
                root.Changed += OnRootChanged;
                attachedRoot = root;
            }

            var size = root.Size;
            dialog.FullSizeDesired = EditorDialogSizing.IsFullSize(size.Width, size.Height);
            if (dialog.Content is FrameworkElement content)
            {
                content.Width = EditorDialogSizing.ContentWidth(size.Width);
            }

            onLayout?.Invoke(size);
        }

        void Detach()
        {
            if (attachedRoot is not null)
            {
                attachedRoot.Changed -= OnRootChanged;
                attachedRoot = null;
            }
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Apply();

        dialog.Loaded += (_, _) => Apply();
        dialog.Unloaded += (_, _) => Detach();
    }
}
