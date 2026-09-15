using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using BrightnessControl.App.Services;

namespace BrightnessControl.App.Views;

// FP7 Фаза 3 — показывается ОДИН раз, на первом запуске (StorageModeStore
// ещё не хранит выбор), до остального старта приложения (см. App.axaml.cs
// ContinueStartup). Согласовано с пользователем по Artifact-макету:
// путь установки — общий блок НАД карточками (не внутри "Установить" — та
// раскладка ломалась), каждая карточка — своя кнопка справа, а не клик по
// всей карточке. Значки-эмодзи из макета сознательно НЕ перенесены — тот же
// класс риска, что уже не раз стрелял в проекте с текстовыми символами
// (эмодзи зависят от системного шрифта, как и "▼" в других местах) —
// здесь достаточно жирного заголовка карточки, отдельная векторная иконка
// не стоит своей цены для одноразового экрана.
public partial class FirstRunWindow : Window
{
    // FP7 (правка 2026-09-15) — раньше Portable ВСЕГДА оставался там, где
    // лежал exe (см. portableDirectory ниже, был отдельным параметром) —
    // прямая жалоба пользователя: если запустить exe из "Загрузок", туда же
    // сыпались все JSON-файлы настроек вперемешку со скачанным мусором.
    // Теперь ОДНА общая папка (_installDirectory) используется ОБОИМИ
    // вариантами — разница только в том, что "Установить" ещё создаёт
    // ярлыки/автозапуск (см. InstallService.Install/InstallPortable).
    private string _installDirectory;

    private TextBlock _installPathText = null!;
    private TextBlock _installErrorText = null!;
    private TextBlock _installPermTip = null!;
    private Button _installButton = null!;
    private Button _portableButton = null!;

    public event EventHandler<StorageModeInfo>? Confirmed;

    // Нужен только для XAML-дизайнера/превью.
    public FirstRunWindow()
    {
        _installDirectory = null!;
        InitializeComponent();
    }

    public FirstRunWindow(string defaultInstallDirectory)
    {
        _installDirectory = defaultInstallDirectory;
        InitializeComponent();
        BuildContent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void BuildContent()
    {
        var root = this.FindControl<StackPanel>("Root")!;
        var closeButton = this.FindControl<Border>("CloseButton")!;
        closeButton.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(closeButton, "Закрыть приложение — без выбора продолжить нельзя");
        closeButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        };

        root.Children.Add(new TextBlock
        {
            Text = "Как запускать BrightnessControl?",
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var lead = new TextBlock
        {
            Text = "Выбор можно посмотреть позже во вкладке \"Обновления\" настроек, но не сменить — на новый режим переключаются переустановкой.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 16),
        };
        lead.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));
        root.Children.Add(lead);

        root.Children.Add(BuildInstallPathSection());

        var choice = new StackPanel { Spacing = 10, Margin = new Thickness(0, 16, 0, 0) };
        choice.Children.Add(BuildInstallCard());
        choice.Children.Add(BuildPortableCard());
        root.Children.Add(choice);

        RefreshInstallPathState();
    }

    // Общий блок НАД карточками (не внутри "Установить" — согласовано отдельно
    // после того, как первая версия с путём внутри карточки ломала вёрстку).
    private Control BuildInstallPathSection()
    {
        var section = new StackPanel { Spacing = 5 };

        var label = new TextBlock { Text = "ПАПКА УСТАНОВКИ", FontSize = 10.5, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));
        section.Children.Add(label);

        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        _installPathText = new TextBlock
        {
            Text = _installDirectory,
            FontFamily = new Avalonia.Media.FontFamily("Consolas"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            Padding = new Thickness(9, 6),
        };
        _installPathText.Bind(TextBlock.BackgroundProperty, this.GetResourceObservable("AppSurfaceSunken"));
        ToolTip.SetTip(_installPathText, _installDirectory);
        Grid.SetColumn(_installPathText, 0);
        pathRow.Children.Add(_installPathText);

        var browseButton = new Button { Content = "Обзор…", Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(browseButton, 1);
        browseButton.Click += OnBrowseClicked;
        pathRow.Children.Add(browseButton);

        section.Children.Add(pathRow);

        var hint = new TextBlock
        {
            Text = "Используется обоими вариантами ниже — и \"Установить\", и Portable копируются именно сюда.",
            FontSize = 10.5,
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppFaint"));
        section.Children.Add(hint);

        _installErrorText = new TextBlock
        {
            Text = "Нет прав на запись в эту папку.",
            FontSize = 10.5,
            IsVisible = false,
        };
        _installErrorText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppDanger"));
        section.Children.Add(_installErrorText);

        _installPermTip = new TextBlock
        {
            Text = "Совет: разрешите запись в свойствах этой папки (снять \"Только чтение\") или выберите другую через \"Обзор…\".",
            FontSize = 10.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false,
        };
        _installPermTip.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppFaint"));
        section.Children.Add(_installPermTip);

        return section;
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        // Окно Topmost="True" — нативный диалог выбора папки Windows иначе может
        // открыться ПОЗАДИ него (COM-диалог IFileOpenDialog не всегда способен
        // встать поверх Topmost-окна), из-за чего приложение выглядит зависшим
        // (диалог на самом деле открыт, просто не виден и не в фокусе). Снимаем
        // Topmost на время диалога и возвращаем сразу после закрытия.
        Topmost = false;
        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выберите папку установки",
                AllowMultiple = false,
            });
        }
        finally
        {
            Topmost = true;
        }

        var localPath = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (localPath is null)
        {
            return;
        }

        // "Обзор…" выбирает РОДИТЕЛЬСКУЮ папку (например "E:\E_Progs"), а не
        // готовую папку установки — иначе пользователю пришлось бы самому
        // гадать, создавать ли подпапку "BrightnessControl" вручную. Дописываем
        // её сами, тем же способом, что и у defaultInstallDirectory
        // (%LocalAppData%\Programs\BrightnessControl — уже готовый полный
        // путь). Если выбрали папку, УЖЕ называющуюся "BrightnessControl"
        // (например, повторно зашли туда же) — не дублируем её ещё раз.
        _installDirectory = string.Equals(Path.GetFileName(localPath), "BrightnessControl", StringComparison.OrdinalIgnoreCase)
            ? localPath
            : Path.Combine(localPath, "BrightnessControl");
        _installPathText.Text = _installDirectory;
        ToolTip.SetTip(_installPathText, _installDirectory);
        RefreshInstallPathState();
    }

    private void RefreshInstallPathState()
    {
        var canWrite = FirstRunDetector.CanWriteTo(_installDirectory);
        _installErrorText.IsVisible = !canWrite;
        _installPermTip.IsVisible = !canWrite;
        _installButton.IsEnabled = canWrite;
        _portableButton.IsEnabled = canWrite;
    }

    private Border BuildInstallCard()
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = "Установить", FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Bold });
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "РЕКОМЕНДУЕТСЯ", FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold },
        };
        badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppAccentBrush"));
        ((TextBlock)badge.Child).Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppAccentOnBrush"));
        titleRow.Children.Add(badge);

        var desc = new TextBlock
        {
            Text = "Копируется в папку выше, появляется в меню Пуск, запускается вместе с Windows.",
            FontSize = 11.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        desc.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));

        _installButton = new Button { Content = "Установить", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        _installButton.Click += (_, _) => Confirmed?.Invoke(this, new StorageModeInfo
        {
            Mode = StorageMode.Installed,
            InstallDirectory = _installDirectory,
        });

        var content = new StackPanel();
        content.Children.Add(titleRow);
        content.Children.Add(desc);
        content.Children.Add(_installButton);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 13),
            BorderThickness = new Thickness(1.5),
            Child = content,
        };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppAccentBrush"));
        return card;
    }

    private Border BuildPortableCard()
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = "Portable", FontSize = 13, FontWeight = Avalonia.Media.FontWeight.Bold });

        var desc = new TextBlock
        {
            Text = "Копируется в папку выше и работает оттуда — настройки рядом с файлом, но БЕЗ ярлыков и автозапуска (в отличие от \"Установить\"). Переносится на флешке вместе с этой папкой.",
            FontSize = 11.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        desc.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));

        var content = new StackPanel();
        content.Children.Add(titleRow);
        content.Children.Add(desc);

        _portableButton = new Button
        {
            Content = "Portable",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _portableButton.Click += (_, _) => Confirmed?.Invoke(this, new StorageModeInfo
        {
            Mode = StorageMode.Portable,
            PortableExeDirectory = _installDirectory,
        });
        content.Children.Add(_portableButton);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 13),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppLineStrong"));
        return card;
    }
}
