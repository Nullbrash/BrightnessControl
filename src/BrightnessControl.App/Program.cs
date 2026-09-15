using Avalonia;
using System;
using System.Threading;
using System.Threading.Tasks;
using BrightnessControl.App.Native;
using BrightnessControl.Core;

namespace BrightnessControl.App;

sealed class Program
{
    // Именованный на весь Windows-сеанс пользователя — сам GUID произвольный,
    // но фиксированный, чтобы не столкнуться со случайным чужим приложением.
    private const string SingleInstanceMutexName = "BrightnessControl-SingleInstance-9F3A2B1C-7E4D-4B8A-B9C2-1A5F6D8E3C90";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Без этого двойной клик по exe, пока первая копия ещё не успела
        // показать иконку в трее, создавал ВТОРУЮ (и третью…) копию —
        // реально произошло с пользователем на настоящей установке
        // (2026-09-15): несколько процессов дерутся за Shell_NotifyIcon и
        // параллельно пишут в одни и те же файлы настроек. Mutex держится
        // владеющим процессом, пока тот не завершится (создан здесь как
        // `using`, `Main` не возвращается до полного выхода приложения —
        // StartWithClassicDesktopLifetime блокирует до Shutdown).
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            User32Native.MessageBoxW(
                IntPtr.Zero,
                "BrightnessControl уже запущена — смотрите иконку в системном трее.",
                "BrightnessControl",
                User32Native.MB_OK | User32Native.MB_ICONINFORMATION);
            return;
        }

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
