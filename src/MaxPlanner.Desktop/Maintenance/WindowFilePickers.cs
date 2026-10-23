using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace MaxPlanner.Desktop.Maintenance;

/// <summary>
/// The Windows App SDK pickers (<c>Microsoft.Windows.Storage.Pickers</c>), which work unpackaged when constructed with
/// the main window's ID (design §4.6). Use on the UI thread after <see cref="Attach"/>.
/// </summary>
internal sealed class WindowFilePickers : IFilePickerService
{
    private WindowId? _window;

    public void Attach(WindowId window) => _window = window;

    public async Task<string?> PickSaveFileAsync(string suggestedName, string typeLabel, string extension)
    {
        if (_window is not { } window)
        {
            return null;
        }

        var picker = new FileSavePicker(window)
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = extension,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(typeLabel, [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public async Task<IReadOnlyList<string>> PickOpenFilesAsync(IReadOnlyList<string> extensions, bool allowMultiple)
    {
        if (_window is not { } window)
        {
            return [];
        }

        var picker = new FileOpenPicker(window) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        if (allowMultiple)
        {
            var results = await picker.PickMultipleFilesAsync();
            return results is null ? [] : [.. results.Select(r => r.Path)];
        }

        var single = await picker.PickSingleFileAsync();
        return single is null ? [] : [single.Path];
    }

    public async Task<string?> PickFolderAsync()
    {
        if (_window is not { } window)
        {
            return null;
        }

        var picker = new FolderPicker(window) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
