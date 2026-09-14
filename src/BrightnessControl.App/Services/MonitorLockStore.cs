using System.Text.Json;
using BrightnessControl.Core;

namespace BrightnessControl.App.Services;

// FP14 — какие мониторы залочены (яркость зафиксирована вручную, автоматика их
// не трогает). Ключ — BrightnessController.GetMonitorKey(monitor), тот же
// принцип идентификации, что и везде в проекте (см. MonitorNameStore). Файл
// отдельный, а не поле в AppSettings/TraySettings — тот же паттерн, что уже
// используется для названий мониторов: маленький самостоятельный стор на одну
// узкую задачу.
public sealed class MonitorLockStore
{
    private readonly string _filePath;

    public MonitorLockStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "monitor-locks.json");
    }

    public HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new HashSet<string>();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write($"MonitorLockStore.Load: не удалось прочитать '{_filePath}': {ex}");
            return new HashSet<string>();
        }
    }

    public void Save(HashSet<string> lockedMonitorKeys)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(lockedMonitorKeys));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"MonitorLockStore.Save: не удалось сохранить '{_filePath}': {ex}");
        }
    }
}
