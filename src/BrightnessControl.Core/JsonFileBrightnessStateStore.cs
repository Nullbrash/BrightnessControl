using System.Text.Json;

namespace BrightnessControl.Core;

public sealed class JsonFileBrightnessStateStore : IBrightnessStateStore
{
    private readonly string _filePath;
    private readonly Dictionary<string, int> _values;
    private readonly object _lock = new();

    public JsonFileBrightnessStateStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "brightness-state.json");

        _values = Load(_filePath);
    }

    public int? GetLastPercent(string monitorKey)
    {
        lock (_lock)
        {
            return _values.TryGetValue(monitorKey, out var percent) ? percent : null;
        }
    }

    public void SetLastPercent(string monitorKey, int percent)
    {
        lock (_lock)
        {
            _values[monitorKey] = percent;
            Save();
        }
    }

    private static Dictionary<string, int> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, int>();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write($"JsonFileBrightnessStateStore.Load: не удалось прочитать '{path}': {ex}");
            return new Dictionary<string, int>();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_values));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не удалось сохранить состояние — не критично, просто потеряем последнее значение при рестарте.
            DebugLog.Write($"JsonFileBrightnessStateStore.Save: не удалось сохранить '{_filePath}': {ex}");
        }
    }
}
