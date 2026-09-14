namespace BrightnessControl.Core;

// FP7/FP16 Фаза 7 — усиленное логирование для разбора багов по запросу
// пользователей. Путь ВСЕГДА фиксирован (%LocalAppData%, не через AppPaths) —
// иначе лог первого запуска (до определения Portable/Installed) было бы
// негде искать. Write — всегда пишет (крэши, сбои сохранения настроек и
// т.п. должны попадать в лог независимо от переключателя "Подробные логи").
// WriteVerbose — только пока VerboseEnabled=true (решения AutomationEngine/
// IdleEngine на каждом тике — по умолчанию было бы слишком шумно).
public static class DebugLog
{
    private static readonly object Lock = new();
    // Отдельная подпапка "logs" — по просьбе пользователя, для наглядности:
    // иначе debug.log лежит вперемешку с десятком JSON-файлов настроек в
    // одной папке (см. скриншот "Открыть папку с логом").
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrightnessControl",
        "logs",
        "debug.log");

    // Устанавливается один раз при старте (из AppSettings.VerboseLoggingEnabled)
    // и обновляется живьём при смене переключателя в настройках — без
    // перезапуска движков.
    public static bool VerboseEnabled { get; set; }

    public static string LogDirectory => Path.GetDirectoryName(FilePath) ?? FilePath;
    public static string LogFilePath => FilePath;

    // Без ротации лог рос бы бесконечно — особенно с VerboseEnabled=true и
    // безусловным логированием DDC/CI (SetBrightness пишет несколько строк
    // на каждый вызов). При превышении лимита текущий файл целиком уходит в
    // .old (затирая предыдущий .old, если он был) — один предыдущий круг
    // лога остаётся для контекста, вместо полной потери истории.
    private const long MaxSizeBytes = 5 * 1024 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();

                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length < MaxSizeBytes)
        {
            return;
        }

        var oldPath = FilePath + ".old";
        if (File.Exists(oldPath))
        {
            File.Delete(oldPath);
        }

        File.Move(FilePath, oldPath);
    }

    public static void WriteVerbose(string message)
    {
        if (VerboseEnabled)
        {
            Write(message);
        }
    }
}
