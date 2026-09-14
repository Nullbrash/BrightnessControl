using System.Text.Json;

namespace BrightnessControl.Core;

public sealed class JsonFileAutomationStore : IAutomationStore
{
    private readonly string _filePath;

    public JsonFileAutomationStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.BaseDirectory, "automation.json");
    }

    public AutomationSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AutomationSettings();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AutomationSettings>(json) ?? new AutomationSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AutomationSettings();
        }
    }

    public void Save(AutomationSettings settings)
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
