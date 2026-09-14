using System.Text.Json;

namespace BrightnessControl.Core;

public sealed class JsonFileIdleSettingsStore : IIdleSettingsStore
{
    private readonly string _filePath;

    public JsonFileIdleSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "idle-settings.json");
    }

    public IdleSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new IdleSettings();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<IdleSettings>(json) ?? new IdleSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new IdleSettings();
        }
    }

    public void Save(IdleSettings settings)
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
        }
    }
}
