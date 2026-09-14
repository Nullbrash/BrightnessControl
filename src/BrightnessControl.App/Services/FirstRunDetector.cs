namespace BrightnessControl.App.Services;

// FP7 Фаза 3 — проверка записи в конкретную папку: пробная запись+удаление
// временного файла, тот же приём, что уже используется во всех Store-классах
// проекта (Save() внутри try/catch на IOException/UnauthorizedAccessException).
// Используется FirstRunWindow для (а) решения, доступен ли Portable из текущей
// папки exe, (б) проверки папки установки, выбранной через "Обзор…".
public static class FirstRunDetector
{
    public static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".wtest-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probePath, Array.Empty<byte>());
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
