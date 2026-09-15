using System.Diagnostics;
using BrightnessControl.App.Native;
using BrightnessControl.Core;

namespace BrightnessControl.App.Services;

// FP7 Фаза 4 — механика выбора "Установить" в FirstRunWindow: копирует себя в
// выбранную пользователем папку, создаёт ярлык в Пуск И на Рабочем столе
// (по прямому запросу пользователя, 2026-09-15 — "на всякий случай"),
// включает автозапуск, запускает копию и (если копия реально понадобилась)
// сигнализирует вызывающему коду завершить текущий процесс.
//
// FP7 (правка 2026-09-15, по прямому запросу пользователя) — Portable
// раньше просто оставался там, где лежал exe (например в "Загрузках",
// вперемешку с другими скачанными файлами — реальная жалоба пользователя).
// Теперь Portable ТОЖЕ копирует себя в выбранную папку (тот же общий путь,
// что и у "Установить"), просто без ярлыков/автозапуска — InstallPortable
// переиспользует ту же копию+перезапуск, что и Install.
public static class InstallService
{
    public const string ExecutableFileName = "BrightnessControl.App.exe";

    public static bool Install(string installDirectory) =>
        CopyAndRelaunch(installDirectory, createShortcutsAndAutostart: true);

    public static bool InstallPortable(string targetDirectory) =>
        CopyAndRelaunch(targetDirectory, createShortcutsAndAutostart: false);

    // true — запущена НОВАЯ копия, вызывающий код должен завершить текущий
    // процесс (см. App.axaml.cs). false — exe уже и так лежит в целевой папке
    // (редкий случай: FirstRunWindow не должен был показаться повторно, но на
    // всякий случай не пересоздаём то, что уже есть, и не убиваем единственный
    // работающий процесс).
    private static bool CopyAndRelaunch(string targetDirectory, bool createShortcutsAndAutostart)
    {
        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к текущему исполняемому файлу.");

        Directory.CreateDirectory(targetDirectory);
        var targetExePath = Path.Combine(targetDirectory, ExecutableFileName);
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

            if (createShortcutsAndAutostart)
            {
                const string description = "BrightnessControl — управление яркостью мониторов";

                var startMenuShortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "BrightnessControl.lnk");
                ShellLinkFactory.CreateShortcut(startMenuShortcutPath, targetExePath, description);

                var desktopShortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "BrightnessControl.lnk");
                ShellLinkFactory.CreateShortcut(desktopShortcutPath, targetExePath, description);

                AutostartService.Enable(targetExePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Установка — не единственный путь пользоваться приложением: если
            // копирование/ярлык/автозапуск не удались (антивирус, занятый
            // файл и т.п.), продолжаем работу из ТЕКУЩЕГО расположения вместо
            // падения — то же защитное поведение, что и у всех Store-классов
            // проекта (Save() тоже не бросает наружу).
            DebugLog.Write($"InstallService: не удалось скопировать в '{targetDirectory}' (ярлыки/автозапуск={createShortcutsAndAutostart}): {ex}");
            return false;
        }

        if (!alreadyThere)
        {
            Process.Start(new ProcessStartInfo(targetExePath) { UseShellExecute = true });
        }

        return !alreadyThere;
    }
}
