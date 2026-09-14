using Microsoft.Win32;

namespace BrightnessControl.App.Services;

// FP7 Фаза 4 — автозапуск через HKCU (не HKLM): работает без прав
// администратора и действует только для текущего пользователя Windows — тот
// же уровень доступа, каким приложение и так пользуется (реестр DWM,
// собственные файлы настроек и т.п.).
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BrightnessControl";

    public static void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.SetValue(ValueName, $"\"{exePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }
}
