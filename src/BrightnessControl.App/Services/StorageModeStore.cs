using System.Text.Json;

namespace BrightnessControl.App.Services;

public enum StorageMode
{
    Portable,
    Installed,
}

public sealed class StorageModeInfo
{
    public StorageMode Mode { get; set; } = StorageMode.Installed;

    // Заполнено только при Mode=Portable — папка, где лежит exe на момент
    // выбора режима. AppPaths.BaseDirectory на каждом запуске резолвится
    // именно отсюда, а не заново от текущего Environment.ProcessPath — так
    // выбор остаётся стабильным, даже если запись случайно оказалась в
    // другом месте между запусками (просто перестанет находить свои файлы,
    // а не молча переключится на новую папку).
    public string? PortableExeDirectory { get; set; }

    // Заполнено только при Mode=Installed — папка, выбранная пользователем
    // в FirstRunWindow (Фаза 3) для копии exe. НЕ влияет на
    // AppPaths.BaseDirectory (настройки в Installed-режиме всегда в
    // %LocalAppData%\BrightnessControl, независимо от места установки) —
    // используется только реализацией самой установки (Фаза 4: копирование,
    // ярлык, автозапуск, само-обновление).
    public string? InstallDirectory { get; set; }
}

// FP7 — ЕДИНСТВЕННЫЙ стор в проекте, который намеренно НЕ участвует в
// portable-режиме и не проходит через AppPaths: он сам решает, откуда
// AppPaths.BaseDirectory берёт значение на каждом запуске (см. App.axaml.cs),
// поэтому обязан жить в одном и том же месте независимо от выбранного
// режима — иначе на втором запуске в portable-режиме неоткуда было бы узнать
// сам факт выбора.
public sealed class StorageModeStore
{
    private readonly string _filePath;

    public StorageModeStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrightnessControl",
            "storage-mode.json");
    }

    // Null означает "решения ещё нет" (первый запуск) — в отличие от
    // остальных Store-классов проекта, здесь это различие критично, поэтому
    // Load() не подставляет объект по умолчанию.
    public StorageModeInfo? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<StorageModeInfo>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(StorageModeInfo info)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
