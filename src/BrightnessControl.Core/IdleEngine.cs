using BrightnessControl.Core.Native;

namespace BrightnessControl.Core;

// Приглушение по бездействию — глобальное, разом для ВСЕХ мониторов (в отличие от
// правил AutomationEngine, у него нет привязки к конкретным мониторам, см. FP6
// Фазу 1: так проще и предсказуемее для MVP). Простой определяется опросом
// GetLastInputInfo — для него нет системного события, в отличие от
// SetWinEventHook у ForegroundAppWatcher (FP5).
public sealed class IdleEngine : IDisposable
{
    private readonly BrightnessController _controller;
    private readonly IIdleSettingsStore _store;
    private readonly Func<MonitorInfo, int?>? _resolveAutomationPercent;
    private readonly Func<MonitorInfo, bool>? _isMonitorLocked;
    private readonly Dictionary<string, int> _preDimSnapshots = new();
    // Таймер работает на ThreadPool и НЕ ждёт завершения предыдущего тика — если
    // DimAll/RestoreAll (реальная запись в DDC/CI, может занять больше, чем короткий
    // опрос) ещё выполняется, а таймер уже вызвал следующий тик, оба запуска будут
    // одновременно читать/писать _preDimSnapshots и _isDimmed без какой-либо защиты.
    // Замечено на практике при уменьшении опроса до 1с: приглушение "залипало" и не
    // восстанавливалось — гонка портила снимок (или обнуляла его не вовремя).
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _isDimmed;
    private bool _disposed;

    // Для живой индикации в GUI (см. FP6 Фазу 2) и чтобы AutomationEngine (FP10) не
    // "спорил" с приглушением, пока оно активно (см. композитный предикат в App.axaml.cs).
    public bool IsDimmed => _isDimmed;

    public IdleEngine(
        BrightnessController controller,
        IIdleSettingsStore? store = null,
        Func<MonitorInfo, int?>? resolveAutomationPercent = null,
        Func<MonitorInfo, bool>? isMonitorLocked = null,
        bool autoStart = true)
    {
        _controller = controller;
        _store = store ?? new JsonFileIdleSettingsStore();
        _resolveAutomationPercent = resolveAutomationPercent;
        _isMonitorLocked = isMonitorLocked;

        if (autoStart)
        {
            Start();
        }
    }

    // Публично — вызывается заново из GUI при смене PollIntervalSeconds, чтобы новый
    // интервал подхватился сразу, а не только после перезапуска приложения.
    public void Start()
    {
        var settings = _store.Load();
        var intervalMs = Math.Max(1, settings.PollIntervalSeconds) * 1000;

        _timer?.Dispose();
        _timer = new Timer(_ => EvaluateAndApply(IdleTime.GetIdleDuration()), null, 0, intervalMs);
    }

    // Публично и принимает время простоя явно — чтобы можно было проверить логику
    // без реального ожидания бездействия (см. тесты), по аналогии с
    // AutomationEngine.EvaluateAndApplyAll(DateTime).
    public void EvaluateAndApply(TimeSpan idleDuration)
    {
        lock (_gate)
        {
            var settings = _store.Load();
            if (!settings.IsEnabled)
            {
                if (_isDimmed)
                {
                    RestoreAll();
                }

                return;
            }

            var timeoutReached = idleDuration >= TimeSpan.FromMinutes(Math.Max(1, settings.IdleTimeoutMinutes));

            if (timeoutReached && !_isDimmed)
            {
                DebugLog.WriteVerbose($"IdleEngine: простой {idleDuration:mm\\:ss} >= таймаута ({settings.IdleTimeoutMinutes} мин) -> приглушение до {settings.DimPercent}%");
                DimAll(settings.DimPercent);
            }
            else if (!timeoutReached && _isDimmed)
            {
                DebugLog.WriteVerbose($"IdleEngine: активность обнаружена -> восстановление яркости");
                RestoreAll();
            }
        }
    }

    private void DimAll(int dimPercent)
    {
        _preDimSnapshots.Clear();
        var targets = new Dictionary<MonitorInfo, int>();
        foreach (var monitor in _controller.Monitors)
        {
            var current = _controller.GetBrightness(monitor)?.Percent;
            if (current is not null)
            {
                _preDimSnapshots[BrightnessController.GetMonitorKey(monitor)] = current.Value;
            }

            // FP14: залоченный монитор пропускаем целиком — простой не должен
            // приглушать яркость, которую пользователь явно зафиксировал.
            if (_isMonitorLocked?.Invoke(monitor) != true)
            {
                targets[monitor] = dimPercent;
            }
        }

        // Один параллельный проход (см. BrightnessController.SetEachBrightness) — та же
        // адаптивная пауза на монитор, что и везде в проекте. Мониторы из-за неё всё
        // равно закончат запись не одновременно — это принято как есть, а не решается
        // искусственной "лесенкой" из мелких шагов (та выглядела хуже, чем один скачок).
        // Раньше здесь был единый SetAllBrightness (бьёт по ВСЕМ мониторам разом) — с
        // появлением лока (FP14) понадобилась поштучная фильтрация, поэтому перешли на
        // тот же SetEachBrightness, что уже использует RestoreAll ниже.
        _controller.SetEachBrightness(targets);
        _isDimmed = true;
    }

    private void RestoreAll()
    {
        var targets = new Dictionary<MonitorInfo, int>();
        foreach (var monitor in _controller.Monitors)
        {
            // FP14: залоченный монитор простой не трогал при затемнении (см. DimAll),
            // поэтому и восстанавливать для него нечего — пропускаем.
            if (_isMonitorLocked?.Invoke(monitor) == true)
            {
                continue;
            }

            var monitorKey = BrightnessController.GetMonitorKey(monitor);
            int? snapshot = _preDimSnapshots.TryGetValue(monitorKey, out var value) ? value : null;

            // Приоритет восстановления: то, что автоматизация (FP10) хочет для
            // монитора ПРЯМО СЕЙЧАС, важнее устаревшего "сырого" снимка до простоя.
            var restoreValue = _resolveAutomationPercent?.Invoke(monitor)
                ?? snapshot;

            if (restoreValue is not null)
            {
                targets[monitor] = restoreValue.Value;
            }
        }

        // Один параллельный проход, как и DimAll — раньше здесь был последовательный
        // foreach по SetBrightness, из-за чего восстановление ощутимо отставало от
        // мгновенного приглушения (особенно с несколькими мониторами).
        _controller.SetEachBrightness(targets);
        _preDimSnapshots.Clear();
        _isDimmed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
    }
}
