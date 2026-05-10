using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Presentation.Helpers;
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
            MessageBoxHelper.ShowInformation("Please enter a folder path.");
            return;
        }

        try
        {
            _preferences.SetSaveFolderPath(trimmed);
            SaveFolderPathDraft = _preferences.SaveFolderPath;
            MessageBoxHelper.ShowInformation("Settings saved.");
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not save folder: {ex.Message}");
        }
    }
}
