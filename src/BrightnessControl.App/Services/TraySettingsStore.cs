using System.Text.Json;
using BrightnessControl.Core;

namespace BrightnessControl.App.Services;

// Значение по умолчанию (10%) уже сейчас настраиваемо — хранится в файле, а не
// зашито в код. Полноценный UI для его редактирования появится в FP3.
public sealed class TraySettingsStore
{
    private readonly string _filePath;

    public TraySettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "tray-settings.json");
    }

    public TraySettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new TraySettings();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TraySettings>(json) ?? new TraySettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write($"TraySettingsStore.Load: не удалось прочитать '{_filePath}': {ex}");
            return new TraySettings();
        }
    }

    public void Save(TraySettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"TraySettingsStore.Save: не удалось сохранить '{_filePath}': {ex}");
        }
    }
}
