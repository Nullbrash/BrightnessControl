using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;

namespace BrightnessControl.App.Services;

// FP16 Фаза 6 — скачивание и применение обновления. Самая рискованная часть
// FP16 (трогает свой же РАБОТАЮЩИЙ exe) — согласовано с пользователем:
// никогда не молча (см. ApplyUpdateAndRestart, вызывается только после
// явного подтверждения в UI), без отдельного updater-процесса.
public static class SelfUpdateService
{
    // Ниже этого размера скачанный файл считается подозрительным (например,
    // вместо exe скачалась HTML-страница ошибки) — реальный self-contained
    // однофайловый exe весит десятки мегабайт (см. Фаза 1, ~53 МБ).
    private const long MinimumExpectedSizeBytes = 5_000_000;

    // Пользователь явно попросил показывать прогресс скачивания (процент +
    // размер + скорость), а не голый статичный текст "Скачивание…".
    public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);

    public static async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<DownloadProgress>? progress = null)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BrightnessControl-Updater", AppVersion.Current.ToString()));

        var tempPath = Path.Combine(Path.GetTempPath(), $"BrightnessControl-update-{Guid.NewGuid():N}.exe");

        using (var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;

            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(tempPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReportElapsed = TimeSpan.Zero;
            var lastReportBytes = 0L;
            int read;
            while ((read = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;

                // Не чаще ~150мс — иначе UI-текст мельтешит быстрее, чем
                // человек успевает его прочитать.
                var elapsed = stopwatch.Elapsed;
                var sinceLastReport = elapsed - lastReportElapsed;
                if (progress is not null && sinceLastReport.TotalMilliseconds >= 150)
                {
                    var bytesPerSecond = sinceLastReport.TotalSeconds > 0
                        ? (totalRead - lastReportBytes) / sinceLastReport.TotalSeconds
                        : 0;
                    progress.Report(new DownloadProgress(totalRead, totalBytes, bytesPerSecond));
                    lastReportElapsed = elapsed;
                    lastReportBytes = totalRead;
                }
            }

            progress?.Report(new DownloadProgress(totalRead, totalBytes, 0));
        }

        var downloadedSize = new FileInfo(tempPath).Length;
        if (downloadedSize < MinimumExpectedSizeBytes)
        {
            File.Delete(tempPath);
            throw new InvalidOperationException($"Скачанный файл подозрительно мал ({downloadedSize} байт) — возможно, повреждён или ссылка ведёт не на exe.");
        }

        return tempPath;
    }

    // Переименование-на-месте: Windows разрешает переименовать РАБОТАЮЩИЙ exe
    // (процесс держит файл по идентичности, а не по пути), но не позволяет
    // перезаписать/удалить его, пока он выполняется. Новый exe встаёт на
    // освободившееся имя, затем запускается — а старый ".old" удаляет уже
    // НОВЫЙ процесс при следующем запуске (см. CleanupOldExecutable, вызвано
    // из App.axaml.cs при каждом старте) — на момент этого вызова текущий
    // процесс ещё жив и не может удалить сам себя.
    //
    // Никогда не вызывать без явного подтверждения пользователя в UI — сам
    // метод такого подтверждения не запрашивает.
    public static void ApplyUpdateAndRestart(string downloadedFilePath)
    {
        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к текущему исполняемому файлу.");

        var oldPath = currentExePath + ".old";
        if (File.Exists(oldPath))
        {
            // Остаток от предыдущей попытки, которую некому было убрать —
            // не должно мешать текущей.
            File.Delete(oldPath);
        }

        File.Move(currentExePath, oldPath);
        File.Move(downloadedFilePath, currentExePath);

        Process.Start(new ProcessStartInfo(currentExePath) { UseShellExecute = true });
    }

    // Best-effort — если ".old" ещё залочен (антивирус и т.п.), тихо
    // пропускаем и попробуем на следующем запуске.
    public static void CleanupOldExecutable()
    {
        var currentExePath = Environment.ProcessPath;
        if (currentExePath is null)
        {
            return;
        }

        var oldPath = currentExePath + ".old";
        if (!File.Exists(oldPath))
        {
            return;
        }

        try
        {
            File.Delete(oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
