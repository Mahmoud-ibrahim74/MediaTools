using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.Services;

public sealed class UserPreferencesService : IUserPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;
    private string _saveFolderPath = string.Empty;
    private bool _toastNotificationsEnabled = true;
    private VideoHardwareEncoderKind _videoHardwareEncoder = VideoHardwareEncoderKind.Software;
    private VideoEncoderScanResult? _lastVideoEncoderScan;
    private HotkeySetting _screenRecorderStart = DefaultStartHotkey;
    private HotkeySetting _screenRecorderPause = DefaultPauseHotkey;

    private int _lifetimeVideoCompressed;
    private int _lifetimePhotoEnhanced;
    private int _lifetimeAudioEnhanced;
    private int _lifetimeScreenRecorded;

    private static HotkeySetting DefaultStartHotkey => new(0, 0x78); // F9
    private static HotkeySetting DefaultPauseHotkey => new(0, 0x79); // F10

    public UserPreferencesService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaTools");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
        LoadPreferencesOrCreateDefaults();
    }

    public string SaveFolderPath => _saveFolderPath;

    public bool ToastNotificationsEnabled => _toastNotificationsEnabled;

    public VideoHardwareEncoderKind PreferredVideoHardwareEncoder => _videoHardwareEncoder;

    public VideoEncoderScanResult? LastVideoEncoderScan => _lastVideoEncoderScan;

    public event EventHandler? SaveFolderPathChanged;

    public event EventHandler? VideoEncoderSettingsChanged;

    public event EventHandler? ScreenRecorderHotkeysChanged;

    public event EventHandler? LifetimeStatsChanged;

    public int LifetimeVideoCompressedCount => _lifetimeVideoCompressed;

    public int LifetimePhotoEnhancedCount => _lifetimePhotoEnhanced;

    public int LifetimeAudioEnhancedCount => _lifetimeAudioEnhanced;

    public int LifetimeScreenRecordedCount => _lifetimeScreenRecorded;

    public void IncrementLifetimeStat(AppLifetimeStatKind kind)
    {
        switch (kind)
        {
            case AppLifetimeStatKind.VideoCompressed:
                _lifetimeVideoCompressed++;
                break;
            case AppLifetimeStatKind.PhotoEnhanced:
                _lifetimePhotoEnhanced++;
                break;
            case AppLifetimeStatKind.AudioEnhanced:
                _lifetimeAudioEnhanced++;
                break;
            case AppLifetimeStatKind.ScreenRecorded:
                _lifetimeScreenRecorded++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        Persist();
        LifetimeStatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public HotkeySetting ScreenRecorderStartHotkey => _screenRecorderStart;

    public HotkeySetting ScreenRecorderPauseHotkey => _screenRecorderPause;

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

    public void SetVideoEncoderSettings(VideoHardwareEncoderKind preference, VideoEncoderScanResult scan)
    {
        _videoHardwareEncoder = CoercePreference(preference, scan);
        _lastVideoEncoderScan = scan;
        Persist();
        VideoEncoderSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetScreenRecorderHotkeys(HotkeySetting start, HotkeySetting pause)
    {
        if (_screenRecorderStart == start && _screenRecorderPause == pause)
        {
            return;
        }

        _screenRecorderStart = start;
        _screenRecorderPause = pause;
        Persist();
        ScreenRecorderHotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    private static VideoHardwareEncoderKind CoercePreference(VideoHardwareEncoderKind preference, VideoEncoderScanResult scan)
    {
        if (preference == VideoHardwareEncoderKind.Software)
        {
            return VideoHardwareEncoderKind.Software;
        }

        return preference switch
        {
            VideoHardwareEncoderKind.Nvenc when scan.NvencAvailable => VideoHardwareEncoderKind.Nvenc,
            VideoHardwareEncoderKind.Amf when scan.AmfAvailable => VideoHardwareEncoderKind.Amf,
            VideoHardwareEncoderKind.QuickSync when scan.QuickSyncAvailable => VideoHardwareEncoderKind.QuickSync,
            _ => VideoHardwareEncoderKind.Software
        };
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

            if (dto.EncoderScanNvenc is { } nv
                && dto.EncoderScanAmf is { } amf
                && dto.EncoderScanQsv is { } qsv)
            {
                _lastVideoEncoderScan = new VideoEncoderScanResult(nv, amf, qsv);
            }

            if (dto.VideoHardwareEncoder is { } enc)
            {
                var scanOrFallback = _lastVideoEncoderScan ?? new VideoEncoderScanResult(false, false, false);
                _videoHardwareEncoder = CoercePreference(enc, scanOrFallback);
            }

            _screenRecorderStart = NormalizeLoadedHotkey(dto.ScreenRecorderStartMods, dto.ScreenRecorderStartVk, DefaultStartHotkey);
            _screenRecorderPause = NormalizeLoadedHotkey(dto.ScreenRecorderPauseMods, dto.ScreenRecorderPauseVk, DefaultPauseHotkey);

            _lifetimeVideoCompressed = dto.LifetimeVideoCompressed ?? 0;
            _lifetimePhotoEnhanced = dto.LifetimePhotoEnhanced ?? 0;
            _lifetimeAudioEnhanced = dto.LifetimeAudioEnhanced ?? 0;
            _lifetimeScreenRecorded = dto.LifetimeScreenRecorded ?? 0;

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
                ToastNotificationsEnabled = _toastNotificationsEnabled,
                VideoHardwareEncoder = _videoHardwareEncoder,
                EncoderScanNvenc = _lastVideoEncoderScan?.NvencAvailable,
                EncoderScanAmf = _lastVideoEncoderScan?.AmfAvailable,
                EncoderScanQsv = _lastVideoEncoderScan?.QuickSyncAvailable,
                ScreenRecorderStartMods = _screenRecorderStart.Modifiers,
                ScreenRecorderStartVk = _screenRecorderStart.VirtualKey,
                ScreenRecorderPauseMods = _screenRecorderPause.Modifiers,
                ScreenRecorderPauseVk = _screenRecorderPause.VirtualKey,
                LifetimeVideoCompressed = _lifetimeVideoCompressed,
                LifetimePhotoEnhanced = _lifetimePhotoEnhanced,
                LifetimeAudioEnhanced = _lifetimeAudioEnhanced,
                LifetimeScreenRecorded = _lifetimeScreenRecorded
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

        public VideoHardwareEncoderKind? VideoHardwareEncoder { get; set; }

        public bool? EncoderScanNvenc { get; set; }

        public bool? EncoderScanAmf { get; set; }

        public bool? EncoderScanQsv { get; set; }

        public uint? ScreenRecorderStartMods { get; set; }

        public uint? ScreenRecorderStartVk { get; set; }

        public uint? ScreenRecorderPauseMods { get; set; }

        public uint? ScreenRecorderPauseVk { get; set; }

        public int? LifetimeVideoCompressed { get; set; }

        public int? LifetimePhotoEnhanced { get; set; }

        public int? LifetimeAudioEnhanced { get; set; }

        public int? LifetimeScreenRecorded { get; set; }
    }

    private static HotkeySetting NormalizeLoadedHotkey(uint? mods, uint? vk, HotkeySetting fallback)
    {
        if (vk is null)
        {
            return fallback;
        }

        if (vk.Value == 0)
        {
            return HotkeySetting.Empty;
        }

        var m = mods ?? 0;
        m &= ~(HotkeySetting.ModNoRepeat);
        return new HotkeySetting(m, vk.Value);
    }
}
