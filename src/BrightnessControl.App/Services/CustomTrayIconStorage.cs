using BrightnessControl.Core;

namespace BrightnessControl.App.Services;

// FP11 — одна запись в TraySettings.CustomTrayIcons: Id — сгенерированный GUID
// (используется как TrayIconDesignId, ключ в TrayIconScaleByDesign/
// TrayIconDesignNameOverrides/TrayIconDesignOrder — те уже строково-ключевые,
// написаны под расширяемость без миграции, см. FP8), FileName — имя файла
// ВНУТРИ папки хранения (не полный путь — папка сама может переехать между
// версиями Windows/пользователями). DisplayName — имя файла на момент импорта
// (без расширения), используется только как ПЕРВОНАЧАЛЬНОЕ имя карточки, пока
// пользователь его не переименует явно (тот же override-механизм, что и у
// встроенных форм).
public sealed record CustomTrayIcon(string Id, string FileName, string DisplayName);

// Копирует выбранный пользователем файл В СВОЮ папку (не хранит путь к
// оригиналу) — переживает переименование/перемещение/удаление исходного
// файла, тот же принцип, что и у остальных хранилищ проекта.
public static class CustomTrayIconStorage
{
    private static string FolderPath => Path.Combine(AppPaths.BaseDirectory, "tray-icons");

    public static CustomTrayIcon Import(string sourceFilePath)
    {
        Directory.CreateDirectory(FolderPath);

        var id = Guid.NewGuid().ToString("N");
        var extension = Path.GetExtension(sourceFilePath);
        var fileName = id + extension;
        File.Copy(sourceFilePath, Path.Combine(FolderPath, fileName), overwrite: true);

        var displayName = Path.GetFileNameWithoutExtension(sourceFilePath);
        return new CustomTrayIcon(id, fileName, string.IsNullOrWhiteSpace(displayName) ? "Своя иконка" : displayName);
    }

    public static string GetFilePath(CustomTrayIcon icon) => Path.Combine(FolderPath, icon.FileName);

    public static void Delete(CustomTrayIcon icon)
    {
        try
        {
            File.Delete(GetFilePath(icon));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
