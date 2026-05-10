namespace MediaTools.Presentation.Services;

public interface IUserPreferencesService
{
    /// <summary>Folder where Video Compress and Photo Enhancer write output files.</summary>
    string SaveFolderPath { get; }

    /// <summary>When false, completion toasts are not shown (in-app messages still appear).</summary>
    bool ToastNotificationsEnabled { get; }

    void SetSaveFolderPath(string path);

    void SetToastNotificationsEnabled(bool enabled);

    event EventHandler? SaveFolderPathChanged;
}
