using System.IO;
using System.Text.Json;

namespace MediaTools.Presentation.Services;

public sealed class UserPreferencesService : IUserPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;
    private string _saveFolderPath = string.Empty;
    private bool _toastNotificationsEnabled = true;

    public UserPreferencesService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaTools");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
        LoadPreferencesOrCreateDefaults();
    }

    public string SaveFolderPath => _saveFolderPath;

    public bool ToastNotificationsEnabled => _toastNotificationsEnabled;

    public event EventHandler? SaveFolderPathChanged;

    public void SetSaveFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Save folder path cannot be empty.", nameof(path));
        }

        var normalized = Path.GetFullPath(path.Trim());
        Directory.CreateDirectory(normalized);

        if (string.Equals(_saveFolderPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _saveFolderPath = normalized;
        Persist();
        SaveFolderPathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToastNotificationsEnabled(bool enabled)
    {
        if (_toastNotificationsEnabled == enabled)
        {
            return;
        }

        _toastNotificationsEnabled = enabled;
        Persist();
    }

    private void LoadPreferencesOrCreateDefaults()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                CreateDefaultFolderAndPersist();
                return;
            }

            using var stream = File.OpenRead(_filePath);
            var dto = JsonSerializer.Deserialize<PreferencesDto>(stream, JsonOptions);
            if (dto is null)
            {
                CreateDefaultFolderAndPersist();
                return;
            }

            _toastNotificationsEnabled = dto.ToastNotificationsEnabled ?? true;

            if (string.IsNullOrWhiteSpace(dto.SaveFolderPath))
            {
                CreateDefaultFolderAndPersist();
                return;
            }

            var path = Path.GetFullPath(dto.SaveFolderPath.Trim());
            Directory.CreateDirectory(path);
            _saveFolderPath = path;
        }
        catch
        {
            CreateDefaultFolderAndPersist();
        }
    }

    private void CreateDefaultFolderAndPersist()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MediaTools Export");
        Directory.CreateDirectory(path);
        _saveFolderPath = Path.GetFullPath(path);
        Persist();
    }

    private void Persist()
    {
        try
        {
            var dto = new PreferencesDto
            {
                SaveFolderPath = _saveFolderPath,
                ToastNotificationsEnabled = _toastNotificationsEnabled
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // best-effort persistence
        }
    }

    private sealed class PreferencesDto
    {
        public string SaveFolderPath { get; set; } = string.Empty;

        /// <summary>Omitted in older settings files — treated as true.</summary>
        public bool? ToastNotificationsEnabled { get; set; }
    }
}
