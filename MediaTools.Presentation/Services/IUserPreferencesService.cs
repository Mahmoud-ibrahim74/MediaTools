namespace MediaTools.Presentation.Services;

public interface IUserPreferencesService
{
    /// <summary>Folder where Video Compress and Photo Enhancer write output files.</summary>
    string SaveFolderPath { get; }

    void SetSaveFolderPath(string path);

    event EventHandler? SaveFolderPathChanged;
}
