using System.Reflection;

namespace BrightnessControl.App.Services;

// FP7 — единая точка чтения версии сборки, общая для отображения в UI и для
// сравнения с последним релизом на GitHub (FP16). Источник правды —
// <Version> в BrightnessControl.App.csproj, из него MSBuild сам заполняет
// AssemblyVersion. app.manifest хранит свою отдельную, нигде не читаемую
// версию — сознательно не синхронизируется с этой.
public static class AppVersion
{
    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
}
