using System.Text.Json;

namespace BrightnessControl.Core;

// Персистентный, на диске: скорость, с которой безопасно слать DDC/CI-команды
// конкретному монитору, подбирается сама (см. BrightnessController.AdaptPacing) и
// запоминается между запусками — надёжным мониторам не нужно заново "прогреваться"
// каждый раз, а капризным не нужно заново их "ломать", чтобы понять, что они капризные.
public sealed class JsonFileMonitorPacingStore : IMonitorPacingStore
{
    public const int DefaultPacingMs = 100;

    private readonly string _filePath;
    private readonly Dictionary<string, int> _values;
    private readonly object _lock = new();

    public JsonFileMonitorPacingStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "monitor-pacing.json");

        _values = Load(_filePath);
    }

    public int GetPacingMs(string monitorKey)
    {
        lock (_lock)
        {
            return _values.TryGetValue(monitorKey, out var pacing) ? pacing : DefaultPacingMs;
        }
    }

    public void SetPacingMs(string monitorKey, int pacingMs)
    {
        lock (_lock)
        {
            _values[monitorKey] = pacingMs;
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
            DebugLog.Write($"JsonFileMonitorPacingStore.Load: не удалось прочитать '{path}': {ex}");
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
            DebugLog.Write($"JsonFileMonitorPacingStore.Save: не удалось сохранить '{_filePath}': {ex}");
        }
    }
}
