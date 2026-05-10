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
    private string _saveFolderPath;

    public UserPreferencesService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaTools");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
        _saveFolderPath = LoadSaveFolderPathOrDefault();
    }

    public string SaveFolderPath => _saveFolderPath;

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

    private string LoadSaveFolderPathOrDefault()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefaultAndPersist();
            }

            using var stream = File.OpenRead(_filePath);
            var dto = JsonSerializer.Deserialize<PreferencesDto>(stream, JsonOptions);
            if (string.IsNullOrWhiteSpace(dto?.SaveFolderPath))
            {
                return CreateDefaultAndPersist();
            }

            var path = Path.GetFullPath(dto.SaveFolderPath.Trim());
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return CreateDefaultAndPersist();
        }
    }

    private string CreateDefaultAndPersist()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MediaTools Export");
        Directory.CreateDirectory(path);
        _saveFolderPath = Path.GetFullPath(path);
        Persist();
        return _saveFolderPath;
    }

    private void Persist()
    {
        try
        {
            var dto = new PreferencesDto { SaveFolderPath = _saveFolderPath };
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
    }
}
