using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace m4xmudovv.Services;

/// <summary>Loads and saves settings.json (atomic writes, debounced saving). Use from the UI thread.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "m4xmudovv",
        "settings.json");

    private readonly DispatcherTimer _saveTimer;

    public SettingsService()
    {
        Settings = Load();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => Flush();
    }

    public AppSettings Settings { get; }

    private AppSettings LoadFromDisk()
    {
        var json = File.ReadAllText(_file);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    private AppSettings Load()
    {
        AppSettings settings;
        try { settings = File.Exists(_file) ? LoadFromDisk() : new AppSettings(); }
        catch (JsonException) { settings = new AppSettings(); }
        catch (IOException) { settings = new AppSettings(); }
        catch (UnauthorizedAccessException) { settings = new AppSettings(); }
        settings.Sanitize();
        return settings;
    }

    /// <summary>Saves shortly after the last change, so dragging a slider does not hit the disk on every tick.</summary>
    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Saves immediately. Returns false if the file could not be written.</summary>
    public bool Flush()
    {
        _saveTimer.Stop();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temp, _file, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
