using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Presentation.Services;
using Ookii.Dialogs.Wpf;

namespace MediaTools.Presentation.ViewModels;

public partial class AppSettingsViewModel : ObservableObject
{
    private readonly IUserPreferencesService _preferences;

    public AppSettingsViewModel(IUserPreferencesService preferences)
    {
        _preferences = preferences;
    }

    [ObservableProperty]
    private string _saveFolderPathDraft = string.Empty;

    public void RefreshFromPreferences()
    {
        SaveFolderPathDraft = _preferences.SaveFolderPath;
    }

    [RelayCommand]
    private void BrowseSaveFolder()
    {
        var dlg = new VistaFolderBrowserDialog
        {
            SelectedPath = Directory.Exists(SaveFolderPathDraft.Trim())
                ? Path.GetFullPath(SaveFolderPathDraft.Trim())
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            UseDescriptionForTitle = true,
            Description = "Choose folder for compressed videos and enhanced photos"
        };

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            return;
        }

        SaveFolderPathDraft = dlg.SelectedPath;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var trimmed = SaveFolderPathDraft.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            global::System.Windows.MessageBox.Show(
                "Please enter a folder path.",
                "MediaTools",
                global::System.Windows.MessageBoxButton.OK,
                global::System.Windows.MessageBoxImage.Information);
            return;
        }

        try
        {
            _preferences.SetSaveFolderPath(trimmed);
            SaveFolderPathDraft = _preferences.SaveFolderPath;
            global::System.Windows.MessageBox.Show(
                "Settings saved.",
                "MediaTools",
                global::System.Windows.MessageBoxButton.OK,
                global::System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            global::System.Windows.MessageBox.Show(
                $"Could not save folder: {ex.Message}",
                "MediaTools",
                global::System.Windows.MessageBoxButton.OK,
                global::System.Windows.MessageBoxImage.Warning);
        }
    }
}
