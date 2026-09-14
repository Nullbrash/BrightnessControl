using System.Diagnostics;
using BrightnessControl.App.Native;
using BrightnessControl.Core;

namespace BrightnessControl.App.Services;

// FP7 Фаза 4 — механика выбора "Установить" в FirstRunWindow: копирует себя в
// выбранную пользователем папку, создаёт ярлык в Пуск, включает автозапуск,
// запускает копию и (если копия реально понадобилась) сигнализирует
// вызывающему коду завершить текущий процесс.
public static class InstallService
{
    public const string ExecutableFileName = "BrightnessControl.App.exe";

    // true — запущена НОВАЯ копия, вызывающий код должен завершить текущий
    // процесс (см. App.axaml.cs). false — exe уже и так лежит в целевой папке
    // (редкий случай: FirstRunWindow не должен был показаться повторно, но на
    // всякий случай не пересоздаём то, что уже есть, и не убиваем единственный
    // работающий процесс).
    public static bool Install(string installDirectory)
    {
        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к текущему исполняемому файлу.");

        Directory.CreateDirectory(installDirectory);
        var targetExePath = Path.Combine(installDirectory, ExecutableFileName);
        var alreadyThere = string.Equals(
            Path.GetFullPath(currentExePath),
            Path.GetFullPath(targetExePath),
            StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!alreadyThere)
            {
                File.Copy(currentExePath, targetExePath, overwrite: true);
            }

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "BrightnessControl.lnk");
            ShellLinkFactory.CreateShortcut(shortcutPath, targetExePath, "BrightnessControl — управление яркостью мониторов");

            AutostartService.Enable(targetExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Установка — не единственный путь пользоваться приложением: если
            // копирование/ярлык/автозапуск не удались (антивирус, занятый
            // файл и т.п.), продолжаем работу из ТЕКУЩЕГО расположения вместо
            // падения — то же защитное поведение, что и у всех Store-классов
            // проекта (Save() тоже не бросает наружу).
            DebugLog.Write($"InstallService.Install: не удалось установить в '{installDirectory}': {ex}");
            return false;
        }

        if (!alreadyThere)
        {
            Process.Start(new ProcessStartInfo(targetExePath) { UseShellExecute = true });
        }

        return !alreadyThere;
    }
}
