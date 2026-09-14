namespace BrightnessControl.App.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

// FP16 Фаза 5 — как часто фоновая проверка обновлений повторяется, пока
// приложение работает. Never полностью выключает фоновую проверку — остаётся
// только ручная кнопка "Проверить сейчас" во вкладке "Обновления".
public enum UpdateCheckInterval
{
    Daily,
    Weekly,
    Monthly,
    Never,
}

// FP7 — версия читается как Major.Minor.Build.Revision, каждое число со
// своим смыслом (согласовано с пользователем явно, 2026-09-15):
// Major = Релизы (крупные, для всех), Minor = Обновления (собранная
// готовая работа, публикуется как обычный релиз), Build = Фиксы (активная
// разработка — тот же канал, которым автор перепрошивает свою РАБОЧУЮ
// копию через штатное самообновление вместо ручного копирования exe),
// Revision = дебаг-тесты (публикуются ТОЛЬКО как prerelease:true на
// GitHub — чтобы IncludePrereleaseUpdates их отфильтровывал независимо
// от выбранного здесь канала; сама механика self-update тестировалась
// именно так три раза за сессию, см. .plans/PLAN_FP7-FP16...).
//
// MajorOnly — только Релизы (пропускает все Minor/Build/Revision).
// AllReleases — полное сравнение, включая Фиксы.
// MajorMinor — Релизы + Обновления, БЕЗ Фиксов (сравнение по Major.Minor,
// Build/Revision игнорируются) — добавлено по прямому запросу
// пользователя, средний уровень между MajorOnly и AllReleases. Добавлен
// ТРЕТЬИМ (не между существующими) — enum сериализуется в JSON числом
// (см. AppSettingsStore), смена номеров сломала бы уже сохранённые
// настройки на диске.
public enum UpdateChannel
{
    AllReleases,
    MajorOnly,
    MajorMinor,
}

public sealed class AppSettings
{
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;

    // Шаг прилипания слайдеров в окне настроек — отдельно от шага скролла над
    // иконкой трея (TraySettings.ScrollStepPercent), это разные органы управления.
    public int SliderStepPercent { get; set; } = 5;

    // Процессы, скрытые пользователем из списка предложений при создании профиля
    // приложения (вкладка "Профили приложений") — там их слишком много без фильтра.
    public List<string> HiddenProcessNames { get; set; } = new();

    // FP9 Фаза 7: подкрашивать акцентные элементы Fluent-темы в цвет, который
    // пользователь выбрал в Параметры Windows → Персонализация → Цвета, а не в
    // зашитый по умолчанию синий Avalonia — приложение должно "вливаться" в систему.
    public bool UseWindowsAccentColor { get; set; } = true;

    // FP13: свой акцентный цвет — используется вместо системного, когда
    // UseWindowsAccentColor выключен. Обычный Fluent-синий по умолчанию —
    // нейтральная отправная точка для пикера, ничего не навязывает.
    public string CustomAccentColorHex { get; set; } = "#0078D4";

    // FP13: правило вычисления ВТОРИЧНОГО акцентного цвета из основного (см.
    // ColorHarmony) — строка, а не голый enum, по той же схеме
    // расширяемости, что и TrayIconDesignId/HudStyleId. Действует всегда,
    // независимо от источника основного цвета (Windows или свой).
    public string ColorHarmonySchemeId { get; set; } = ColorHarmonyScheme.Analogous.ToString();

    // FP13: набор вычисленных цветов не меняется — меняется только, КУДА они
    // применяются. При включении пара "основной/тон 2" и пара
    // "противоположный/тон 2" меняются местами по всему приложению (то, что
    // раньше было "основным акцентом" на кнопках/навигации, становится
    // "противоположным" — сейчас видно только на доп. лучах HUD-солнца — и
    // наоборот).
    public bool SwapAccentRoles { get; set; }

    // FP16 Фаза 5 — настройки проверки обновлений, согласованы по
    // Artifact-макету вкладки "Обновления". Проверка при запуске — С ПАУЗОЙ
    // после старта (см. UpdateCheckService.StartPeriodicChecks), а не сразу —
    // явная просьба пользователя не мешать инициализации приложения.
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public UpdateCheckInterval UpdateCheckIntervalPreference { get; set; } = UpdateCheckInterval.Daily;
    public UpdateChannel UpdateChannelPreference { get; set; } = UpdateChannel.AllReleases;
    public bool IncludePrereleaseUpdates { get; set; }

    // FP16 Фаза 7 — по умолчанию ВЫКЛЮЧЕН: решения AutomationEngine/IdleEngine
    // логируются на каждом изменённом тике, у активных пользователей автоматизации
    // это может быстро раздуть debug.log. Критические ошибки (крэши, сбои
    // Store.Save/Load) логируются ВСЕГДА, независимо от этого переключателя —
    // см. DebugLog.Write vs DebugLog.WriteVerbose.
    public bool VerboseLoggingEnabled { get; set; }
}
