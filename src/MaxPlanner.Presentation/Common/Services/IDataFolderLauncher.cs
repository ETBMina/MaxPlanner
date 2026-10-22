namespace MaxPlanner.Presentation.Common.Services;

/// <summary>Opens the portable <c>data/</c> folder in File Explorer. Only the folder is ever opened, never a file.</summary>
public interface IDataFolderLauncher
{
    string DataDirectory { get; }

    void OpenDataFolder();
}
