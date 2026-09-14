namespace BrightnessControl.Core;

// FP10 — единый движок конструктора автоматизации, заменяет собой ScheduleEngine
// (FP4) и AppProfileEngine (FP5): вместо двух раздельных слоёв ("профиль поверх
// расписания") теперь один список правил, каждое из которых может нести условие
// по времени (набор отрезков, см. TimeSegment), по процессу (как раньше у
// AppProfile), или оба сразу (см. AutomationRule.Combinator). Победитель среди одновременно
// активных правил на монитор пересчитывается заново на КАЖДОМ тике (периодическом
// или по смене переднего окна) — см. ResolveWinner. Модель декларативная: правило
// не "восстанавливает как было" при потере активности, а просто перестаёт быть
// кандидатом — см. PLAN_FP10, Фаза 1, п.8, это сознательный отказ от снимков
// AppProfileEngine.
public sealed class AutomationEngine : IDisposable
{
    private readonly BrightnessController _controller;
    private readonly IAutomationStore _store;
    private readonly IForegroundAppWatcher _watcher;
    private readonly Func<MonitorInfo, bool>? _isMonitorLocked;
    private readonly Func<bool>? _isSuppressed;
    private readonly Dictionary<string, string?> _lastActiveRuleId = new();
    // Таймер (ThreadPool) и OnForegroundChanged (нативный хук смены переднего
    // окна) оба зовут EvaluateAndApplyAll — на практике это РЕАЛЬНО происходит
    // одновременно (замечено по краху: "A concurrent update was performed on
    // this collection and corrupted its state" — Dictionary не потокобезопасен).
    // Та же гонка, от которой уже защищён IdleEngine._gate, здесь была упущена
    // при первой реализации. Лочим доступ и к _lastActiveRuleId, и к
    // _lastForegroundInfo — оба читаются/пишутся из разных потоков.
    private readonly object _gate = new();
    private ForegroundAppInfo? _lastForegroundInfo;
    private Timer? _timer;
    private bool _disposed;

    // isSuppressed: глобальное подавление (приглушение по бездействию, FP6) — идёт
    // ОДНИМ значением на все мониторы, в отличие от isMonitorLocked (FP14),
    // который per-монитор — см. App.axaml.cs, где оба композируются.
    public AutomationEngine(
        BrightnessController controller,
        IAutomationStore? store = null,
        IForegroundAppWatcher? watcher = null,
        Func<MonitorInfo, bool>? isMonitorLocked = null,
        Func<bool>? isSuppressed = null,
        bool autoStart = true)
    {
        _controller = controller;
        _store = store ?? new JsonFileAutomationStore();
        _watcher = watcher ?? new ForegroundAppWatcher();
        _isMonitorLocked = isMonitorLocked;
        _isSuppressed = isSuppressed;
        _watcher.ForegroundChanged += OnForegroundChanged;

        if (autoStart)
        {
            Start();
        }

        _watcher.ReportCurrentForegroundWindow();
    }

    public void Start()
    {
        var settings = _store.Load();
        var intervalMs = Math.Max(1, settings.CheckIntervalMinutes) * 60_000;

        _timer?.Dispose();
        _timer = new Timer(_ => EvaluateAndApplyAll(DateTime.Now), null, 0, intervalMs);
    }

    // Публично и принимает данные явно — чтобы можно было проверить логику без
    // реального нативного хука (см. тесты), по тому же принципу, что раньше у
    // AppProfileEngine.OnForegroundChanged/ScheduleEngine.EvaluateAndApply.
    public void OnForegroundChanged(ForegroundAppInfo info)
    {
        lock (_gate)
        {
            _lastForegroundInfo = info;
        }
        EvaluateAndApplyAll(DateTime.Now);
    }

    public void EvaluateAndApplyAll(DateTime now)
    {
        if (_isSuppressed?.Invoke() == true)
        {
            return;
        }

        var settings = _store.Load();
        if (!settings.IsEnabled || settings.Rules.Count == 0)
        {
            return;
        }

        var timeOfDay = TimeOnly.FromDateTime(now);

        // Держим лок на весь проход, включая SetBrightness (может занять
        // заметное время на монитор) — тот же компромисс, что уже принят у
        // IdleEngine.DimAll/RestoreAll: конкурентный вызов подождёт своей
        // очереди вместо того, чтобы гонять недопотокобезопасный словарь.
        lock (_gate)
        {
            foreach (var monitor in _controller.Monitors)
            {
                if (_isMonitorLocked?.Invoke(monitor) == true)
                {
                    continue;
                }

                var monitorKey = BrightnessController.GetMonitorKey(monitor);
                var winner = ResolveWinner(settings, monitor, monitorKey, _lastForegroundInfo, timeOfDay);

                _lastActiveRuleId.TryGetValue(monitorKey, out var lastId);
                if (lastId == winner?.Id)
                {
                    continue;
                }

                _lastActiveRuleId[monitorKey] = winner?.Id;
                if (winner is not null)
                {
                    DebugLog.WriteVerbose($"AutomationEngine: правило '{winner.Name}' -> монитор {monitorKey}: {winner.Percent}%");
                    _controller.SetBrightness(monitor, winner.Percent);
                }
                else if (lastId is not null)
                {
                    DebugLog.WriteVerbose($"AutomationEngine: монитор {monitorKey} — активных правил больше нет (было '{lastId}')");
                }
            }
        }
    }

    // Публичный, БЕЗ записи в монитор и без учёта isSuppressed/isMonitorLocked —
    // чистый запрос "что автоматизация хочет для этого монитора ПРЯМО СЕЙЧАС".
    // Переиспользуется IdleEngine (восстановление после простоя — на момент вызова
    // приглушение ещё формально активно, поэтому проверка isSuppressed здесь была
    // бы неверной) и App.axaml.cs (ресинхронизация при снятии лока FP14). Раньше
    // такой расчёт (ComputeScheduleFallback) был чистой функцией и намеренно
    // дублировался в трёх местах — здесь он требует ЖИВОГО состояния переднего
    // окна, поэтому переиспользуется через общий инстанс вместо копирования.
    public int? ResolvePercentForMonitor(MonitorInfo monitor) => ResolveActiveRuleForMonitor(monitor)?.Percent;

    // Тот же расчёт, но возвращает само правило (не только процент) — нужен GUI
    // (см. SettingsWindow.BuildAutomationTab), чтобы показать пользователю НАЗВАНИЕ
    // правила, а не только итоговый процент.
    public AutomationRule? ResolveActiveRuleForMonitor(MonitorInfo monitor)
    {
        var settings = _store.Load();
        if (!settings.IsEnabled || settings.Rules.Count == 0)
        {
            return null;
        }

        var monitorKey = BrightnessController.GetMonitorKey(monitor);
        lock (_gate)
        {
            return ResolveWinner(settings, monitor, monitorKey, _lastForegroundInfo, TimeOnly.FromDateTime(DateTime.Now));
        }
    }

    public static AutomationRule? ResolveWinner(
        AutomationSettings settings,
        MonitorInfo monitor,
        string monitorKey,
        ForegroundAppInfo? foreground,
        TimeOnly timeOfDay)
    {
        AutomationRule? best = null;
        var bestIsDynamic = false;

        foreach (var rule in settings.Rules)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }

            var (isActive, isDynamicScope) = Evaluate(rule, foreground, timeOfDay);
            if (!isActive)
            {
                continue;
            }

            if (isDynamicScope)
            {
                if (foreground?.MonitorAdapterDeviceName != monitor.AdapterDeviceName)
                {
                    continue;
                }
            }
            else if (rule.MonitorKeys.Count != 0 && !rule.MonitorKeys.Contains(monitorKey))
            {
                continue;
            }

            if (best is null || IsBetter(rule, isDynamicScope, best, bestIsDynamic))
            {
                best = rule;
                bestIsDynamic = isDynamicScope;
            }
        }

        return best;
    }

    // true, когда правило истинно (полностью или частично) благодаря условию по
    // процессу — определяет область действия (динамическая vs MonitorKeys, см.
    // PLAN_FP10 Фаза 1 п.7) и приоритет между правилами без явного Priority (см.
    // IsBetter).
    private static (bool IsActive, bool IsDynamicScope) Evaluate(
        AutomationRule rule,
        ForegroundAppInfo? foreground,
        TimeOnly timeOfDay)
    {
        var timeMatch = rule.HasTimeCondition ? rule.MatchesTime(MinutesOfDay(timeOfDay)) : (bool?)null;
        var processMatch = rule.HasProcessCondition
            ? foreground is not null && rule.MatchesProcess(foreground.Value.ProcessName, foreground.Value.WindowTitle)
            : (bool?)null;

        if (timeMatch is null && processMatch is null)
        {
            return (false, false);
        }

        if (timeMatch is not null && processMatch is not null)
        {
            var isActive = rule.Combinator == AutomationCombinator.And
                ? timeMatch.Value && processMatch.Value
                : timeMatch.Value || processMatch.Value;

            var isDynamic = rule.Combinator == AutomationCombinator.And
                ? isActive
                : processMatch.Value;

            return (isActive, isDynamic);
        }

        if (processMatch is not null)
        {
            return (processMatch.Value, processMatch.Value);
        }

        return (timeMatch!.Value, false);
    }

    // TimeSegment оперирует минутами от полуночи (0..1440), а не TimeOnly — см.
    // TimeSegment.cs. TimeOnly.Hour/Minute достаточно точны для этого (секунды
    // не нужны — движок и так тикает по минутам, см. CheckIntervalMinutes).
    private static int MinutesOfDay(TimeOnly time) => time.Hour * 60 + time.Minute;

    // candidate перебирается ПОСЛЕ current по порядку списка — при равном ранге
    // возвращает false (сохраняет current), поэтому порядок в списке естественно
    // работает как последний тай-брейк (см. PLAN_FP10 Фаза 1 п.6).
    private static bool IsBetter(AutomationRule candidate, bool candidateIsDynamic, AutomationRule current, bool currentIsDynamic)
    {
        var candidateHasPriority = candidate.Priority is not null;
        var currentHasPriority = current.Priority is not null;

        if (candidateHasPriority != currentHasPriority)
        {
            return candidateHasPriority;
        }

        if (candidateHasPriority && currentHasPriority)
        {
            return candidate.Priority!.Value > current.Priority!.Value;
        }

        return candidateIsDynamic && !currentIsDynamic;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _watcher.ForegroundChanged -= OnForegroundChanged;
        _watcher.Dispose();
    }
}
