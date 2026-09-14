using System.Text.Json;
using BrightnessControl.Core;

namespace BrightnessControl.App.Services;

// Пользовательские названия мониторов (переименование в настройках) — ключ,
// как и везде в проекте, это BrightnessController.GetMonitorKey(monitor), не
// индекс/название по умолчанию (та же логика идентификации, что уже
// используется для яркости, расписания, профилей приложений).
public sealed class MonitorNameStore
{
    private readonly string _filePath;

    public MonitorNameStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "monitor-names.json");
    }

    public Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, string>();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write($"MonitorNameStore.Load: не удалось прочитать '{_filePath}': {ex}");
            return new Dictionary<string, string>();
        }
    }

    public void Save(Dictionary<string, string> names)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(names));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"MonitorNameStore.Save: не удалось сохранить '{_filePath}': {ex}");
        }
    }
}
