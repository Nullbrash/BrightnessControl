using Avalonia;
using System;
using System.Threading.Tasks;
using BrightnessControl.Core;

namespace BrightnessControl.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // FP16 Фаза 7 — до этой правки крэш не оставлял в логе НИКАКОГО следа.
        // Регистрируется ДО BuildAvaloniaApp — это обычные .NET-хуки
        // (AppDomain/TaskScheduler), Avalonia для них не нужна, поэтому
        // ограничение выше (не трогать Avalonia-API до AppMain) их не
        // касается. DebugLog.Write, а не WriteVerbose — крэш логируется
        // ВСЕГДА, независимо от переключателя "Подробные логи".
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DebugLog.Write($"UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DebugLog.Write($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
