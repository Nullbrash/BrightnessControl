namespace BrightnessControl.Core;

// FP7 — общая база для файлов всех хранилищ (JSON-настройки, кастомные
// иконки трея). По умолчанию — сегодняшнее поведение (%LocalAppData%
// \BrightnessControl), не меняется, пока явно не вызван Initialize —
// это делает App.axaml.cs один раз при старте, после того как решён
// portable/обычный режим (FP7 Фаза 2/3). DebugLog сюда сознательно НЕ
// подключён — диагностический лог всегда остаётся в %LocalAppData%
// независимо от режима (согласовано с пользователем явно).
public static class AppPaths
{
    private static string? _baseDirectory;

    public static string BaseDirectory
    {
        get => _baseDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrightnessControl");
        private set => _baseDirectory = value;
    }

    public static void Initialize(string baseDirectory) => BaseDirectory = baseDirectory;
}
