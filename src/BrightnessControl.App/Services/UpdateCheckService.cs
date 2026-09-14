using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia.Threading;

namespace BrightnessControl.App.Services;

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; init; }
    public string? LatestVersion { get; init; }
    public string? DownloadUrl { get; init; }
    // FP7 — контрольная сумма ассета, если GitHub её отдал (поле "digest" у
    // релиз-ассета, формат "sha256:<hex>", появилось у GitHub без
    // объявления версии API — если когда-нибудь пропадёт/переименуется,
    // здесь просто останется null, а SelfUpdateService.DownloadUpdateAsync
    // тихо пропустит проверку, не блокируя обновление).
    public string? DownloadSha256 { get; init; }
    public string? Error { get; init; }
    public DateTime CheckedAt { get; init; } = DateTime.Now;
}

// FP16 Фаза 5 — только ПРОВЕРКА (сравнение версии с релизами на GitHub), без
// скачивания/установки — та часть сознательно отделена в Фазу 6
// (переименование-на-месте работающего exe — риск другого класса, требует
// отдельной проверки). Источник — GitHub Releases API, согласовано с
// пользователем явно (не свой сервер).
//
// Держит ссылку на AppSettings (тот же приём, что и у AccentColorService) —
// настройки канала/беты/интервала читаются заново при каждой проверке, а не
// фиксируются один раз при создании, поэтому смена настройки во вкладке
// "Обновления" применяется сразу к следующей проверке без пересоздания
// сервиса.
public sealed class UpdateCheckService
{
    // Владелец/репозиторий — тот же, куда реально пушится код этого проекта
    // (см. git remote). Имя файла-ассета — ожидаемое соглашение для будущих
    // релизов (CI/публикация релизов — отдельная, ещё не сделанная работа);
    // если реальное имя окажется другим, здесь нужно будет поправить.
    private const string ReleasesApiUrl = "https://api.github.com/repos/Nullbrash/BrightnessControl/releases";
    private const string ExpectedAssetName = "BrightnessControl.exe";

    private readonly AppSettings _appSettings;
    private readonly HttpClient _httpClient;
    private DispatcherTimer? _timer;

    public event EventHandler<UpdateCheckResult>? Checked;

    public UpdateCheckResult? LastResult { get; private set; }

    public UpdateCheckService(AppSettings appSettings)
    {
        _appSettings = appSettings;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BrightnessControl-UpdateChecker", AppVersion.Current.ToString()));
        // GitHub API отклоняет запросы без Accept — стандартный заголовок для
        // REST API v3.
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        UpdateCheckResult result;
        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Version? bestVersion = null;
            string? bestVersionText = null;
            string? bestDownloadUrl = null;
            string? bestSha256 = null;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
                {
                    continue;
                }

                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) && prereleaseProp.GetBoolean();
                if (isPrerelease && !_appSettings.IncludePrereleaseUpdates)
                {
                    continue;
                }

                var tagName = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                var versionText = tagName?.TrimStart('v', 'V');
                if (versionText is null || !Version.TryParse(versionText, out var version))
                {
                    continue;
                }

                // MajorOnly/MajorMinor сравнивают только ПЕРВЫЕ N компонентов
                // (Build/Revision игнорируются полностью — не только "не
                // учитываются при сравнении", а физически обрезаются, иначе
                // патч-версия с новым Build всё равно обошла бы фильтр за
                // счёт равных Major/Minor). AllReleases — обычное сравнение
                // Version на всех компонентах.
                var isNewer = _appSettings.UpdateChannelPreference switch
                {
                    UpdateChannel.MajorOnly => version.Major > AppVersion.Current.Major,
                    UpdateChannel.MajorMinor => new Version(version.Major, Math.Max(version.Minor, 0))
                        > new Version(AppVersion.Current.Major, Math.Max(AppVersion.Current.Minor, 0)),
                    _ => version > AppVersion.Current,
                };
                if (!isNewer)
                {
                    continue;
                }

                if (bestVersion is not null && version <= bestVersion)
                {
                    continue;
                }

                string? downloadUrl = null;
                string? sha256 = null;
                if (release.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        if (string.Equals(name, ExpectedAssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                            var digest = asset.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() : null;
                            sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                                ? digest["sha256:".Length..]
                                : null;
                            break;
                        }
                    }
                }

                bestVersion = version;
                bestVersionText = versionText;
                bestDownloadUrl = downloadUrl;
                bestSha256 = sha256;
            }

            result = new UpdateCheckResult
            {
                UpdateAvailable = bestVersion is not null,
                LatestVersion = bestVersionText,
                DownloadUrl = bestDownloadUrl,
                DownloadSha256 = bestSha256,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            result = new UpdateCheckResult { Error = ex.Message };
        }

        LastResult = result;
        Checked?.Invoke(this, result);
        return result;
    }

    // Пользователь явно попросил НЕ проверять сразу при запуске, а с паузой
    // (не мешать инициализации приложения сетевым запросом в первые секунды);
    // CheckUpdatesOnStartup отключает ТОЛЬКО эту стартовую проверку —
    // периодическая по интервалу продолжает идти независимо от неё.
    // UpdateCheckInterval.Never полностью останавливает фоновую проверку,
    // оставляя только ручную CheckNowAsync() (кнопка "Проверить сейчас").
    // Можно вызывать повторно (например, сразу после смены настройки во
    // вкладке "Обновления") — предыдущий таймер останавливается.
    public void StartPeriodicChecks(TimeSpan startupDelay)
    {
        _timer?.Stop();

        var interval = _appSettings.UpdateCheckIntervalPreference switch
        {
            UpdateCheckInterval.Daily => TimeSpan.FromDays(1),
            UpdateCheckInterval.Weekly => TimeSpan.FromDays(7),
            UpdateCheckInterval.Monthly => TimeSpan.FromDays(30),
            _ => (TimeSpan?)null,
        };

        if (interval is null)
        {
            _timer = null;
            return;
        }

        var firstDelay = _appSettings.CheckUpdatesOnStartup ? startupDelay : interval.Value;

        _timer = new DispatcherTimer { Interval = firstDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer!.Stop();
            await CheckNowAsync();
            _timer.Interval = interval.Value;
            _timer.Start();
        };
        _timer.Start();
    }

    public void StopPeriodicChecks()
    {
        _timer?.Stop();
        _timer = null;
    }
}
