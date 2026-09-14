using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BrightnessControl.App.Services;
using BrightnessControl.Core;
using System.Diagnostics;

namespace BrightnessControl.App.Views;

public partial class SettingsWindow : Window
{
    private readonly BrightnessController _controller;
    private readonly AppSettings _appSettings;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly TraySettings _traySettings;
    private readonly TraySettingsStore _traySettingsStore;
    private readonly AutomationEngine? _automationEngine;
    private readonly IdleEngine? _idleEngine;
    private readonly AccentColorService? _accentColorService;
    private readonly TrayService? _trayService;
    private readonly Action _onExitRequested;

    // FP13 — единственный экземпляр окна предпросмотра темы: повторный клик
    // на кнопку должен активировать уже открытое окно, а не плодить дубликаты.
    private ThemePreviewWindow? _themePreviewWindow;

    // Двухуровневая навигация (FP9 Фаза 3): _selectedCategory == null — показан
    // список категорий верхнего уровня; иначе — подкатегории ВЫБРАННОЙ категории
    // (список целиком подменяется, а не разворачивается на месте — решено заранее).
    private List<NavCategory> _rootCategories = new();
    private NavCategory? _selectedCategory;
    private NavSubcategory? _selectedSubcategory;
    private bool _navOnRight = true;

    private sealed record NavCategory(string Title, List<NavSubcategory> Subcategories);
    private sealed record NavSubcategory(string Title, Action<StackPanel> BuildContent);

    // Нужен только для XAML-дизайнера/превью — реальный экземпляр всегда создаётся
    // через конструктор ниже, с реальными зависимостями.
    public SettingsWindow()
    {
        _controller = null!;
        _appSettings = null!;
        _appSettingsStore = null!;
        _traySettings = null!;
        _traySettingsStore = null!;
        _automationEngine = null;
        _idleEngine = null;
        _accentColorService = null;
        _trayService = null;
        _onExitRequested = () => { };
        InitializeComponent();
    }

    public SettingsWindow(
        BrightnessController controller,
        AppSettings appSettings,
        AppSettingsStore appSettingsStore,
        TraySettings traySettings,
        TraySettingsStore traySettingsStore,
        AutomationEngine? automationEngine,
        IdleEngine? idleEngine,
        AccentColorService? accentColorService,
        TrayService? trayService,
        Action onExitRequested)
    {
        _controller = controller;
        _appSettings = appSettings;
        _appSettingsStore = appSettingsStore;
        _traySettings = traySettings;
        _traySettingsStore = traySettingsStore;
        _automationEngine = automationEngine;
        _idleEngine = idleEngine;
        _accentColorService = accentColorService;
        _trayService = trayService;
        _onExitRequested = onExitRequested;
        InitializeComponent();
        BuildContent();
        SetupCloseButton();

        // Ведёт себя как всплывающее меню трея: закрывается, стоит только кликнуть
        // мимо — а не как обычное окно настроек, которое остаётся открытым.
        // _suppressDeactivateClose снимает это на время показа дочернего диалога
        // (см. ColorPickerWindow) — иначе открытие диалога само по себе забирает
        // фокус ОС у этого окна, оно считается "деактивированным" и тут же
        // закрывается, из-за чего казалось, что всё приложение исчезает.
        //
        // _themePreviewWindow (FP13) — та же проблема, но окно НЕМОДАЛЬНОЕ и
        // должно жить долго (пока пользователь крутит настройки рядом), а не
        // на краткий момент одного диалога, поэтому проверяется отдельно, а не
        // через _suppressDeactivateClose (тот включается/выключается только
        // вокруг Show/ShowDialog конкретного вызова).
        Deactivated += (_, _) =>
        {
            if (!_suppressDeactivateClose && _themePreviewWindow is null)
            {
                Close();
            }
        };
    }

    private bool _suppressDeactivateClose;

    // Окно без рамки (WindowDecorations="None"), поэтому своего крестика у него нет —
    // рисуем свой: красный фон и белый крестик всегда, а при наведении крестик
    // становится жирнее и фон сменяется диагональным переливом красный→белый.
    // Обычная Avalonia Button поверх любого заданного фона рисует свой полупрозрачный
    // оверлей наведения из темы (отсюда "чёрное/прозрачное пятно" вместо градиента),
    // поэтому здесь используется Border — у него нет встроенного состояния наведения,
    // и заданный фон отображается ровно так, как задан.
    private void SetupCloseButton()
    {
        var closeButton = this.FindControl<Border>("CloseButton")!;
        var glyph = this.FindControl<TextBlock>("CloseButtonGlyph")!;

        // Случайный клик закрывает всё приложение (не просто окно настроек) — легко
        // промахнуться мимо более безобидной цели. Требуем зажатый Shift как
        // защиту от случайного закрытия.
        ToolTip.SetTip(closeButton, "Удерживайте Shift и кликните, чтобы выйти из программы");

        var red = Avalonia.Media.Color.FromRgb(0xE8, 0x11, 0x23);
        var solidRed = new Avalonia.Media.SolidColorBrush(red);

        // Ширина белой полосы "блика" в долях ширины диагонали кнопки.
        const double bandWidth = 0.28;
        var sweepGradient = new Avalonia.Media.LinearGradientBrush
        {
            // Из правого верхнего угла в левый нижний.
            StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new Avalonia.Media.GradientStop(red, 0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Colors.White, 0),
                new Avalonia.Media.GradientStop(red, 0),
            },
        };

        closeButton.Background = solidRed;
        glyph.Foreground = Avalonia.Media.Brushes.White;
        glyph.FontWeight = Avalonia.Media.FontWeight.Normal;
        closeButton.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

        DispatcherTimer? sweepTimer = null;
        var progress = 0.0;

        // Быстрые повторные наведения/уходы мышью не должны дёргать анимацию туда-сюда:
        // если блик уже бежит — даём ему доиграть до конца, не перезапуская и не обрывая
        // резко при уходе курсора. PointerEntered/PointerExited влияют только на жирность
        // крестика, которая мгновенна и не может выглядеть "дёргано".
        closeButton.PointerEntered += (_, _) =>
        {
            glyph.FontWeight = Avalonia.Media.FontWeight.Bold;

            if (sweepTimer is not null)
            {
                return;
            }

            progress = 0.0;
            closeButton.Background = sweepGradient;
            sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            sweepTimer.Tick += (_, _) =>
            {
                progress += 0.06;
                if (progress >= 1.0)
                {
                    sweepTimer?.Stop();
                    sweepTimer = null;
                    closeButton.Background = solidRed;
                    return;
                }

                // Полоса въезжает с одного угла (центр < 0) и выезжает за противоположный
                // (центр > 1) — на краях кнопка целиком красная, в середине пути виден блик.
                var center = -bandWidth + progress * (1 + 2 * bandWidth);
                sweepGradient.GradientStops[0].Offset = Math.Clamp(center - bandWidth, 0, 1);
                sweepGradient.GradientStops[1].Offset = Math.Clamp(center, 0, 1);
                sweepGradient.GradientStops[2].Offset = Math.Clamp(center + bandWidth, 0, 1);
            };
            sweepTimer.Start();
        };
        closeButton.PointerExited += (_, _) =>
        {
            glyph.FontWeight = Avalonia.Media.FontWeight.Normal;
        };
        closeButton.PointerPressed += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _onExitRequested();
            }
            else
            {
                // Обычный клик — просто скрывает окно настроек, как и клик мимо.
                Close();
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Прицеплено к иконке трея (как GlobalSliderPopup), а не по центру монитора
    // клика — FP9 Фаза 6: центрирование по монитору для окна настроек "всё ещё
    // не устраивало" пользователя после того, как левый клик забрал себе поповер.
    // Сама сторона/сторона панели — общая логика, см. TrayPopupPlacement (её же
    // использует GlobalSliderPopup и BrightnessHudWindow).
    public void ShowNearIcon(MonitorBounds iconRect)
    {
        if (!IsVisible)
        {
            Show();
        }

        Position = TrayPopupPlacement.Compute(Screens, iconRect.X, iconRect.Y, iconRect.Width, iconRect.Height, (int)Width, (int)Height);
    }

    private void BuildContent()
    {
        _rootCategories = new List<NavCategory>
        {
            new("Настройки", new List<NavSubcategory>
            {
                new("Слайдеры яркости", BuildSliderStepTab),
                new("Ярлык трея", BuildTrayTab),
                new("Оформление", BuildAppearanceTab),
            }),
            new("Автоматизация", new List<NavSubcategory>
            {
                new("Правила", BuildAutomationTab),
                new("Простой", BuildIdleTab),
            }),
        };

        SetupNavFlip();
        SetupContentPanelBlur();
        SelectSubcategory(_rootCategories[0].Subcategories[0]);
    }

    // Клик по совсем пустому месту вкладки (не по конкретному контролу) сам по
    // себе никуда фокус не переводит — Avalonia не "уводит в никуда" фокус с
    // текстового поля просто потому, что кликнули мимо. e.Source сравнивается
    // именно с самим ContentPanel — событие доходит и от кликов по дочерним
    // Border/TextBlock (у них своих обработчиков нет), но у них e.Source будет
    // ЭТОТ дочерний элемент, а не панель, так что реальные ряды настроек клик
    // не перехватывают.
    private void SetupContentPanelBlur()
    {
        var contentPanel = this.FindControl<StackPanel>("ContentPanel")!;
        contentPanel.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, contentPanel))
            {
                contentPanel.Focus();
            }
        };
    }

    // Кнопка сверху списка перекидывает сам список категорий между правым и левым
    // краем окна. Раньше была стрелкой ("←"/"→") — но выглядела так же, как кнопка
    // "Назад" в подкатегориях, путала. Теперь — маленькая иконка-диаграмма макета
    // (два блока: узкая полоса-панель + широкая область), показывающая ТЕКУЩУЮ
    // сторону списка, а не направление клика — см. BuildLayoutFlipIcon.
    private void SetupNavFlip()
    {
        var grid = this.FindControl<Grid>("NavContentGrid")!;
        var navBorder = this.FindControl<Border>("NavBorder")!;
        var contentScroll = (Control)grid.Children.First(c => c is ScrollViewer);
        var flipButton = this.FindControl<Button>("NavFlipButton")!;

        flipButton.Content = BuildLayoutFlipIcon(_navOnRight);
        ToolTip.SetTip(flipButton, "Переместить список категорий на другую сторону окна");

        flipButton.Click += (_, _) =>
        {
            _navOnRight = !_navOnRight;

            // Раньше менялся только Grid.Column у детей, а сами ColumnDefinitions
            // оставались "*,Auto" всегда — при переносе налево список попадал в
            // "резиновую" звёздочную колонку вместо колонки под свой фиксированный
            // размер и не прижимался к углу, а "плавал". Колонки нужно переставлять
            // местами вместе с детьми, а не только менять им индекс.
            grid.ColumnDefinitions = _navOnRight
                ? new ColumnDefinitions("*,Auto")
                : new ColumnDefinitions("Auto,*");
            Grid.SetColumn(navBorder, _navOnRight ? 1 : 0);
            Grid.SetColumn(contentScroll, _navOnRight ? 0 : 1);
            navBorder.HorizontalAlignment = _navOnRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            flipButton.Content = BuildLayoutFlipIcon(_navOnRight);
        };
    }

    // Маленькая диаграмма окна: внешняя рамка + внутренняя перегородка, узкая
    // закрашенная полоса — там, где СЕЙЧАС находится список категорий (не куда он
    // поедет по клику, а где он есть прямо сейчас) — так пользователь всегда видит
    // текущий макет, а не гадает по стрелке.
    private static Control BuildLayoutFlipIcon(bool navOnRight)
    {
        var outline = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x90, 0x90, 0x90));

        var grid = new Grid
        {
            ColumnDefinitions = navOnRight ? new ColumnDefinitions("*,Auto") : new ColumnDefinitions("Auto,*"),
        };
        var panel = new Border { Width = 5, Background = outline };
        Grid.SetColumn(panel, navOnRight ? 1 : 0);
        grid.Children.Add(panel);

        return new Border
        {
            Width = 18,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            BorderBrush = outline,
            BorderThickness = new Thickness(1.3),
            Child = grid,
        };
    }

    private void RenderNavList()
    {
        var navList = this.FindControl<StackPanel>("NavList")!;
        navList.Children.Clear();

        if (_selectedCategory is null)
        {
            foreach (var category in _rootCategories)
            {
                navList.Children.Add(BuildNavRow(category.Title, isActive: false, () =>
                {
                    _selectedCategory = category;
                    SelectSubcategory(category.Subcategories[0]);
                }));
            }

            return;
        }

        navList.Children.Add(BuildNavRow("← Назад", isActive: false, () =>
        {
            _selectedCategory = null;
            RenderNavList();
        }));
        navList.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

        foreach (var subcategory in _selectedCategory.Subcategories)
        {
            var isActive = ReferenceEquals(subcategory, _selectedSubcategory);
            navList.Children.Add(BuildNavRow(subcategory.Title, isActive, () => SelectSubcategory(subcategory)));
        }
    }

    // Пункт списка навигации — Border+TextBlock вместо Button: список категорий
    // должен читаться именно как СПИСОК с одним акцентно закрашенным на всю
    // строку активным пунктом (см. референс Volumey settings panel), а не как
    // набор одинаковых кнопок без разницы между активным/неактивным состоянием
    // (FP12, "непонятно куда ты заходишь"). Цвета — через GetResourceObservable,
    // а не разовый снимок ресурса, чтобы подсветка не "залипала" на старом
    // акцентном цвете при live-смене акцента Windows.
    private Border BuildNavRow(string title, bool isActive, Action onClick)
    {
        var text = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isActive ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
        };
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(isActive ? "AppAccentOnBrush" : "AppInk"));

        var row = new Border
        {
            Child = text,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (isActive)
        {
            row.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppAccentBrush"));
        }
        else
        {
            row.Background = Avalonia.Media.Brushes.Transparent;
            // FP13: приглушённый акцентный тинт вместо нейтрального
            // AppSurfaceHover — та же роль (AppAccentBackgroundBrush), что и
            // у фона карточки Compact Bar (BrightnessHudWindow), для единого
            // ощущения "это подсвечено акцентом", а не просто "это серее".
            //
            // ВАЖНО: Bind() создаёт ЖИВУЮ подписку на ресурс — раньше (когда
            // цвет наведения был статичным AppSurfaceHover) её не отключали,
            // это было безобидно. Теперь AppAccentBackgroundBrush меняется
            // при переключении настроек акцента (SwapAccentRoles, чекбокс
            // Windows-акцента и т.п.) — если не отключить старую подписку
            // явно, она продолжает жить и переписывает Background обратно на
            // акцентный тон при следующей смене ресурса, ДАЖЕ ЕСЛИ курсор уже
            // давно ушёл с этого пункта (сложный баг, найденный пользователем:
            // "навигация по категориям, потом переключение акцента").
            IDisposable? hoverBinding = null;
            row.PointerEntered += (_, _) => hoverBinding = row.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppAccentBackgroundBrush"));
            row.PointerExited += (_, _) =>
            {
                hoverBinding?.Dispose();
                hoverBinding = null;
                row.Background = Avalonia.Media.Brushes.Transparent;
            };
        }

        row.PointerPressed += (_, _) => onClick();

        return row;
    }

    // Единица измерения встроена как InnerRightContent, а НЕ через литерал в
    // FormatString ("0 'мин'") — тот подход смешивал единицу с редактируемым
    // текстом самого поля: пользователь мог случайно стереть/повредить "мин"
    // при ручном вводе, и это же ломало commit по Enter/клику мимо поля
    // (парсинг спотыкался о оставшиеся обрывки суффикса). InnerRightContent —
    // отдельный визуальный элемент внутри рамки, не участвующий в
    // редактируемом Text/Value вообще, поэтому не мешает ни вводу, ни commit.
    private NumericUpDown BuildNumericStepper(decimal minimum, decimal maximum, decimal value, string unit)
    {
        var suffix = new TextBlock
        {
            Text = unit,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        suffix.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));

        var stepper = new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = 150,
            FormatString = "0",
            InnerRightContent = suffix,
        };

        // Встроенный коммит текста у NumericUpDown ненадёжен (ни Enter, ни клик
        // мимо поля не применяли набранное значение на практике) — коммитим
        // вручную: парсим Text и выставляем Value сами. Невалидный текст (или
        // пустое поле) откатывается обратно к текущему Value, а не оставляет
        // "битую" строку в поле.
        void CommitText()
        {
            if (decimal.TryParse(stepper.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var parsed))
            {
                stepper.Value = Math.Clamp(parsed, stepper.Minimum, stepper.Maximum);
            }
            else
            {
                stepper.Text = stepper.Value?.ToString("0", System.Globalization.CultureInfo.CurrentCulture);
            }
        }

        stepper.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                // Window как цель фокуса не подходит (фокус реально не уходит
                // с текстового поля — курсор-каретка и выделение остаются на
                // месте). ContentPanel сделан Focusable в XAML специально под
                // это — реальная фокусируемая, но не текстовая цель.
                this.FindControl<StackPanel>("ContentPanel")?.Focus();
                e.Handled = true;
            }
        };
        stepper.LostFocus += (_, _) => CommitText();

        return stepper;
    }

    private void SelectSubcategory(NavSubcategory subcategory)
    {
        _selectedSubcategory = subcategory;
        RenderNavList();

        var contentPanel = this.FindControl<StackPanel>("ContentPanel")!;
        contentPanel.Children.Clear();
        subcategory.BuildContent(contentPanel);
    }

    // Всё, что осталось от бывшей вкладки "Мониторы" — сами слайдеры яркости
    // переехали в поповер трея по левому клику (FP9 Фаза 2, GlobalSliderPopup).
    // FP17 Фаза 4, п.9 — заголовок "Шаг слайдеров" убран: он только дублировал
    // подпись единственной строки ниже (сама вкладка и так называется
    // "Слайдеры яркости" в боковом меню). Подсказка перенесена на подпись.
    private void BuildSliderStepTab(StackPanel root)
    {
        var sliderStepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var sliderStepLabel = new TextBlock { Text = "Шаг слайдера:", VerticalAlignment = VerticalAlignment.Center, Width = 150 };
        ToolTip.SetTip(sliderStepLabel, "Слайдеры в поповере трея (левый клик по иконке) \"прилипают\" к этому шагу — отдельно от шага скролла над иконкой трея (см. \"Ярлык трея\").");
        sliderStepRow.Children.Add(sliderStepLabel);
        var sliderStepUpDown = BuildNumericStepper(1, 50, _appSettings.SliderStepPercent, "%");
        sliderStepUpDown.ValueChanged += (_, _) =>
        {
            var value = (int)(sliderStepUpDown.Value ?? 5);
            _appSettings.SliderStepPercent = value;
            _appSettingsStore.Save(_appSettings);
        };
        sliderStepRow.Children.Add(sliderStepUpDown);
        root.Children.Add(sliderStepRow);
    }

    // FP17 Фаза 4, п.10 — заголовок "Скролл над иконкой трея" убран (дублировал
    // текст чекбокса), чекбокс и степпер шага объединены в одну строку вместо
    // трёх отдельных элементов. Подсказка на чекбоксе теперь ещё и объясняет,
    // что такое "скролл" в этом контексте (раньше это нигде не поянялось).
    private void BuildTrayTab(StackPanel root)
    {
        var scrollRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var enabledCheckBox = new CheckBox { Content = "Включить скролл над иконкой трея", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(enabledCheckBox, "Колесо мыши над иконкой трея в системном лотке меняет яркость без открытия поповера — этот шаг настраивает, на сколько процентов меняется яркость за один щелчок колеса.");
        enabledCheckBox.IsChecked = _traySettings.IsScrollEnabled;
        enabledCheckBox.IsCheckedChanged += (_, _) =>
        {
            _traySettings.IsScrollEnabled = enabledCheckBox.IsChecked ?? true;
            _traySettingsStore.Save(_traySettings);
        };
        scrollRow.Children.Add(enabledCheckBox);

        scrollRow.Children.Add(new TextBlock { Text = "Шаг:", VerticalAlignment = VerticalAlignment.Center });
        var stepUpDown = BuildNumericStepper(1, 50, _traySettings.ScrollStepPercent, "%");
        stepUpDown.ValueChanged += (_, _) =>
        {
            _traySettings.ScrollStepPercent = (int)(stepUpDown.Value ?? 10);
            _traySettingsStore.Save(_traySettings);
        };
        scrollRow.Children.Add(stepUpDown);
        root.Children.Add(scrollRow);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        var stickyHeader = new TextBlock { Text = "\"Липкие\" значения", FontWeight = Avalonia.Media.FontWeight.Bold };
        ToolTip.SetTip(stickyHeader, "При скролле яркость на этих значениях ненадолго задерживается (один щелчок " +
            "колеса), чтобы легко было попасть точно в них. Остальные проценты по-прежнему доступны без ограничений.");
        root.Children.Add(stickyHeader);

        // FP17 Фаза 4, п.8 — компактные "чипы" в `UniformGrid` вместо карточек на
        // всю ширину, число колонок подбирается тем же `ComputeOptimalColumns`,
        // что уже уравнивает заполненность строк у галереи форм иконки трея
        // (согласовано с пользователем: "как со списком иконок приложения").
        // Тот же расчёт доступной ширины, что и в BuildTrayIconSection
        // (availableGalleryWidth) — окно фиксированного размера (760px), но
        // константа локальна для того метода, поэтому пересчитана здесь же.
        const int availableStickyWidth = 760 - 2 - 170 - 32 - 18;
        const int chipTotalWidth = 64 + 6; // сам чип (MinWidth) + Margin(0,0,6,6)
        var maxStickyColumns = Math.Max(1, availableStickyWidth / chipTotalWidth);
        var stickyGrid = new UniformGrid();
        var stickyEmptyText = new TextBlock { Text = "(пока не задано ни одного значения)", FontStyle = Avalonia.Media.FontStyle.Italic, IsVisible = false };

        void RefreshStickyList()
        {
            stickyGrid.Children.Clear();
            var values = _traySettings.StickyValues.OrderBy(v => v).ToList();
            stickyGrid.Columns = ComputeOptimalColumns(values.Count, maxStickyColumns);
            stickyEmptyText.IsVisible = values.Count == 0;

            foreach (var value in values)
            {
                // FP17 Фаза 2/4 — та же карточка-стена, что и раньше (удаление
                // встроено в правый край), просто сужена до компактного чипа.
                var card = new Border
                {
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(15),
                    ClipToBounds = true,
                    Height = 30,
                    MinWidth = 64,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                card.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));

                var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,22") };

                var valueText = new TextBlock
                {
                    Text = $"{value}%",
                    FontSize = 12,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                Grid.SetColumn(valueText, 0);

                var deleteGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var deleteButton = BuildSweepWallButton(
                    BuildCrossGlyph(9, 1.6, deleteGlyphBrush),
                    deleteGlyphBrush,
                    glyphRestColor: ResolveThemeColor("AppMuted", Avalonia.Media.Color.Parse("#948FA3")),
                    glyphHoverColor: ResolveThemeColor("AppDangerInk", Avalonia.Media.Colors.White),
                    cornerRadius: new CornerRadius(0, 14, 14, 0),
                    restColor: ResolveThemeColor("AppNeutralRest", Avalonia.Media.Color.FromArgb(0x0D, 0x94, 0x8F, 0xA3)),
                    solidColor: ResolveThemeColor("AppDanger", Avalonia.Media.Color.Parse("#E85D6B")),
                    streakColor: ResolveThemeColor("AppDangerInk", Avalonia.Media.Colors.White),
                    sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
                    isEnabled: true,
                    tooltip: "Удалить",
                    onClick: () =>
                    {
                        _traySettings.StickyValues.Remove(value);
                        _traySettingsStore.Save(_traySettings);
                        RefreshStickyList();
                    });
                Grid.SetColumn(deleteButton, 1);

                cardGrid.Children.Add(valueText);
                cardGrid.Children.Add(deleteButton);
                card.Child = cardGrid;

                stickyGrid.Children.Add(card);
            }
        }

        RefreshStickyList();
        root.Children.Add(stickyGrid);
        root.Children.Add(stickyEmptyText);

        // FP17 Фаза 4, п.7 — одна строка (подпись + степпер + круглая кнопка "+")
        // вместо подписи/степпера/широкой кнопки друг под другом. Тот же стиль
        // круглой кнопки, что уже используется для добавления своего цвета
        // иконки трея (см. addButton в RefreshColorRow).
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };
        addRow.Children.Add(new TextBlock { Text = "Новое значение:", VerticalAlignment = VerticalAlignment.Center });
        var addValueInput = BuildNumericStepper(0, 100, 50, "%");
        addRow.Children.Add(addValueInput);

        var addGlyphBrush = new Avalonia.Media.SolidColorBrush();
        var addButton = BuildSweepWallButton(
            BuildPlusGlyph(14, 2, addGlyphBrush),
            addGlyphBrush,
            glyphRestColor: ResolveThemeColor("AppMuted", Avalonia.Media.Color.Parse("#948FA3")),
            glyphHoverColor: ResolveThemeColor("AppInk", Avalonia.Media.Color.Parse("#F1EEF7")),
            cornerRadius: new CornerRadius(14),
            restColor: ResolveThemeColor("AppNeutralRest", Avalonia.Media.Color.FromArgb(0x0D, 0x94, 0x8F, 0xA3)),
            solidColor: ResolveThemeColor("AppNeutralHover", Avalonia.Media.Color.FromArgb(0x24, 0x94, 0x8F, 0xA3)),
            streakColor: Avalonia.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
            sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
            sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
            isEnabled: true,
            tooltip: "Добавить как липкое значение",
            onClick: () =>
            {
                var value = (int)(addValueInput.Value ?? 50);
                if (!_traySettings.StickyValues.Contains(value))
                {
                    _traySettings.StickyValues.Add(value);
                    _traySettingsStore.Save(_traySettings);
                    RefreshStickyList();
                }
            });
        addButton.Width = 28;
        addButton.Height = 28;
        addButton.BorderThickness = new Thickness(1);
        addButton.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x60, 0x80, 0x80, 0x80));
        addRow.Children.Add(addButton);
        root.Children.Add(addRow);
    }

    // FP12 Фаза 4, п.8 — "+"/"×" рисуются векторной геометрией (Line), а не
    // TextBlock: TextBlock центрирует текст по LINE BOX шрифта (полная высота
    // ascent+descent), а не по фактическим закрашенным пикселям конкретного
    // глифа — у символов "+"/"×" реальная "чернильная" область заметно уже и
    // расположена не строго по центру line box, из-за чего центрирование по
    // умолчанию давало видимое смещение влево-вниз. Линии центрируются по
    // РЕАЛЬНОЙ геометрии фигуры, поэтому не "плавают" в зависимости от шрифта.
    internal static Control BuildPlusGlyph(double size, double thickness, Avalonia.Media.IBrush stroke)
    {
        var half = size / 2;
        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(half, 0),
            EndPoint = new Point(half, size),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(0, half),
            EndPoint = new Point(size, half),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        return canvas;
    }

    private static Control BuildCrossGlyph(double size, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(size, size),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(size, 0),
            EndPoint = new Point(0, size),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        return canvas;
    }

    // Векторный шеврон вниз вместо текстового символа "▼" — тот, как выяснилось
    // (FP10, скриншот пользователя), либо не рисовался шрифтом кнопки вовсе, либо
    // съезжал в крошечную точку в узкой 32px кнопке. Та же причина, по которой
    // плюс/крестик выше нарисованы линиями, а не текстовыми глифами.
    // internal (не private) — переиспользуется из GlobalSliderPopup (FP17 Фаза 4,
    // п.4: те же векторные шевроны/линии вместо текстовых "▼"/"▲"/"−"/"+", что уже
    // применены здесь для карточки правил FP10 и других мест этого файла).
    internal static Control BuildChevronDownGlyph(double width, double height, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Polyline
        {
            Points = new Avalonia.Points { new Point(0, 0), new Point(width / 2, height), new Point(width, 0) },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        });
        return canvas;
    }

    // Зеркальный шеврон вверх — для кнопки "▲" в стене карточки правила (FP10).
    internal static Control BuildChevronUpGlyph(double width, double height, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Polyline
        {
            Points = new Avalonia.Points { new Point(0, height), new Point(width / 2, 0), new Point(width, height) },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        });
        return canvas;
    }

    // Горизонтальные шевроны — та же форма, что вверх/вниз, повёрнутая на 90°
    // (FP17: замена текстовых "◀"/"▶" в галерее форм иконки трея — векторный
    // глиф, не текстовый символ, та же причина, что и с "▼" в другом месте).
    private static Control BuildChevronLeftGlyph(double width, double height, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Polyline
        {
            Points = new Avalonia.Points { new Point(width, 0), new Point(0, height / 2), new Point(width, height) },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        });
        return canvas;
    }

    private static Control BuildChevronRightGlyph(double width, double height, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Polyline
        {
            Points = new Avalonia.Points { new Point(0, 0), new Point(width, height / 2), new Point(0, height) },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round,
        });
        return canvas;
    }

    // Упрощённый "карандаш" для кнопки "Изменить" в стене карточки правила
    // (FP10) — тот же язык, что у плюса/крестика/шеврона выше: несколько линий
    // на Canvas, а не текстовый глиф или сложная SVG-геометрия.
    // FP17 Фаза 2, п.5 — векторная линия вместо текстового "−" у кнопки "скрыть
    // процесс" (та же причина, что и у остальных глифов: текстовый символ мог
    // не отрисоваться шрифтом кнопки, как уже было с "▼"). Без блика-анимации
    // — решено избыточным для переходного popup автодополнения.
    internal static Control BuildMinusGlyph(double size, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(0, size / 2),
            EndPoint = new Point(size, size / 2),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        return canvas;
    }

    private static Control BuildEditGlyph(double size, double thickness, Avalonia.Media.IBrush stroke)
    {
        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(size * 0.08, size * 0.92),
            EndPoint = new Point(size * 0.62, size * 0.38),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(size * 0.62, size * 0.38),
            EndPoint = new Point(size * 0.9, size * 0.1),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(size * 0.04, size * 0.96),
            EndPoint = new Point(size * 0.16, size * 0.84),
            Stroke = stroke,
            StrokeThickness = thickness * 1.4,
            StrokeLineCap = Avalonia.Media.PenLineCap.Round,
        });
        return canvas;
    }

    // FP10 — блик, вживлённый в стену карточки правила ("Изменить"/"Удалить"/
    // "▲"/"▼"): та же техника, что уже используется кнопкой закрытия программы
    // (SetupCloseButton) — ручной DispatcherTimer двигает офсеты GradientStops
    // диагонального (или вертикального) LinearGradientBrush, а не CSS-подобный
    // transition/keyframes (тех в Avalonia просто нет). В отличие от кнопки
    // закрытия — при уходе курсора эта кнопка возвращается в состояние покоя
    // (закрытие остаётся красным всегда, тут это не нужно).
    // glyphBrush — общая кисть у САМОГО глифа (Line/Polyline внутри glyph):
    // передаётся отдельно, чтобы можно было перекрасить контур синхронно с
    // фоном (Line.Stroke не читается обратно из Control, проще держать
    // отдельную ссылку на мутируемую SolidColorBrush).
    private static Border BuildSweepWallButton(
        Control glyph,
        Avalonia.Media.SolidColorBrush glyphBrush,
        Avalonia.Media.Color glyphRestColor,
        Avalonia.Media.Color glyphHoverColor,
        CornerRadius cornerRadius,
        Avalonia.Media.Color restColor,
        Avalonia.Media.Color solidColor,
        Avalonia.Media.Color streakColor,
        RelativePoint sweepStart,
        RelativePoint sweepEnd,
        bool isEnabled,
        string tooltip,
        Action onClick)
    {
        glyphBrush.Color = isEnabled ? glyphRestColor : glyphHoverColor;
        var restBrush = new Avalonia.Media.SolidColorBrush(restColor);

        var button = new Border
        {
            CornerRadius = cornerRadius,
            Background = restBrush,
            Child = glyph,
            Cursor = new Avalonia.Input.Cursor(isEnabled ? Avalonia.Input.StandardCursorType.Hand : Avalonia.Input.StandardCursorType.No),
            Opacity = isEnabled ? 1.0 : 0.3,
        };
        ToolTip.SetTip(button, tooltip);

        if (!isEnabled)
        {
            glyphBrush.Color = glyphRestColor;
            return button;
        }

        var solidBrush = new Avalonia.Media.SolidColorBrush(solidColor);
        var sweepGradient = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = sweepStart,
            EndPoint = sweepEnd,
            GradientStops =
            {
                new Avalonia.Media.GradientStop(solidColor, 0),
                new Avalonia.Media.GradientStop(streakColor, 0),
                new Avalonia.Media.GradientStop(solidColor, 0),
            },
        };

        const double bandWidth = 0.28;
        DispatcherTimer? sweepTimer = null;
        var progress = 0.0;

        button.PointerEntered += (_, _) =>
        {
            glyphBrush.Color = glyphHoverColor;

            if (sweepTimer is not null)
            {
                return;
            }

            progress = 0.0;
            button.Background = sweepGradient;
            sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            sweepTimer.Tick += (_, _) =>
            {
                progress += 0.06;
                if (progress >= 1.0)
                {
                    sweepTimer?.Stop();
                    sweepTimer = null;
                    button.Background = solidBrush;
                    return;
                }

                var center = -bandWidth + progress * (1 + 2 * bandWidth);
                sweepGradient.GradientStops[0].Offset = Math.Clamp(center - bandWidth, 0, 1);
                sweepGradient.GradientStops[1].Offset = Math.Clamp(center, 0, 1);
                sweepGradient.GradientStops[2].Offset = Math.Clamp(center + bandWidth, 0, 1);
            };
            sweepTimer.Start();
        };
        button.PointerExited += (_, _) =>
        {
            glyphBrush.Color = glyphRestColor;
            sweepTimer?.Stop();
            sweepTimer = null;
            button.Background = restBrush;
        };
        button.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    // FP13 — свой акцентный цвет, виден только пока чекбокс Windows-акцента
    // снят (иначе базовый цвет и так берётся из системы, выбирать нечего).
    // Переиспользует тот же ColorPickerWindow, что и свой цвет иконки трея
    // (FP8) — только по подтверждению ("Изменить" → диалог → OK), не вживую
    // по ходу перетаскивания слайдеров внутри пикера.
    private Control BuildCustomAccentSection()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        var initialColor = Avalonia.Media.Color.TryParse(_appSettings.CustomAccentColorHex, out var parsedInitial)
            ? parsedInitial
            : Avalonia.Media.Color.FromRgb(0x00, 0x78, 0xD4);

        var swatch = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = new Avalonia.Media.SolidColorBrush(initialColor),
        };
        swatch.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppLineStrong"));

        // FP17 Фаза 2 — то же перо, что и у "Изменить" в карточке правил (FP10),
        // просто в масштабе под эту кнопку — не встроено в стену (тут нет
        // карточки-силуэта, как у списка правил/галереи), поэтому кнопка
        // целиком круглая, в тон свотчу рядом с ней.
        var editGlyphBrush = new Avalonia.Media.SolidColorBrush();
        var changeButton = BuildSweepWallButton(
            BuildEditGlyph(13, 1.8, editGlyphBrush),
            editGlyphBrush,
            glyphRestColor: ResolveThemeColor("AppMuted", Avalonia.Media.Color.Parse("#948FA3")),
            glyphHoverColor: ResolveThemeColor("AppWarningInk", Avalonia.Media.Color.Parse("#241C02")),
            cornerRadius: new CornerRadius(14),
            restColor: ResolveThemeColor("AppNeutralRest", Avalonia.Media.Color.FromArgb(0x0D, 0x94, 0x8F, 0xA3)),
            solidColor: ResolveThemeColor("AppWarning", Avalonia.Media.Color.Parse("#E8C23D")),
            streakColor: Avalonia.Media.Colors.White,
            sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
            sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
            isEnabled: true,
            tooltip: "Изменить",
            onClick: async () =>
            {
                _suppressDeactivateClose = true;
                try
                {
                    var picker = new ColorPickerWindow(_appSettings.CustomAccentColorHex);
                    await picker.ShowDialog(this);

                    if (picker.ResultHex is { } hex)
                    {
                        swatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex));
                        _accentColorService?.SetCustomAccentColor(hex);
                    }
                }
                finally
                {
                    _suppressDeactivateClose = false;
                }
            });
        changeButton.Width = 28;
        changeButton.Height = 28;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = "Свой акцентный цвет:", VerticalAlignment = VerticalAlignment.Center, Width = 180 });
        row.Children.Add(swatch);
        row.Children.Add(changeButton);
        panel.Children.Add(row);

        return panel;
    }

    private ComboBox BuildColorHarmonySelector()
    {
        var options = new (ColorHarmonyScheme Value, string Label)[]
        {
            (ColorHarmonyScheme.Monochromatic, "Монохромная"),
            (ColorHarmonyScheme.AnalogousClose, "Соседняя, узкая"),
            (ColorHarmonyScheme.Analogous, "Соседняя"),
            (ColorHarmonyScheme.AnalogousWide, "Соседняя, широкая"),
        };

        var current = Enum.TryParse<ColorHarmonyScheme>(_appSettings.ColorHarmonySchemeId, out var parsedScheme)
            ? parsedScheme
            : ColorHarmonyScheme.Analogous;

        var comboBox = new ComboBox
        {
            ItemsSource = options.Select(o => o.Label).ToList(),
            SelectedIndex = Array.FindIndex(options, o => o.Value == current),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedIndex < 0)
            {
                return;
            }

            _accentColorService?.SetColorHarmonyScheme(options[comboBox.SelectedIndex].Value);
        };

        return comboBox;
    }

    // FP13 — открывает окно предпросмотра темы НЕМОДАЛЬНО (Show, не
    // ShowDialog), рядом с SettingsWindow: пользователь должен иметь
    // возможность крутить слайдеры/комбобоксы настроек и сразу видеть эффект
    // в предпросмотре, не закрывая ни то, ни другое окно. Повторный клик на
    // кнопку активирует уже открытое окно вместо создания дубликата.
    private void OpenThemePreview()
    {
        if (_themePreviewWindow is not null)
        {
            _themePreviewWindow.Activate();
            return;
        }

        _themePreviewWindow = new ThemePreviewWindow();
        _themePreviewWindow.Closed += (_, _) => _themePreviewWindow = null;
        _themePreviewWindow.Show(this);
    }

    // FP17 Фаза 4, п.11/13 — "Тема" и "HUD с процентом" были заголовком над
    // ComboBox на двух строках, теперь одна строка "Подпись: [ComboBox]" —
    // тот же приём, что уже используется чуть ниже для "Цветовая комбинация:".
    private void BuildAppearanceTab(StackPanel root)
    {
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        themeRow.Children.Add(new TextBlock { Text = "Тема:", VerticalAlignment = VerticalAlignment.Center, Width = 180 });
        themeRow.Children.Add(BuildThemeSelector());
        root.Children.Add(themeRow);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        var accentCheckBox = new CheckBox { Content = "Использовать акцентный цвет Windows", IsChecked = _appSettings.UseWindowsAccentColor };
        ToolTip.SetTip(accentCheckBox, "Подкрашивает выделение/акцентные элементы в цвет, который вы выбрали в Параметры Windows → Персонализация → Цвета, вместо стандартного синего.");
        root.Children.Add(accentCheckBox);

        // Комбинация — ВСЕГДА видна (действует независимо от того, откуда взят
        // основной цвет, из Windows или свой), а свой базовый цвет — только
        // пока Windows-акцент выключен (FP13).
        var colorHarmonyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        colorHarmonyRow.Children.Add(new TextBlock { Text = "Цветовая комбинация:", VerticalAlignment = VerticalAlignment.Center, Width = 180 });
        colorHarmonyRow.Children.Add(BuildColorHarmonySelector());
        ToolTip.SetTip(colorHarmonyRow, "Дополнительные акцентные элементы (например, вторые лучи HUD-солнца) получают цвет, вычисленный из основного акцента по этому правилу.");
        root.Children.Add(colorHarmonyRow);

        var customAccentSection = BuildCustomAccentSection();
        customAccentSection.IsVisible = !(accentCheckBox.IsChecked ?? true);
        root.Children.Add(customAccentSection);

        accentCheckBox.IsCheckedChanged += (_, _) =>
        {
            var enabled = accentCheckBox.IsChecked ?? true;
            _accentColorService?.SetEnabled(enabled);
            customAccentSection.IsVisible = !enabled;
        };

        var swapRolesCheckBox = new CheckBox { Content = "Поменять акцент и противоположный цвет местами", IsChecked = _appSettings.SwapAccentRoles };
        ToolTip.SetTip(swapRolesCheckBox, "Тот же набор вычисленных цветов — меняется только, какой из них применяется как основной акцент по всему приложению, а какой как противоположный.");
        swapRolesCheckBox.IsCheckedChanged += (_, _) => _accentColorService?.SetSwapAccentRoles(swapRolesCheckBox.IsChecked ?? false);
        root.Children.Add(swapRolesCheckBox);

        var previewButton = new Button { Content = "Открыть предпросмотр темы", Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(previewButton, "Отдельное немодальное окошко со сводкой элементов интерфейса — удобно держать открытым рядом с настройками для быстрой оценки сочетания цветов.");
        previewButton.Click += (_, _) => OpenThemePreview();
        root.Children.Add(previewButton);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        var hudStyleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var hudStyleLabel = new TextBlock { Text = "HUD с процентом:", VerticalAlignment = VerticalAlignment.Center, Width = 180 };
        ToolTip.SetTip(hudStyleLabel, "Всплывающее окошко с процентом, которое появляется при скролле над иконкой трея.");
        hudStyleRow.Children.Add(hudStyleLabel);
        hudStyleRow.Children.Add(BuildHudStyleSelector());
        root.Children.Add(hudStyleRow);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        root.Children.Add(new TextBlock { Text = "Иконка трея", FontWeight = Avalonia.Media.FontWeight.Bold });
        root.Children.Add(BuildTrayIconSection());
    }

    // Стиль HUD — параметрический выбор (FP12 Фаза 4, п.6), по аналогии с
    // формой иконки трея (FP8): пользователь выбирает готовый стиль вместо
    // подстройки параметров вручную. Сам HUD-window уже создан и живёт всё
    // время работы приложения (см. App.axaml.cs) — она читает
    // TraySettings.HudStyleId заново при каждом показе, отдельно уведомлять
    // её о смене настройки не нужно.
    private ComboBox BuildHudStyleSelector()
    {
        var options = new (HudStyle Value, string Label)[]
        {
            (HudStyle.GrowingRaysSun, "Растущее солнце"),
            (HudStyle.CompactBar, "Компактная шкала"),
            (HudStyle.PillToast, "Капсула"),
        };

        var currentStyle = Enum.TryParse<HudStyle>(_traySettings.HudStyleId, out var parsed) ? parsed : HudStyle.GrowingRaysSun;

        var comboBox = new ComboBox
        {
            ItemsSource = options.Select(o => o.Label).ToList(),
            SelectedIndex = Array.FindIndex(options, o => o.Value == currentStyle),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedIndex < 0)
            {
                return;
            }

            _traySettings.HudStyleId = options[comboBox.SelectedIndex].Value.ToString();
            _traySettingsStore.Save(_traySettings);
        };

        return comboBox;
    }

    // Форма, цвет и масштаб иконки трея — три независимых параметра (FP8):
    // иконка рисуется на лету (TrayIconRenderer), а не грузится из готового
    // файла, поэтому любую комбинацию можно применить сразу — без пересборки
    // и без необходимости хранить файл на каждую комбинацию. Масштаб — свой
    // на каждую форму (крутится колесом мыши над карточкой), не общий слайдер.
    // FP11 — единая карточка галереи: либо встроенная векторная форма
    // (IsCustom=false, Custom=null), либо своя импортированная растровая
    // иконка (IsCustom=true). Обе живут в ОДНОЙ галерее и используют ОДНИ И
    // ТЕ ЖЕ строково-ключевые словари TraySettings (Scale/NameOverrides/
    // Order) — задел на это был заложен ещё в FP8.
    private sealed record TrayIconCard(string Id, string BaseDisplayName, bool IsCustom, CustomTrayIcon? Custom);

    private Control BuildTrayIconSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        var mutedBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xA0, 0x80, 0x80, 0x80));
        var accentBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xF2, 0x90, 0x0C));
        var neutralBorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        var handCursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

        // FP17 Фаза 2 — те же цвета-стены, что уже у карточек правил (FP10):
        // приглушённый тон покоя, чуть светлее при наведении, палитра НЕ
        // меняется (стрелки "влево"/"вправо" — те же "move", не destructive/edit).
        var wallRestColor = ResolveThemeColor("AppNeutralRest", Avalonia.Media.Color.FromArgb(0x0D, 0x94, 0x8F, 0xA3));
        var wallHoverColor = ResolveThemeColor("AppNeutralHover", Avalonia.Media.Color.FromArgb(0x24, 0x94, 0x8F, 0xA3));
        var wallGlyphRestColor = ResolveThemeColor("AppMuted", Avalonia.Media.Color.Parse("#948FA3"));
        var wallGlyphHoverColor = ResolveThemeColor("AppInk", Avalonia.Media.Color.Parse("#F1EEF7"));
        var wallStreakColor = Avalonia.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);

        // SettingsWindow фиксированного размера (Width=760, CanResize="False" в
        // .axaml — увеличено с исходных 640 именно чтобы в галерею форм влезало
        // 4 карточки в ряд, не только 3) — доступную ширину под галерею можно
        // посчитать заранее по известным размерам разметки, не дожидаясь
        // реального прохода layout (в момент построения контента окно ещё не
        // показано, Bounds всех элементов ещё нулевые). Если разметка
        // окна/навигации изменится, эти числа нужно будет поправить вручную:
        // 760 (окно) − 2 (внешний Border BorderThickness=1×2) − 170 (NavBorder) −
        // 32 (ContentPanel Margin=16×2) − 18 (запас на вертикальный скроллбар,
        // если содержимое вкладки не помещается по высоте).
        const int availableGalleryWidth = 760 - 2 - 170 - 32 - 18;
        const int cardTotalWidth = 104 + 8; // сама карточка (см. ниже) + Margin(4) с каждой стороны
        var maxDesignColumns = Math.Max(1, availableGalleryWidth / cardTotalWidth);

        // FP11 — число колонок больше не фиксируется один раз при построении:
        // с добавлением/удалением своих иконок общее количество карточек
        // меняется, поэтому пересчитывается заново при каждом RefreshDesignGallery
        // (см. ниже), а не только исходя из числа встроенных форм.
        var designGallery = new UniformGrid();
        panel.Children.Add(new TextBlock { Text = "Форма", FontSize = 12, Foreground = mutedBrush });
        panel.Children.Add(designGallery);
        panel.Children.Add(new TextBlock
        {
            Text = "Прокрутите колесо мыши над формой, чтобы изменить её масштаб — у каждой формы он свой.",
            FontSize = 10,
            Foreground = mutedBrush,
            FontStyle = Avalonia.Media.FontStyle.Italic,
            Margin = new Thickness(0, 2, 0, 0),
        });

        // FP11 — импорт своей иконки трея (растр: PNG/ICO/BMP/JPG). Копируется В
        // СВОЮ папку (CustomTrayIconStorage) — переживает переименование/
        // перемещение/удаление исходного файла. Параметрический цвет к ней не
        // применяется (показывается "как есть"), масштаб — тот же диапазон и тот
        // же механизм (колесо мыши над карточкой), что и у встроенных форм.
        //
        // Обработчик клика подключается НИЖЕ (после ApplyLiveIcon/RefreshDesignGallery)
        // — сама кнопка создаётся и добавляется в дерево здесь же ради нужного
        // порядка в разметке, но её Click-лямбда ссылается на ещё не объявленные
        // на этом месте локальные функции/переменные (renamingDesignId и т.п.), а
        // лямбда не может форвард-ссылаться на них, в отличие от локальных функций.
        // FP17 Фаза 4, п.14 — компактная и по центру панели (была растянута на
        // всю ширину, текст выровнен слева) — согласовано с пользователем.
        var importButton = new Button
        {
            Content = "Добавить свою иконку",
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(importButton, "Подходит любое изображение в формате PNG/ICO/BMP/JPG. Для лучшего результата — квадратная картинка, желательно с прозрачным фоном (PNG). Цвет к своим иконкам не применяется, показываются как есть.");
        panel.Children.Add(importButton);
        panel.Children.Add(new TextBlock
        {
            Text = "Подходит любое изображение (PNG/ICO/BMP/JPG). Лучше всего смотрится квадратная картинка с прозрачным фоном — трей маленький, сложные и неквадратные изображения при масштабировании теряют детали.",
            FontSize = 10,
            Foreground = mutedBrush,
            FontStyle = Avalonia.Media.FontStyle.Italic,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var colorRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Цвет", FontSize = 12, Foreground = mutedBrush, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(colorRow);

        string? renamingDesignId = null;
        var isCommittingRename = false;

        // FP11 — своя иконка (Id найден среди CustomTrayIcons) рендерится из
        // файла, без параметрического цвета; иначе — прежняя логика встроенной
        // векторной формы.
        void ApplyLiveIcon()
        {
            var customIcon = _traySettings.CustomTrayIcons.FirstOrDefault(c => c.Id == _traySettings.TrayIconDesignId);
            var scale = _traySettings.GetTrayIconScale(_traySettings.TrayIconDesignId);

            System.Drawing.Icon icon;
            if (customIcon is not null)
            {
                icon = TrayIconRenderer.RenderCustom(CustomTrayIconStorage.GetFilePath(customIcon), scale);
            }
            else
            {
                if (!Enum.TryParse<TrayIconDesign>(_traySettings.TrayIconDesignId, out var design))
                {
                    design = TrayIconDesign.Spokes;
                }

                var color = System.Drawing.ColorTranslator.FromHtml(_traySettings.TrayIconColorHex);
                icon = TrayIconRenderer.Render(design, color, scale);
            }

            _trayService?.SetIcon(icon);
            _traySettingsStore.Save(_traySettings);
        }

        // Формы/свои иконки, ещё не встречавшиеся в TrayIconDesignOrder (новые,
        // добавленные уже после того как порядок сохранился), уходят в конец —
        // сначала встроенные формы в порядке каталога, потом свои иконки в
        // порядке добавления — так список остаётся стабильным и не требует
        // отдельной миграции сохранённых настроек.
        List<TrayIconCard> GetOrderedIcons()
        {
            var byId = new Dictionary<string, TrayIconCard>();
            foreach (var option in TrayIconCatalog.Designs)
            {
                var id = option.Design.ToString();
                byId[id] = new TrayIconCard(id, option.DisplayName, false, null);
            }

            foreach (var custom in _traySettings.CustomTrayIcons)
            {
                byId[custom.Id] = new TrayIconCard(custom.Id, custom.DisplayName, true, custom);
            }

            var ordered = new List<TrayIconCard>();
            foreach (var id in _traySettings.TrayIconDesignOrder)
            {
                if (byId.Remove(id, out var card))
                {
                    ordered.Add(card);
                }
            }

            foreach (var option in TrayIconCatalog.Designs)
            {
                if (byId.TryGetValue(option.Design.ToString(), out var card))
                {
                    ordered.Add(card);
                }
            }

            foreach (var custom in _traySettings.CustomTrayIcons)
            {
                if (byId.TryGetValue(custom.Id, out var card))
                {
                    ordered.Add(card);
                }
            }

            return ordered;
        }

        void MoveDesign(string designId, int direction)
        {
            var orderedIds = GetOrderedIcons().Select(o => o.Id).ToList();
            var index = orderedIds.IndexOf(designId);
            var newIndex = index + direction;
            if (newIndex < 0 || newIndex >= orderedIds.Count)
            {
                return;
            }

            (orderedIds[index], orderedIds[newIndex]) = (orderedIds[newIndex], orderedIds[index]);
            _traySettings.TrayIconDesignOrder = orderedIds;
            _traySettingsStore.Save(_traySettings);
            RefreshDesignGallery();
        }

        void RefreshDesignGallery()
        {
            designGallery.Children.Clear();
            var color = System.Drawing.ColorTranslator.FromHtml(_traySettings.TrayIconColorHex);
            var orderedIcons = GetOrderedIcons();
            // FP11 — пересчитывается каждый раз (не один раз при построении), т.к.
            // с добавлением/удалением своих иконок общее число карточек меняется.
            designGallery.Columns = ComputeOptimalColumns(orderedIcons.Count, maxDesignColumns);

            for (var designIndex = 0; designIndex < orderedIcons.Count; designIndex++)
            {
                var iconCard = orderedIcons[designIndex];
                var designId = iconCard.Id;
                var isSelected = designId == _traySettings.TrayIconDesignId;
                var scale = _traySettings.GetTrayIconScale(designId);
                var displayName = _traySettings.TrayIconDesignNameOverrides.TryGetValue(designId, out var nameOverride)
                    ? nameOverride
                    : iconCard.BaseDisplayName;

                var preview = new Image
                {
                    Width = 32,
                    Height = 32,
                    // FP11 — своя иконка рендерится из файла (без параметрического
                    // цвета — тот действует только на встроенные векторные формы).
                    Source = iconCard.IsCustom
                        ? TrayIconRenderer.RenderCustomPreview(CustomTrayIconStorage.GetFilePath(iconCard.Custom!), scale)
                        : TrayIconRenderer.RenderPreview(Enum.Parse<TrayIconDesign>(designId), color, scale),
                };

                Control nameControl;
                if (renamingDesignId == designId)
                {
                    var nameBox = new TextBox { Text = displayName, Width = 68, FontSize = 10 };
                    // Клик внутри поля ввода не должен всплыть до карточки и переключить
                    // выбор формы посреди редактирования имени.
                    nameBox.PointerPressed += (_, e) => e.Handled = true;
                    nameBox.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            CommitRename(designId, nameBox.Text);
                        }
                    };
                    nameBox.LostFocus += (_, _) => CommitRename(designId, nameBox.Text);
                    nameControl = nameBox;
                    Dispatcher.UIThread.Post(() => nameBox.Focus(), DispatcherPriority.Background);
                }
                else
                {
                    // FP11 — переименование и удаление переехали в контекстное меню
                    // по правому клику (см. card.ContextMenu ниже) — раньше карандаш
                    // и крестик были зажаты в один узкий ряд с именем ("слишком
                    // маленькая кнопка удаления", по фидбеку пользователя). Сама
                    // карточка теперь показывает только имя, без иконок-кнопок.
                    var nameText = new TextBlock
                    {
                        Text = displayName,
                        FontSize = 10,
                        MaxWidth = 68,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    };
                    ToolTip.SetTip(nameText, displayName);
                    nameControl = nameText;
                }

                var scaleText = new TextBlock { Text = $"{scale}%", FontSize = 9, Foreground = mutedBrush };

                // Сортировка — кнопки "влево/вправо" вместо drag-and-drop: проще и
                // надёжнее, тот же стиль, что и остальные явные кнопки в проекте
                // (± у процента, крестик удаления цвета). Порядок — это индекс в
                // GetOrderedIcons(), а не визуальная позиция в WrapPanel (та может
                // переноситься на новую строку независимо от логического порядка).
                //
                // Кнопки встроены в САМУ карточку по бокам (не отдельным рядом снизу):
                // узкие полосы во всю высоту, каждая скруглена только со своей стороны
                // (как угол карточки) — так они читаются как часть силуэта плитки, а
                // не как отдельные наклеенные поверх кружки.
                var canMoveLeft = designIndex > 0;
                var canMoveRight = designIndex < orderedIcons.Count - 1;

                // FP17 Фаза 2 — тот же приём, что уже у карточек правил (FP10):
                // узкая полоса встроена в стену самой карточки, векторный шеврон
                // вместо текстового "◀"/"▶", блик-волна по направлению стрелки
                // (влево → волна вправо-налево, вправо → волна влево-направо).
                var leftGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var leftArrow = BuildSweepWallButton(
                    BuildChevronLeftGlyph(6, 10, 1.8, leftGlyphBrush),
                    leftGlyphBrush,
                    glyphRestColor: wallGlyphRestColor,
                    glyphHoverColor: wallGlyphHoverColor,
                    cornerRadius: new CornerRadius(7, 0, 0, 7),
                    restColor: wallRestColor,
                    solidColor: wallHoverColor,
                    streakColor: wallStreakColor,
                    sweepStart: new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    isEnabled: canMoveLeft,
                    tooltip: "Сдвинуть влево",
                    onClick: () => MoveDesign(designId, -1));
                leftArrow.Width = 16;

                var rightGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var rightArrow = BuildSweepWallButton(
                    BuildChevronRightGlyph(6, 10, 1.8, rightGlyphBrush),
                    rightGlyphBrush,
                    glyphRestColor: wallGlyphRestColor,
                    glyphHoverColor: wallGlyphHoverColor,
                    cornerRadius: new CornerRadius(0, 7, 7, 0),
                    restColor: wallRestColor,
                    solidColor: wallHoverColor,
                    streakColor: wallStreakColor,
                    sweepStart: new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    isEnabled: canMoveRight,
                    tooltip: "Сдвинуть вправо",
                    onClick: () => MoveDesign(designId, 1));
                rightArrow.Width = 16;

                var centerContent = new StackPanel
                {
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { preview, nameControl, scaleText },
                };

                var cardLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                Grid.SetColumn(leftArrow, 0);
                Grid.SetColumn(centerContent, 1);
                Grid.SetColumn(rightArrow, 2);
                cardLayout.Children.Add(leftArrow);
                cardLayout.Children.Add(centerContent);
                cardLayout.Children.Add(rightArrow);

                var card = new Border
                {
                    Width = 104,
                    Height = 88,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    BorderBrush = isSelected ? accentBrush : neutralBorderBrush,
                    // Иначе прямоугольные боковые полосы (leftArrow/rightArrow) торчали
                    // бы за пределы скруглённых внешних углов карточки — Avalonia Border
                    // по умолчанию не обрезает содержимое по своей геометрии.
                    ClipToBounds = true,
                    Cursor = handCursor,
                    Child = cardLayout,
                };
                // FP18 — раньше было Background=Transparent (нужен был только НЕ-null
                // фон, чтобы клики в "пустых" местах карточки ловились, а не проваливались
                // сквозь неё) — из-за этого карточка визуально сливалась с фоном ВСЕГО
                // окна вместо своей собственной заливки, особенно заметно на новой,
                // более контрастной светлой палитре ("ВООБЩЕ не помогло на странице с
                // иконками" — живой фидбег). Теперь честно залита AppSurface, как и
                // остальные карточки в приложении.
                card.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));

                // FP18 — плоской разницы заливки соседних светлых тонов оказалось
                // недостаточно (живой фидбек: "вроде лучше, а вроде фигня всё-равно") —
                // на светлом конце шкалы глаз плохо различает соседние оттенки яркости,
                // сколько её ни раздвигай. Тень (elevation) добавляет ощущение глубины
                // независимо от того, насколько близки тона фона и карточки — на тёмном
                // фоне тень того же чёрного цвета остаётся почти незаметной, поэтому
                // безопасно применять без ветвления по теме. ВАЖНО: тень повешена на
                // ОТДЕЛЬНЫЙ внешний Border, а не на сам `card` — у `card` стоит
                // ClipToBounds=true (нужен для скругления углов стен leftArrow/rightArrow)
                // и он обрезал бы тень, выходящую за собственные границы.
                var cardShadowWrapper = new Border
                {
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(8),
                    BoxShadow = new Avalonia.Media.BoxShadows(new Avalonia.Media.BoxShadow
                    {
                        OffsetX = 0,
                        OffsetY = 2,
                        Blur = 6,
                        Color = Avalonia.Media.Color.FromArgb(0x30, 0, 0, 0),
                    }),
                    Child = card,
                };

                // FP11 — переименование/удаление живут в контекстном меню по
                // правому клику (по фидбеку пользователя — прежний крестик прямо
                // на карточке был "слишком маленькой кнопкой"). "Удалить" только
                // для своих иконок — у встроенных форм нет исходного файла.
                var contextMenu = new ContextMenu();
                var renameItem = new MenuItem { Header = "Переименовать" };
                renameItem.Click += (_, _) =>
                {
                    renamingDesignId = designId;
                    RefreshDesignGallery();
                };
                contextMenu.Items.Add(renameItem);

                if (iconCard.IsCustom)
                {
                    var deleteItem = new MenuItem { Header = "Удалить" };
                    deleteItem.Click += (_, _) => DeleteCustomIcon(iconCard.Custom!);
                    contextMenu.Items.Add(deleteItem);
                }

                card.ContextMenu = contextMenu;

                // Правый клик открывает контекстное меню (штатно, через ContextMenu
                // выше) — здесь реагируем ТОЛЬКО на левую кнопку, иначе выбор формы
                // менялся бы попутно и при правом клике тоже.
                card.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                    {
                        return;
                    }

                    _traySettings.TrayIconDesignId = designId;
                    ApplyLiveIcon();
                    RefreshDesignGallery();
                };

                // Масштаб — индивидуальный на каждую форму: скролл над карточкой, а не
                // общий контрол на всю галерею.
                // FP17 — раньше колесо меняло масштаб ЛЮБОЙ карточки под курсором,
                // даже "проезжающей" под ним во время прокрутки всей страницы —
                // событие перехватывалось раньше, чем долетало до внешнего
                // ScrollViewer, и страница переставала прокручиваться, а масштаб
                // менялся случайно. Теперь колесо действует только на карточку,
                // которую перед этим явно ВЫБРАЛИ кликом ("клик-фокус перед
                // взаимодействием", по решению пользователя) — остальные карточки
                // не перехватывают событие, оно спокойно уходит на прокрутку страницы.
                card.PointerWheelChanged += (_, e) =>
                {
                    if (!isSelected)
                    {
                        return;
                    }

                    e.Handled = true;
                    var current = _traySettings.GetTrayIconScale(designId);
                    var next = Math.Clamp(current + (e.Delta.Y > 0 ? 5 : -5), 100, 170);
                    _traySettings.TrayIconScaleByDesign[designId] = next;
                    ApplyLiveIcon();
                    RefreshDesignGallery();
                };

                designGallery.Children.Add(cardShadowWrapper);
            }
        }

        // FP11 — удаляет саму запись + файл + все следы её id в остальных
        // строково-ключевых словарях (Scale/NameOverrides/Order), тот же принцип
        // очистки "хвостов", что уже применяется при удалении своего цвета иконки.
        void DeleteCustomIcon(CustomTrayIcon icon)
        {
            _traySettings.CustomTrayIcons.Remove(icon);
            _traySettings.TrayIconDesignOrder.Remove(icon.Id);
            _traySettings.TrayIconScaleByDesign.Remove(icon.Id);
            _traySettings.TrayIconDesignNameOverrides.Remove(icon.Id);
            CustomTrayIconStorage.Delete(icon);

            if (_traySettings.TrayIconDesignId == icon.Id)
            {
                // Удалили ВЫБРАННУЮ иконку — откатываемся на первую доступную
                // карточку (та же логика восстановления, что и при удалении
                // выбранного цвета в RefreshColorRow).
                var fallback = GetOrderedIcons().FirstOrDefault();
                _traySettings.TrayIconDesignId = fallback?.Id ?? TrayIconDesign.Spokes.ToString();
                ApplyLiveIcon();
            }
            else
            {
                _traySettingsStore.Save(_traySettings);
            }

            RefreshDesignGallery();
        }

        importButton.Click += async (_, _) =>
        {
            _suppressDeactivateClose = true;
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Выберите изображение для иконки трея",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.ico", "*.bmp", "*.jpg", "*.jpeg"] },
                    },
                });

                var localPath = files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (localPath is null)
                {
                    return;
                }

                var imported = CustomTrayIconStorage.Import(localPath);
                _traySettings.CustomTrayIcons.Add(imported);
                _traySettings.TrayIconDesignId = imported.Id;
                ApplyLiveIcon();
                RefreshDesignGallery();
            }
            finally
            {
                _suppressDeactivateClose = false;
            }
        };

        void CommitRename(string designId, string? newName)
        {
            // Children.Clear() внутри RefreshDesignGallery() ниже синхронно отбирает
            // фокус у ещё "живого" TextBox, из-за чего LostFocus срабатывает ПОВТОРНО
            // прямо посреди этого же вызова (реентерабельно) — без этой защиты каждое
            // переименование через Enter+клик-мимо запускало вложенный Clear()/Add(),
            // из-за чего часть карточек добавлялась в галерею дважды.
            if (isCommittingRename)
            {
                return;
            }

            isCommittingRename = true;
            try
            {
                newName = newName?.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    _traySettings.TrayIconDesignNameOverrides[designId] = newName;
                    _traySettingsStore.Save(_traySettings);
                }

                renamingDesignId = null;
                RefreshDesignGallery();
            }
            finally
            {
                isCommittingRename = false;
            }
        }

        void RefreshColorRow()
        {
            colorRow.Children.Clear();

            foreach (var hex in ColorSort.SortByHue(_traySettings.TrayIconColors))
            {
                var isSelected = string.Equals(hex, _traySettings.TrayIconColorHex, StringComparison.OrdinalIgnoreCase);

                var swatch = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)),
                    BorderThickness = new Thickness(isSelected ? 3 : 1),
                    BorderBrush = isSelected
                        ? accentBrush
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                    Cursor = handCursor,
                };
                ToolTip.SetTip(swatch, hex);
                // FP18 — по умолчанию подсказка следует за курсором (PlacementMode.Pointer)
                // и на маленьком 28px кружке перекрывает собой сам цвет, на который
                // навелись (живой фидбек, скриншот). Показываем её НИЖЕ кружка вместо
                // этого — там свободное место, ничего не перекрывает.
                ToolTip.SetPlacement(swatch, Avalonia.Controls.PlacementMode.Bottom);
                ToolTip.SetVerticalOffset(swatch, 4);

                swatch.PointerPressed += (_, _) =>
                {
                    _traySettings.TrayIconColorHex = hex;
                    ApplyLiveIcon();
                    RefreshColorRow();
                    RefreshDesignGallery();
                };

                // FP17 Фаза 2 — тот же крестик, что и раньше, но с бликом при
                // наведении (тон покоя не меняем — было решено консервативно, без
                // ввода нового цвета, только блик).
                var removeGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var removeGlyphRestColor = ResolveThemeColor("AppFaint", Avalonia.Media.Color.FromRgb(0x90, 0x90, 0x90));
                var removeGlyph = BuildSweepWallButton(
                    BuildCrossGlyph(7, 1.4, removeGlyphBrush),
                    removeGlyphBrush,
                    glyphRestColor: Avalonia.Media.Colors.White,
                    glyphHoverColor: Avalonia.Media.Colors.White,
                    cornerRadius: new CornerRadius(7),
                    restColor: removeGlyphRestColor,
                    solidColor: removeGlyphRestColor,
                    streakColor: Avalonia.Media.Colors.White,
                    sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
                    isEnabled: true,
                    tooltip: "Удалить цвет",
                    onClick: () =>
                    {
                        _traySettings.TrayIconColors.Remove(hex);

                        if (string.Equals(_traySettings.TrayIconColorHex, hex, StringComparison.OrdinalIgnoreCase)
                            && _traySettings.TrayIconColors.Count > 0)
                        {
                            _traySettings.TrayIconColorHex = _traySettings.TrayIconColors[0];
                            ApplyLiveIcon();
                            RefreshDesignGallery();
                        }
                        else
                        {
                            _traySettingsStore.Save(_traySettings);
                        }

                        RefreshColorRow();
                    });
                removeGlyph.Width = 14;
                removeGlyph.Height = 14;
                removeGlyph.HorizontalAlignment = HorizontalAlignment.Right;
                removeGlyph.VerticalAlignment = VerticalAlignment.Top;
                removeGlyph.Margin = new Thickness(0, -3, -3, 0);

                var cell = new Grid { Margin = new Thickness(0, 0, 4, 4) };
                cell.Children.Add(swatch);
                cell.Children.Add(removeGlyph);
                colorRow.Children.Add(cell);
            }

            // FP17 Фаза 2 — тот же плюс, что и раньше, но с бликом при наведении;
            // палитра НЕ меняется (консервативное решение, как и для removeGlyph
            // выше) — граница-контур сохранена отдельно, BuildSweepWallButton её
            // не задаёт сам.
            var addGlyphBrush = new Avalonia.Media.SolidColorBrush();
            var addButton = BuildSweepWallButton(
                BuildPlusGlyph(14, 2, addGlyphBrush),
                addGlyphBrush,
                glyphRestColor: ResolveThemeColor("AppMuted", Avalonia.Media.Color.Parse("#948FA3")),
                glyphHoverColor: ResolveThemeColor("AppInk", Avalonia.Media.Color.Parse("#F1EEF7")),
                cornerRadius: new CornerRadius(14),
                restColor: ResolveThemeColor("AppNeutralRest", Avalonia.Media.Color.FromArgb(0x0D, 0x94, 0x8F, 0xA3)),
                solidColor: ResolveThemeColor("AppNeutralHover", Avalonia.Media.Color.FromArgb(0x24, 0x94, 0x8F, 0xA3)),
                streakColor: Avalonia.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
                sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
                sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
                isEnabled: true,
                tooltip: "Добавить свой цвет",
                onClick: async () =>
                {
                    _suppressDeactivateClose = true;
                    try
                    {
                        var picker = new ColorPickerWindow(_traySettings.TrayIconColorHex);
                        await picker.ShowDialog(this);

                        if (picker.ResultHex is { } hex && !_traySettings.TrayIconColors.Contains(hex, StringComparer.OrdinalIgnoreCase))
                        {
                            _traySettings.TrayIconColors.Add(hex);
                            _traySettingsStore.Save(_traySettings);
                            RefreshColorRow();
                        }
                    }
                    finally
                    {
                        _suppressDeactivateClose = false;
                    }
                });
            addButton.Width = 28;
            addButton.Height = 28;
            addButton.BorderThickness = new Thickness(1);
            addButton.BorderBrush = neutralBorderBrush;
            addButton.Margin = new Thickness(0, 0, 4, 4);
            colorRow.Children.Add(addButton);
        }

        RefreshDesignGallery();
        RefreshColorRow();

        return panel;
    }

    // Минуты (0..1440) → "HH:mm" — НЕ через TimeOnly, потому что тот не может
    // представить "24:00" (см. TimeSegment.cs и PLAN_FP10 Фаза 4).
    private static string FormatMinutesOfDay(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, 1440);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private static string FormatTimeCondition(AutomationRule rule)
    {
        if (rule.IsTimeAlwaysActive)
        {
            return "весь день";
        }

        return string.Join(", ", rule.TimeSegments
            .OrderBy(s => s.StartMinute)
            .Select(s => $"{FormatMinutesOfDay(s.StartMinute)}–{FormatMinutesOfDay(s.EndMinute)}"));
    }

    // FP10 Фаза 4 — линейка с отрезками вместо числовых степперов часа/минуты.
    // Схема жестов: ЛКМ на пустом месте + протяжка создаёт отрезок (простой клик
    // без протяжки — час по умолчанию); ЛКМ по краю (~10px зона у засечки) тянет
    // этот край; ЛКМ по середине двигает отрезок целиком; ПКМ удаляет отрезок.
    // Отрезки, коснувшиеся/пересёкшиеся в процессе, сливаются в один при
    // отпускании кнопки (см. TimeSegment.Normalize). Явного переключателя "весь
    // день" нет намеренно — это просто отрезок, растянутый на всю линейку (кнопка
    // "Растянуть на все сутки" — ярлык, а не особый режим), см. PLAN_FP10 Фаза 4.
    private (Control Widget, Func<(List<TimeSegment> Segments, bool IsAlwaysActive)> GetValue, Action<IEnumerable<TimeSegment>> SetValue) BuildTimeSegmentsRuler(
        IEnumerable<TimeSegment> initialSegments)
    {
        const int Step = 15;
        const double EdgePx = 10;

        var segments = initialSegments.Select(s => (Start: s.StartMinute, End: s.EndMinute)).ToList();

        var readoutText = new TextBlock { FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var fillDayButton = new Button { Content = "Растянуть на все сутки" };

        var readoutRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(readoutText, 0);
        Grid.SetColumn(fillDayButton, 1);
        readoutRow.Children.Add(readoutText);
        readoutRow.Children.Add(fillDayButton);

        // Background=Transparent обязателен — без него Canvas не участвует в
        // хит-тесте на своей ПУСТОЙ площади (только там, где реально есть дочерний
        // элемент с фоном, т.е. уже нарисованный отрезок), и клик по свободному
        // месту линейки (создание нового отрезка) просто не доходит до обработчика
        // PointerPressed ниже — тот же класс проблемы, что уже был у StackPanel
        // в AddSliderRow (см. её комментарий про "хит-тест за пределами детей").
        var canvas = new Canvas { Height = 44, Background = Avalonia.Media.Brushes.Transparent };
        var trackBorder = new Border
        {
            // FP17 — сведено к "мелкому" уровню шкалы радиусов (8), было 6.
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = canvas,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross),
        };
        trackBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurfaceSunken"));
        trackBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppLine"));

        var ticksRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), Margin = new Thickness(0, 4, 0, 0) };
        var tickLabels = new[] { "00:00", "06:00", "12:00", "18:00", "24:00" };
        for (var col = 0; col < tickLabels.Length; col++)
        {
            var tick = new TextBlock
            {
                Text = tickLabels[col],
                FontSize = 11,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : col == tickLabels.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            };
            tick.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));
            Grid.SetColumn(tick, col);
            ticksRow.Children.Add(tick);
        }

        var widget = new StackPanel { Spacing = 6 };
        widget.Children.Add(readoutRow);
        widget.Children.Add(trackBorder);
        widget.Children.Add(ticksRow);

        int Snap(double minutes) => (int)(Math.Round(minutes / Step) * Step);

        int XToMinutes(double x, double width)
        {
            var frac = Math.Clamp(x / width, 0, 1);
            return Math.Clamp(Snap(frac * 1440), 0, 1440);
        }

        void Describe()
        {
            if (segments.Count == 0)
            {
                readoutText.Text = "(ничего не выбрано)";
            }
            else if (segments.Count == 1 && segments[0].Start <= 0 && segments[0].End >= 1440)
            {
                readoutText.Text = "Весь день";
            }
            else
            {
                readoutText.Text = string.Join(", ", segments
                    .OrderBy(s => s.Start)
                    .Select(s => $"{FormatMinutesOfDay(s.Start)}–{FormatMinutesOfDay(s.End)}"));
            }
        }

        void Render()
        {
            canvas.Children.Clear();
            var width = canvas.Bounds.Width;
            var height = canvas.Bounds.Height > 0 ? canvas.Bounds.Height : 44;

            if (width > 0)
            {
                foreach (var seg in segments.OrderBy(s => s.Start))
                {
                    var left = seg.Start / 1440.0 * width;
                    var segWidth = Math.Max(3, (seg.End - seg.Start) / 1440.0 * width);

                    var segBorder = new Border
                    {
                        Width = segWidth,
                        Height = height,
                        BorderThickness = new Thickness(2, 0, 2, 0),
                    };
                    segBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppAccentBackgroundBrush"));
                    segBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppAccentBrush"));
                    Canvas.SetLeft(segBorder, left);
                    Canvas.SetTop(segBorder, 0);
                    canvas.Children.Add(segBorder);
                }
            }

            Describe();
        }

        (string Type, int Index)? HitTest(double x, double width)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var (s, en) = segments[i];
                var px1 = s / 1440.0 * width;
                var px2 = en / 1440.0 * width;
                var wide = (px2 - px1) > EdgePx * 2;
                if (x >= px1 - EdgePx && x <= px1 + EdgePx && wide)
                {
                    return ("edge-left", i);
                }
                if (x >= px2 - EdgePx && x <= px2 + EdgePx && wide)
                {
                    return ("edge-right", i);
                }
                if (x > px1 && x < px2)
                {
                    return ("move", i);
                }
            }
            return null;
        }

        void NormalizeAndSync()
        {
            var (normalized, isAlwaysActive) = TimeSegment.Normalize(
                segments.Select(s => new TimeSegment { StartMinute = s.Start, EndMinute = s.End }));
            segments = isAlwaysActive
                ? new List<(int Start, int End)> { (0, 1440) }
                : normalized.Select(s => (s.StartMinute, s.EndMinute)).ToList();
            Render();
        }

        void BeginEditExisting((string Type, int Index) hit, PointerPressedEventArgs e)
        {
            e.Pointer.Capture(canvas);
            var (origStart, origEnd) = segments[hit.Index];
            var startX = e.GetCurrentPoint(canvas).Position.X;

            void Move(object? _, PointerEventArgs ev)
            {
                var width = canvas.Bounds.Width;
                if (width <= 0)
                {
                    return;
                }

                var x = ev.GetCurrentPoint(canvas).Position.X;
                var mins = XToMinutes(x, width);

                if (hit.Type == "edge-left")
                {
                    var newStart = Math.Max(0, Math.Min(mins, segments[hit.Index].End - Step));
                    segments[hit.Index] = (newStart, segments[hit.Index].End);
                }
                else if (hit.Type == "edge-right")
                {
                    var newEnd = Math.Min(1440, Math.Max(mins, segments[hit.Index].Start + Step));
                    segments[hit.Index] = (segments[hit.Index].Start, newEnd);
                }
                else
                {
                    var deltaFrac = (x - startX) / width;
                    var deltaMin = Snap(deltaFrac * 1440);
                    var duration = origEnd - origStart;
                    var ns = Math.Clamp(origStart + deltaMin, 0, 1440 - duration);
                    segments[hit.Index] = (ns, ns + duration);
                }
                Render();
            }

            void Up(object? _, PointerReleasedEventArgs ev)
            {
                canvas.PointerMoved -= Move;
                canvas.PointerReleased -= Up;
                e.Pointer.Capture(null);
                NormalizeAndSync();
            }

            canvas.PointerMoved += Move;
            canvas.PointerReleased += Up;
        }

        void BeginCreateNew(PointerPressedEventArgs e, double anchorX, double width)
        {
            e.Pointer.Capture(canvas);
            var anchor = XToMinutes(anchorX, width);
            var index = segments.Count;
            segments.Add((anchor, Math.Min(1440, anchor + Step)));
            var dragged = false;
            Render();

            void Move(object? _, PointerEventArgs ev)
            {
                dragged = true;
                var w = canvas.Bounds.Width;
                if (w <= 0)
                {
                    return;
                }

                var cur = XToMinutes(ev.GetCurrentPoint(canvas).Position.X, w);
                var start = Math.Max(0, Math.Min(anchor, cur));
                var end = Math.Min(1440, Math.Max(anchor, cur));
                if (end - start < Step)
                {
                    end = Math.Min(1440, start + Step);
                }
                segments[index] = (start, end);
                Render();
            }

            void Up(object? _, PointerReleasedEventArgs ev)
            {
                canvas.PointerMoved -= Move;
                canvas.PointerReleased -= Up;
                e.Pointer.Capture(null);
                if (!dragged)
                {
                    var start = Math.Max(0, anchor - 30);
                    segments[index] = (start, Math.Min(1440, start + 60));
                }
                NormalizeAndSync();
            }

            canvas.PointerMoved += Move;
            canvas.PointerReleased += Up;
        }

        canvas.PointerPressed += (_, e) =>
        {
            var width = canvas.Bounds.Width;
            if (width <= 0)
            {
                return;
            }

            var point = e.GetCurrentPoint(canvas);
            var x = point.Position.X;

            if (point.Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                var hit = HitTest(x, width);
                if (hit is { } h)
                {
                    segments.RemoveAt(h.Index);
                    NormalizeAndSync();
                }
                return;
            }

            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            var editHit = HitTest(x, width);
            if (editHit is { } eh)
            {
                BeginEditExisting(eh, e);
            }
            else
            {
                BeginCreateNew(e, x, width);
            }
        };

        fillDayButton.Click += (_, _) =>
        {
            segments = new List<(int Start, int End)> { (0, 1440) };
            Render();
        };

        canvas.SizeChanged += (_, _) => Render();
        Render();

        (List<TimeSegment> Segments, bool IsAlwaysActive) GetValue() => TimeSegment.Normalize(
            segments.Select(s => new TimeSegment { StartMinute = s.Start, EndMinute = s.End }));

        // Нужен для редактирования уже созданного правила — подгружает его отрезки
        // в уже существующий виджет вместо пересоздания (см. BuildAutomationTab).
        void SetValue(IEnumerable<TimeSegment> newSegments)
        {
            segments = newSegments.Select(s => (Start: s.StartMinute, End: s.EndMinute)).ToList();
            Render();
        }

        return (widget, GetValue, SetValue);
    }

    // FP17 — читает АКТУАЛЬНОЕ значение именованного токена ТЕКУЩЕЙ темы (не
    // зашитый литерал). Снимок на момент вызова, не живая подписка — годится
    // там, где значение сразу же используется для одноразовой инициализации
    // мутируемой SolidColorBrush (см. BuildSweepWallButton: та же кисть потом
    // ещё и перекрашивается вручную по наведению, поэтому полноценный Bind()
    // сюда не встроить без конфликта с этими ручными перезаписями) — для
    // простых, не мутируемых свойств вместо этого используется обычный Bind()
    // на GetResourceObservable (см. card.Background в RefreshRulesList).
    private Avalonia.Media.Color ResolveThemeColor(string resourceKey, Avalonia.Media.Color fallback) =>
        this.TryFindResource(resourceKey, out var value) && value is Avalonia.Media.SolidColorBrush brush
            ? brush.Color
            : fallback;

    private void BuildAutomationTab(StackPanel root)
    {
        var automationStore = new JsonFileAutomationStore();
        var automationSettings = automationStore.Load();
        var monitorNames = new MonitorNameStore().Load();

        var enabledCheckBox = new CheckBox { Content = "Включить автоматизацию", IsChecked = automationSettings.IsEnabled };
        root.Children.Add(enabledCheckBox);

        // FP17 Фаза 4, п.15 — блок ("Состояние", было "Сейчас активно")
        // добавляется в root в САМОМ КОНЦЕ этого метода (после "Новое
        // правило"), а не здесь — согласовано с пользователем перенести его
        // в низ вкладки. activeNowPanel объявлен уже тут (нужен
        // RefreshActiveNow ниже), но в дерево визуально попадает позже.
        var activeNowPanel = new StackPanel { Spacing = 2 };

        void RefreshActiveNow()
        {
            activeNowPanel.Children.Clear();
            var current = automationStore.Load();

            if (!current.IsEnabled || current.Rules.Count == 0)
            {
                activeNowPanel.Children.Add(new TextBlock
                {
                    Text = "Автоматизация выключена или правил ещё нет.",
                    FontStyle = Avalonia.Media.FontStyle.Italic,
                });
                return;
            }

            foreach (var monitor in _controller.Monitors)
            {
                var active = _automationEngine?.ResolveActiveRuleForMonitor(monitor);
                if (active is null)
                {
                    continue;
                }

                // FP17 Фаза 4, п.15 — без имени правила (было
                // `DELL: «80%» → 80%`, дублирование для безымянных правил) —
                // согласовано с пользователем: везде только монитор→значение,
                // независимо от того, названо правило или нет.
                activeNowPanel.Children.Add(new TextBlock
                {
                    Text = $"{MonitorLabel.Format(monitor, monitorNames)} → {active.Percent}%",
                });
            }

            if (activeNowPanel.Children.Count == 0)
            {
                activeNowPanel.Children.Add(new TextBlock
                {
                    Text = "Ни для одного монитора нет активных правил прямо сейчас.",
                    FontStyle = Avalonia.Media.FontStyle.Italic,
                });
            }
        }

        enabledCheckBox.IsCheckedChanged += (_, _) =>
        {
            automationSettings.IsEnabled = enabledCheckBox.IsChecked ?? true;
            automationStore.Save(automationSettings);
            RefreshActiveNow();
        };

        RefreshActiveNow();
        var activeNowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        activeNowTimer.Tick += (_, _) => RefreshActiveNow();
        activeNowTimer.Start();

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        var rulesHeaderText = new TextBlock { Text = "Правила", FontWeight = Avalonia.Media.FontWeight.Bold };
        ToolTip.SetTip(rulesHeaderText, "Порядок в списке имеет значение — им разрешаются конфликты между " +
            "правилами без явного приоритета (см. кнопки \"▲\"/\"▼\"): побеждает то правило, что ВЫШЕ по списку.");
        root.Children.Add(rulesHeaderText);

        var rulesListPanel = new StackPanel { Spacing = 6 };

        // Форма "Новое правило" ниже переиспользуется и для редактирования (см.
        // PLAN_FP10) — editingRule != null, пока форма заполнена данными
        // существующего правила. loadRuleIntoForm объявлен здесь как nullable-
        // делегат и присваивается реальной реализацией ПОЗЖЕ, после того как
        // поля формы объявлены (та же причина, что и раньше с CS0165 у FP11 —
        // RefreshRulesList вызывается раньше, чем форма построена).
        AutomationRule? editingRule = null;
        Action<AutomationRule>? loadRuleIntoForm = null;

        // FP10/FP17 — цвета для карточки-с-стенами читаются из именованных
        // токенов темы (App.axaml) через ResolveThemeColor, а не зашиты
        // литералом — раньше несколько значений были буквально СКОПИРОВАНЫ из
        // тёмной темы и молча ломались при переключении на светлую (см.
        // PLAN_FP17). Приглушённый тон покоя общий у всех кнопок в стене,
        // Изменить/Удалить заливаются насыщенным цветом при наведении, "▲"/"▼"
        // палитру НЕ меняют (только чуть светлее той же приглушённой заливки).
        var wallRestColor = ResolveThemeColor("AppNeutralRest", Avalonia.Media.Color.FromArgb(0x0D, 0x94, 0x8F, 0xA3));
        var editColor = ResolveThemeColor("AppWarning", Avalonia.Media.Color.Parse("#E8C23D"));
        var editInkColor = ResolveThemeColor("AppWarningInk", Avalonia.Media.Color.Parse("#241C02"));
        var dangerColor = ResolveThemeColor("AppDanger", Avalonia.Media.Color.Parse("#E85D6B"));
        var dangerInkColor = ResolveThemeColor("AppDangerInk", Avalonia.Media.Colors.White);
        var moveHoverColor = ResolveThemeColor("AppNeutralHover", Avalonia.Media.Color.FromArgb(0x24, 0x94, 0x8F, 0xA3));
        var streakFaintColor = Avalonia.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
        var mutedGlyphColor = ResolveThemeColor("AppMuted", Avalonia.Media.Color.Parse("#948FA3"));
        var inkGlyphColor = ResolveThemeColor("AppInk", Avalonia.Media.Color.Parse("#F1EEF7"));
        var goodColor = ResolveThemeColor("AppGood", Avalonia.Media.Color.Parse("#7FBF6A"));
        var faintColor = ResolveThemeColor("AppFaint", Avalonia.Media.Color.Parse("#6B6678"));

        void RefreshRulesList()
        {
            rulesListPanel.Children.Clear();

            for (var i = 0; i < automationSettings.Rules.Count; i++)
            {
                var rule = automationSettings.Rules[i];
                var index = i;

                var conditionParts = new List<string>();
                if (rule.HasTimeCondition)
                {
                    conditionParts.Add(FormatTimeCondition(rule));
                }
                if (rule.HasProcessCondition)
                {
                    var matchTypeText = rule.ProcessMatchType == AppMatchType.ProcessName ? "процесс" : "заголовок";
                    conditionParts.Add($"{matchTypeText} «{rule.ProcessMatchValue}»");
                }
                var conditionsText = conditionParts.Count == 2
                    ? string.Join(rule.Combinator == AutomationCombinator.And ? " И " : " ИЛИ ", conditionParts)
                    : conditionParts.FirstOrDefault() ?? "(нет условий)";

                var scopeText = rule.HasProcessCondition
                    ? "монитор с окном"
                    : rule.MonitorKeys.Count == 0
                        ? "все мониторы"
                        : string.Join(", ", rule.MonitorKeys.Select(key =>
                            _controller.Monitors.FirstOrDefault(m => BrightnessController.GetMonitorKey(m) == key) is { } found
                                ? MonitorLabel.Format(found, monitorNames)
                                : key));

                var priorityText = rule.Priority is not null ? $"приоритет {rule.Priority}" : null;

                // FP10 — карточка-раскладка "стена | центр | стена" (тот же приём,
                // что уже используется leftArrow/rightArrow в галерее форм иконки
                // трея): кнопки — часть силуэта самой карточки, не отдельные
                // элементы поверх неё. ClipToBounds обязателен — иначе прямые
                // внутренние углы полос торчат за скруглённые внешние углы карточки.
                var card = new Border
                {
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    ClipToBounds = true,
                };
                // FP17 — фон карточки живо привязан к AppSurface (тот же баг, что и у
                // цветов кнопок: раньше был буквально скопированным литералом тёмной
                // темы, не адаптировался в светлой).
                card.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));
                var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,34") };

                var editGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var editButton = BuildSweepWallButton(
                    BuildEditGlyph(13, 1.8, editGlyphBrush),
                    editGlyphBrush,
                    glyphRestColor: mutedGlyphColor,
                    glyphHoverColor: editInkColor,
                    cornerRadius: new CornerRadius(13, 0, 0, 0),
                    restColor: wallRestColor,
                    solidColor: editColor,
                    streakColor: Avalonia.Media.Colors.White,
                    sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
                    isEnabled: true,
                    tooltip: "Изменить",
                    onClick: () => loadRuleIntoForm?.Invoke(rule));

                var deleteGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var deleteButton = BuildSweepWallButton(
                    BuildCrossGlyph(11, 2.0, deleteGlyphBrush),
                    deleteGlyphBrush,
                    glyphRestColor: mutedGlyphColor,
                    glyphHoverColor: dangerInkColor,
                    cornerRadius: new CornerRadius(0, 0, 0, 13),
                    restColor: wallRestColor,
                    solidColor: dangerColor,
                    streakColor: dangerInkColor,
                    sweepStart: new RelativePoint(1, 0, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
                    isEnabled: true,
                    tooltip: "Удалить",
                    onClick: () =>
                    {
                        automationSettings.Rules.Remove(rule);
                        automationStore.Save(automationSettings);
                        RefreshRulesList();
                        RefreshActiveNow();
                    });

                var leftWall = new Grid { RowDefinitions = new RowDefinitions("*,*") };
                Grid.SetRow(editButton, 0);
                Grid.SetRow(deleteButton, 1);
                leftWall.Children.Add(editButton);
                leftWall.Children.Add(deleteButton);
                Grid.SetColumn(leftWall, 0);

                var canMoveUp = index > 0;
                var canMoveDown = index < automationSettings.Rules.Count - 1;

                var moveUpGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var moveUpButton = BuildSweepWallButton(
                    BuildChevronUpGlyph(12, 7, 2.2, moveUpGlyphBrush),
                    moveUpGlyphBrush,
                    glyphRestColor: mutedGlyphColor,
                    glyphHoverColor: inkGlyphColor,
                    cornerRadius: new CornerRadius(0, 13, 0, 0),
                    restColor: wallRestColor,
                    solidColor: moveHoverColor,
                    streakColor: streakFaintColor,
                    sweepStart: new RelativePoint(0, 1, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 0, RelativeUnit.Relative),
                    isEnabled: canMoveUp,
                    tooltip: "Сдвинуть выше",
                    onClick: () =>
                    {
                        (automationSettings.Rules[index - 1], automationSettings.Rules[index]) =
                            (automationSettings.Rules[index], automationSettings.Rules[index - 1]);
                        automationStore.Save(automationSettings);
                        RefreshRulesList();
                        RefreshActiveNow();
                    });

                var moveDownGlyphBrush = new Avalonia.Media.SolidColorBrush();
                var moveDownButton = BuildSweepWallButton(
                    BuildChevronDownGlyph(12, 7, 2.2, moveDownGlyphBrush),
                    moveDownGlyphBrush,
                    glyphRestColor: mutedGlyphColor,
                    glyphHoverColor: inkGlyphColor,
                    cornerRadius: new CornerRadius(0, 0, 13, 0),
                    restColor: wallRestColor,
                    solidColor: moveHoverColor,
                    streakColor: streakFaintColor,
                    sweepStart: new RelativePoint(0, 0, RelativeUnit.Relative),
                    sweepEnd: new RelativePoint(0, 1, RelativeUnit.Relative),
                    isEnabled: canMoveDown,
                    tooltip: "Сдвинуть ниже",
                    onClick: () =>
                    {
                        (automationSettings.Rules[index + 1], automationSettings.Rules[index]) =
                            (automationSettings.Rules[index], automationSettings.Rules[index + 1]);
                        automationStore.Save(automationSettings);
                        RefreshRulesList();
                        RefreshActiveNow();
                    });

                var rightWall = new Grid { RowDefinitions = new RowDefinitions("*,*") };
                Grid.SetRow(moveUpButton, 0);
                Grid.SetRow(moveDownButton, 1);
                rightWall.Children.Add(moveUpButton);
                rightWall.Children.Add(moveDownButton);
                Grid.SetColumn(rightWall, 2);

                // Кружок-переключатель вкл/выкл — сам кликабельный (по фидбеку
                // пользователя: "это же кнопка выключения правила"), центрирован
                // по всей высоте блока имя+детали (не только по строке с именем).
                var statusToggle = new Border
                {
                    Width = 15,
                    Height = 15,
                    CornerRadius = new CornerRadius(7.5),
                    BorderThickness = new Thickness(1.5),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0),
                };

                void RefreshStatusToggleVisual()
                {
                    statusToggle.BorderBrush = new Avalonia.Media.SolidColorBrush(rule.IsEnabled ? goodColor : faintColor);
                    statusToggle.Background = rule.IsEnabled
                        ? new Avalonia.Media.SolidColorBrush(goodColor)
                        : Avalonia.Media.Brushes.Transparent;
                    ToolTip.SetTip(statusToggle, rule.IsEnabled
                        ? "Правило включено — нажмите, чтобы выключить"
                        : "Правило выключено — нажмите, чтобы включить");
                }
                RefreshStatusToggleVisual();

                statusToggle.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    rule.IsEnabled = !rule.IsEnabled;
                    automationStore.Save(automationSettings);
                    RefreshStatusToggleVisual();
                    RefreshActiveNow();
                };

                var nameText = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(rule.Name) ? "(без названия)" : rule.Name,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                };

                var detailText = priorityText is null
                    ? $"{conditionsText} → {rule.Percent}% · {scopeText}"
                    : $"{conditionsText} → {rule.Percent}% · {scopeText} · {priorityText}";

                var textStack = new StackPanel { Spacing = 2 };
                textStack.Children.Add(nameText);
                textStack.Children.Add(new TextBlock { Text = detailText, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

                var centerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(10, 8, 10, 8) };
                Grid.SetColumn(statusToggle, 0);
                Grid.SetColumn(textStack, 1);
                centerGrid.Children.Add(statusToggle);
                centerGrid.Children.Add(textStack);
                Grid.SetColumn(centerGrid, 1);

                cardGrid.Children.Add(leftWall);
                cardGrid.Children.Add(centerGrid);
                cardGrid.Children.Add(rightWall);
                card.Child = cardGrid;

                rulesListPanel.Children.Add(card);
            }

            if (automationSettings.Rules.Count == 0)
            {
                rulesListPanel.Children.Add(new TextBlock { Text = "(правил ещё нет)", FontStyle = Avalonia.Media.FontStyle.Italic });
            }
        }

        RefreshRulesList();
        root.Children.Add(rulesListPanel);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        // Визуально отделённая карточка для формы создания правила — тот же приём,
        // что раньше был у формы расписания (FP9 Фаза 5).
        var newRuleCard = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            // FP17 — сведено к "крупному" уровню шкалы радиусов (14, тот же, что и
            // у карточек правил над этой формой), было 4 — раньше форма добавления
            // визуально не совпадала по скруглению со списком правил над ней.
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
        };
        var newRulePanel = new StackPanel { Spacing = 8 };
        newRuleCard.Child = newRulePanel;

        var formTitleText = new TextBlock { Text = "Новое правило", FontWeight = Avalonia.Media.FontWeight.Bold };
        newRulePanel.Children.Add(formTitleText);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        nameRow.Children.Add(new TextBlock { Text = "Название:", VerticalAlignment = VerticalAlignment.Center, Width = 150 });
        var nameInput = new TextBox { Width = 260, PlaceholderText = "например, «Вечер»" };
        nameRow.Children.Add(nameInput);
        newRulePanel.Children.Add(nameRow);

        // --- Условие по времени: линейка с отрезками (см. PLAN_FP10 Фаза 4) —
        // булево ("время попадает хотя бы в один отрезок"), комбинируется с
        // условием по процессу через И/ИЛИ ниже. ---
        var timeConditionCheckBox = new CheckBox { Content = "Условие по времени" };
        newRulePanel.Children.Add(timeConditionCheckBox);

        var (timeRulerWidget, getTimeSegments, setTimeSegments) = BuildTimeSegmentsRuler(new[]
        {
            new TimeSegment { StartMinute = 22 * 60, EndMinute = 24 * 60 },
            new TimeSegment { StartMinute = 0, EndMinute = 6 * 60 },
        });
        var timeRulerRow = new Border { Margin = new Thickness(20, 0, 0, 0), IsVisible = false, Child = timeRulerWidget };
        newRulePanel.Children.Add(timeRulerRow);

        // --- Условие по процессу — та же машинерия подсказок/скрытия процессов,
        // что раньше была у профилей приложений (FP5/FP9). ---
        var processConditionCheckBox = new CheckBox { Content = "Условие по процессу" };
        newRulePanel.Children.Add(processConditionCheckBox);

        var processConditionPanel = new StackPanel { Spacing = 8, Margin = new Thickness(20, 0, 0, 0), IsVisible = false };
        newRulePanel.Children.Add(processConditionPanel);

        var matchTypeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        matchTypeRow.Children.Add(new TextBlock { Text = "Определять по:", VerticalAlignment = VerticalAlignment.Center, Width = 130 });
        var matchTypeCombo = new ComboBox
        {
            ItemsSource = new[] { "Запущенный процесс (из списка)", "Заголовок окна (текст)" },
            SelectedIndex = 0,
            MinWidth = 220,
        };
        matchTypeRow.Children.Add(matchTypeCombo);
        processConditionPanel.Children.Add(matchTypeRow);

        var processPickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        // Все переменные, на которые ссылаются локальные функции/шаблоны ниже, объявлены
        // здесь заранее (просто как объекты, без ItemTemplate) — иначе анализ определённого
        // присваивания C# ругается, даже если реально эти обработчики выполнятся значительно позже.
        // FP17 Фаза 4, п.6 — без PlaceholderText поле выглядело как пустой
        // прямоугольник без подсказки, что оно вообще для чего-то (можно и
        // вводить текст для фильтрации списка, а не только выбирать из
        // выпадающего списка).
        var processAutoComplete = new AutoCompleteBox
        {
            MinWidth = 260,
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
            PlaceholderText = "Введите или выберите процесс…",
        };
        var hiddenProcessesLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var hiddenProcessesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var hiddenProcessesCombo = new ComboBox { MinWidth = 200 };

        // Текст "Скрыто процессов: N" — отдельный TextBlock рядом с ComboBox, а не его
        // PlaceholderText: раньше рядом с этой надписью был виден плюсик — ComboBox в
        // закрытом состоянии иногда показывал шаблон первого пункта вместо плейсхолдера.
        // Вынос текста наружу и принудительный сброс выбора (см. SelectionChanged ниже)
        // полностью убирают этот эффект.
        hiddenProcessesCombo.SelectionChanged += (_, _) =>
        {
            if (hiddenProcessesCombo.SelectedIndex != -1)
            {
                hiddenProcessesCombo.SelectedIndex = -1;
            }
        };

        // Первый пункт списка — не процесс, а спец-строка "вернуть все сразу"
        // (жирный текст + крупный плюс, кликабельна целиком). Ниже неё —
        // обычные пункты по одному процессу с плюсом, для точечного возврата.
        // Значение — случайный GUID, гарантированно не совпадёт с реальным именем процесса.
        var restoreAllSentinel = Guid.NewGuid().ToString();

        hiddenProcessesCombo.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            if (name == restoreAllSentinel)
            {
                var allRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2) };
                var allText = new TextBlock
                {
                    Text = "Вернуть все скрытые процессы",
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var restoreAllGlyph = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    // FP17 — был другой зелёный (#2E8B3D) для того же по смыслу
                    // "+"-глифа, что и у точечного восстановления процесса ниже
                    // (#3CA050) — сведено к одному тону.
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3C, 0xA0, 0x50)),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new TextBlock
                    {
                        Text = "+",
                        Foreground = Avalonia.Media.Brushes.White,
                        FontSize = 16,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                allRow.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    RestoreAllProcessNames();
                };

                Grid.SetColumn(allText, 0);
                Grid.SetColumn(restoreAllGlyph, 1);
                allRow.Children.Add(allText);
                allRow.Children.Add(restoreAllGlyph);
                return allRow;
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2) };
            var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };

            var restoreGlyph = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3C, 0xA0, 0x50)),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = "+",
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = 13,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            restoreGlyph.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                RestoreProcessName(name);
            };

            Grid.SetColumn(text, 0);
            Grid.SetColumn(restoreGlyph, 1);
            row.Children.Add(text);
            row.Children.Add(restoreGlyph);
            return row;
        });

        void RestoreProcessName(string name)
        {
            _appSettings.HiddenProcessNames.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            _appSettingsStore.Save(_appSettings);
            RefreshRunningProcesses();
            RefreshHiddenLink();
        }

        void RestoreAllProcessNames()
        {
            _appSettings.HiddenProcessNames.Clear();
            _appSettingsStore.Save(_appSettings);
            RefreshRunningProcesses();
            RefreshHiddenLink();
        }

        void RefreshHiddenLink()
        {
            var hiddenNames = _appSettings.HiddenProcessNames.OrderBy(n => n).ToList();
            hiddenProcessesRow.IsVisible = hiddenNames.Count > 0;
            hiddenProcessesCombo.ItemsSource = hiddenNames.Count > 0
                ? new List<string> { restoreAllSentinel }.Concat(hiddenNames).ToList()
                : hiddenNames;
            hiddenProcessesLabel.Text = $"Скрыто процессов: {hiddenNames.Count}";
            hiddenProcessesCombo.SelectedIndex = -1;
        }

        // Каждый пункт списка — имя процесса + серый кружок с "−" для скрытия
        // ненужных процессов из подсказок (например, служебных). Скрытые запоминаются
        // в AppSettings.HiddenProcessNames и не показываются, пока их явно не вернуть.
        var hideGlyphBg = ResolveThemeColor("AppFaint", Avalonia.Media.Color.FromRgb(0x90, 0x90, 0x90));
        processAutoComplete.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2) };
            var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };

            var hideGlyph = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = new Avalonia.Media.SolidColorBrush(hideGlyphBg),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = BuildMinusGlyph(8, 1.6, Avalonia.Media.Brushes.White),
            };
            hideGlyph.PointerPressed += (_, e) =>
            {
                // Иначе клик по крестику также сработал бы как выбор всего пункта списка.
                e.Handled = true;
                HideProcessName(name);
            };

            Grid.SetColumn(text, 0);
            Grid.SetColumn(hideGlyph, 1);
            row.Children.Add(text);
            row.Children.Add(hideGlyph);
            return row;
        });

        var chevronBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xA0, 0x80, 0x80, 0x80));
        var openAllButton = new Button
        {
            Content = BuildChevronDownGlyph(10, 6, 1.6, chevronBrush),
            Width = 32,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(openAllButton, "Показать список всех запущенных процессов");
        var refreshProcessesButton = new Button { Content = "Обновить список" };

        void RefreshRunningProcesses()
        {
            var hidden = new HashSet<string>(_appSettings.HiddenProcessNames, StringComparer.OrdinalIgnoreCase);
            var names = Process.GetProcesses()
                .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                .Select(p => p.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !hidden.Contains(n))
                .OrderBy(n => n)
                .ToList();

            processAutoComplete.ItemsSource = names;
        }

        void HideProcessName(string name)
        {
            if (!_appSettings.HiddenProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _appSettings.HiddenProcessNames.Add(name);
                _appSettingsStore.Save(_appSettings);
            }

            RefreshRunningProcesses();
            RefreshHiddenLink();
        }

        openAllButton.Click += (_, _) =>
        {
            RefreshRunningProcesses();
            processAutoComplete.IsDropDownOpen = true;
        };
        refreshProcessesButton.Click += (_, _) => RefreshRunningProcesses();
        RefreshRunningProcesses();
        processPickRow.Children.Add(processAutoComplete);
        processPickRow.Children.Add(openAllButton);
        processPickRow.Children.Add(refreshProcessesButton);
        processConditionPanel.Children.Add(processPickRow);

        hiddenProcessesRow.Children.Add(hiddenProcessesLabel);
        hiddenProcessesRow.Children.Add(hiddenProcessesCombo);
        processConditionPanel.Children.Add(hiddenProcessesRow);
        RefreshHiddenLink();

        var titleValueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false };
        titleValueRow.Children.Add(new TextBlock { Text = "Часть заголовка окна:", VerticalAlignment = VerticalAlignment.Center, Width = 130 });
        var titleValueInput = new TextBox { Width = 260 };
        titleValueRow.Children.Add(titleValueInput);
        processConditionPanel.Children.Add(titleValueRow);

        matchTypeCombo.SelectionChanged += (_, _) =>
        {
            var isProcess = matchTypeCombo.SelectedIndex == 0;
            processPickRow.IsVisible = isProcess;
            titleValueRow.IsVisible = !isProcess;
        };

        timeConditionCheckBox.IsCheckedChanged += (_, _) => timeRulerRow.IsVisible = timeConditionCheckBox.IsChecked == true;
        processConditionCheckBox.IsCheckedChanged += (_, _) => processConditionPanel.IsVisible = processConditionCheckBox.IsChecked == true;

        // --- И/ИЛИ — виден только когда заданы ОБА условия (см. PLAN_FP10 Фаза 1 п.2). ---
        var combinatorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false };
        combinatorRow.Children.Add(new TextBlock { Text = "Совпадение:", VerticalAlignment = VerticalAlignment.Center, Width = 150 });
        var andToggle = new ToggleButton { Content = "И", IsChecked = true };
        var orToggle = new ToggleButton { Content = "ИЛИ", IsChecked = false };
        andToggle.Click += (_, _) => { andToggle.IsChecked = true; orToggle.IsChecked = false; };
        orToggle.Click += (_, _) => { orToggle.IsChecked = true; andToggle.IsChecked = false; };
        combinatorRow.Children.Add(andToggle);
        combinatorRow.Children.Add(orToggle);
        newRulePanel.Children.Add(combinatorRow);

        void RefreshCombinatorVisibility()
        {
            combinatorRow.IsVisible = timeConditionCheckBox.IsChecked == true && processConditionCheckBox.IsChecked == true;
        }
        timeConditionCheckBox.IsCheckedChanged += (_, _) => RefreshCombinatorVisibility();
        processConditionCheckBox.IsCheckedChanged += (_, _) => RefreshCombinatorVisibility();

        var percentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        percentRow.Children.Add(new TextBlock { Text = "Яркость:", VerticalAlignment = VerticalAlignment.Center, Width = 150 });
        var percentInput = BuildNumericStepper(0, 100, 50, "%");
        percentRow.Children.Add(percentInput);
        newRulePanel.Children.Add(percentRow);

        // --- Приоритет — необязательный явный тай-брейк (см. PLAN_FP10 Фаза 1 п.6). ---
        var priorityCheckBox = new CheckBox { Content = "Задать приоритет вручную" };
        ToolTip.SetTip(priorityCheckBox, "Чем БОЛЬШЕ число — тем выше приоритет: правило с приоритетом 10 " +
            "побеждает правило с приоритетом 1, если оба активны одновременно. Правило с указанным приоритетом " +
            "всегда побеждает правило без него. При равном приоритете (или когда ни у одного из правил " +
            "приоритет не задан) побеждает то, что выше в списке правил (см. кнопки \"▲\"/\"▼\").");
        newRulePanel.Children.Add(priorityCheckBox);
        var priorityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(20, 0, 0, 0), IsVisible = false };
        priorityRow.Children.Add(new TextBlock { Text = "Приоритет:", VerticalAlignment = VerticalAlignment.Center, Width = 130 });
        var priorityInput = BuildNumericStepper(0, 999, 10, "");
        priorityRow.Children.Add(priorityInput);
        newRulePanel.Children.Add(priorityRow);
        priorityCheckBox.IsCheckedChanged += (_, _) => priorityRow.IsVisible = priorityCheckBox.IsChecked == true;

        // --- Мониторы — используется, только когда правило сработало ПО ВРЕМЕНИ;
        // для процесса область действия определяется автоматически (монитор, где
        // сейчас окно) — см. PLAN_FP10 Фаза 1 п.7. ---
        newRulePanel.Children.Add(new TextBlock
        {
            Text = "Мониторы (учитывается, только если правило сработало по времени — для процесса монитор определяется автоматически):",
            FontStyle = Avalonia.Media.FontStyle.Italic,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var allMonitorsCheckBox = new CheckBox { Content = "Все мониторы", IsChecked = true };
        newRulePanel.Children.Add(allMonitorsCheckBox);

        var monitorCheckBoxes = new List<(MonitorInfo Monitor, CheckBox CheckBox)>();
        var monitorsPickPanel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 0, 0, 0) };
        foreach (var monitor in _controller.Monitors)
        {
            var checkBox = new CheckBox { Content = MonitorLabel.Format(monitor, monitorNames), IsChecked = true, IsEnabled = false };
            monitorCheckBoxes.Add((monitor, checkBox));
            monitorsPickPanel.Children.Add(checkBox);
        }

        allMonitorsCheckBox.IsCheckedChanged += (_, _) =>
        {
            var allSelected = allMonitorsCheckBox.IsChecked == true;
            foreach (var (_, checkBox) in monitorCheckBoxes)
            {
                checkBox.IsEnabled = !allSelected;
                if (allSelected)
                {
                    checkBox.IsChecked = true;
                }
            }
        };
        newRulePanel.Children.Add(monitorsPickPanel);

        var addRuleButton = new Button { Content = "Добавить правило" };
        var cancelEditButton = new Button { Content = "Отмена", IsVisible = false };

        // Возвращает форму в состояние "новое правило" — вызывается и после
        // успешного добавления/сохранения, и по кнопке "Отмена".
        void ResetForm()
        {
            editingRule = null;
            formTitleText.Text = "Новое правило";
            addRuleButton.Content = "Добавить правило";
            cancelEditButton.IsVisible = false;

            nameInput.Text = "";

            timeConditionCheckBox.IsChecked = false;
            setTimeSegments(new[]
            {
                new TimeSegment { StartMinute = 22 * 60, EndMinute = 24 * 60 },
                new TimeSegment { StartMinute = 0, EndMinute = 6 * 60 },
            });

            processConditionCheckBox.IsChecked = false;
            matchTypeCombo.SelectedIndex = 0;
            processPickRow.IsVisible = true;
            titleValueRow.IsVisible = false;
            processAutoComplete.Text = "";
            titleValueInput.Text = "";

            andToggle.IsChecked = true;
            orToggle.IsChecked = false;

            percentInput.Value = 50;

            priorityCheckBox.IsChecked = false;
            priorityInput.Value = 10;

            allMonitorsCheckBox.IsChecked = true;
        }

        // Заполняет форму значениями уже существующего правила — по кнопке
        // "Изменить" на карточке (см. RefreshRulesList выше). Видимость
        // processPickRow/titleValueRow выставляется ЯВНО, а не только через
        // событие SelectionChanged у matchTypeCombo — если два подряд
        // редактируемых правила одного типа (оба "процесс" или оба "заголовок"),
        // SelectedIndex не меняется и событие не перевыстрелит.
        void LoadRuleIntoForm(AutomationRule rule)
        {
            editingRule = rule;
            var ruleLabel = string.IsNullOrWhiteSpace(rule.Name) ? "(без названия)" : rule.Name;
            formTitleText.Text = $"Изменение правила «{ruleLabel}»";
            addRuleButton.Content = "Сохранить изменения";
            cancelEditButton.IsVisible = true;

            nameInput.Text = rule.Name;

            timeConditionCheckBox.IsChecked = rule.HasTimeCondition;
            setTimeSegments(rule.IsTimeAlwaysActive
                ? new[] { new TimeSegment { StartMinute = 0, EndMinute = 1440 } }
                : rule.TimeSegments);

            processConditionCheckBox.IsChecked = rule.HasProcessCondition;
            var isProcessByNameForEdit = rule.ProcessMatchType == AppMatchType.ProcessName;
            matchTypeCombo.SelectedIndex = isProcessByNameForEdit ? 0 : 1;
            processPickRow.IsVisible = isProcessByNameForEdit;
            titleValueRow.IsVisible = !isProcessByNameForEdit;
            processAutoComplete.Text = isProcessByNameForEdit ? rule.ProcessMatchValue : "";
            titleValueInput.Text = isProcessByNameForEdit ? "" : rule.ProcessMatchValue;

            andToggle.IsChecked = rule.Combinator == AutomationCombinator.And;
            orToggle.IsChecked = rule.Combinator == AutomationCombinator.Or;

            percentInput.Value = rule.Percent;

            priorityCheckBox.IsChecked = rule.Priority is not null;
            priorityInput.Value = rule.Priority ?? 10;

            allMonitorsCheckBox.IsChecked = rule.MonitorKeys.Count == 0;
            if (rule.MonitorKeys.Count > 0)
            {
                foreach (var (monitor, checkBox) in monitorCheckBoxes)
                {
                    checkBox.IsChecked = rule.MonitorKeys.Contains(BrightnessController.GetMonitorKey(monitor));
                }
            }
        }

        loadRuleIntoForm = LoadRuleIntoForm;
        cancelEditButton.Click += (_, _) => ResetForm();

        addRuleButton.Click += (_, _) =>
        {
            var hasTime = timeConditionCheckBox.IsChecked == true;
            var hasProcess = processConditionCheckBox.IsChecked == true;

            if (!hasTime && !hasProcess)
            {
                return;
            }

            var isProcessByName = matchTypeCombo.SelectedIndex == 0;
            var processValue = hasProcess
                ? (isProcessByName ? processAutoComplete.Text : titleValueInput.Text)
                : null;

            if (hasProcess && string.IsNullOrWhiteSpace(processValue))
            {
                return;
            }

            var scopeKeys = allMonitorsCheckBox.IsChecked == true
                ? new List<string>()
                : monitorCheckBoxes.Where(t => t.CheckBox.IsChecked == true).Select(t => BrightnessController.GetMonitorKey(t.Monitor)).ToList();

            var (timeSegments, isTimeAlwaysActive) = hasTime
                ? getTimeSegments()
                : (new List<TimeSegment>(), false);

            if (editingRule is { } rule)
            {
                // Правило — ссылочный тип, уже лежащий в automationSettings.Rules по
                // своему индексу: правим поля НА МЕСТЕ, а не удаляем/пересоздаём —
                // иначе потерялась бы позиция в списке (а она участвует в тай-брейке
                // приоритета, см. PLAN_FP10 Фаза 1 п.6).
                rule.Name = nameInput.Text?.Trim() ?? "";
                rule.TimeSegments = timeSegments;
                rule.IsTimeAlwaysActive = isTimeAlwaysActive;
                rule.ProcessMatchType = isProcessByName ? AppMatchType.ProcessName : AppMatchType.WindowTitle;
                rule.ProcessMatchValue = hasProcess ? processValue!.Trim() : null;
                rule.Combinator = andToggle.IsChecked == true ? AutomationCombinator.And : AutomationCombinator.Or;
                rule.Percent = (int)(percentInput.Value ?? 50);
                rule.Priority = priorityCheckBox.IsChecked == true ? (int)(priorityInput.Value ?? 10) : null;
                rule.MonitorKeys = scopeKeys;
            }
            else
            {
                automationSettings.Rules.Add(new AutomationRule
                {
                    Name = nameInput.Text?.Trim() ?? "",
                    TimeSegments = timeSegments,
                    IsTimeAlwaysActive = isTimeAlwaysActive,
                    ProcessMatchType = isProcessByName ? AppMatchType.ProcessName : AppMatchType.WindowTitle,
                    ProcessMatchValue = hasProcess ? processValue!.Trim() : null,
                    Combinator = andToggle.IsChecked == true ? AutomationCombinator.And : AutomationCombinator.Or,
                    Percent = (int)(percentInput.Value ?? 50),
                    Priority = priorityCheckBox.IsChecked == true ? (int)(priorityInput.Value ?? 10) : null,
                    MonitorKeys = scopeKeys,
                });
            }

            automationStore.Save(automationSettings);
            RefreshRulesList();
            RefreshActiveNow();
            ResetForm();
        };

        var formButtonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        formButtonsRow.Children.Add(addRuleButton);
        formButtonsRow.Children.Add(cancelEditButton);
        newRulePanel.Children.Add(formButtonsRow);

        root.Children.Add(newRuleCard);

        // FP17 Фаза 4, п.15 — перенесено в самый низ вкладки (было сразу под
        // переключателем "Включить автоматизацию"), заголовок переименован
        // "Сейчас активно" → "Состояние" (согласовано с пользователем).
        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        root.Children.Add(new TextBlock { Text = "Состояние", FontWeight = Avalonia.Media.FontWeight.Bold });
        root.Children.Add(activeNowPanel);
    }

    private void BuildIdleTab(StackPanel root)
    {
        var idleStore = new JsonFileIdleSettingsStore();
        var idleSettings = idleStore.Load();

        var enabledCheckBox = new CheckBox { Content = "Включить приглушение по бездействию", IsChecked = idleSettings.IsEnabled };
        ToolTip.SetTip(enabledCheckBox, "Приглушает яркость ВСЕХ мониторов разом после N минут без клавиатуры/мыши " +
            "и восстанавливает при возврате активности (актуальное значение расписания или профиля приложения, если применимо — не устаревший снимок).");
        root.Children.Add(enabledCheckBox);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        var timeoutRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        timeoutRow.Children.Add(new TextBlock { Text = "Таймаут простоя:", VerticalAlignment = VerticalAlignment.Center, Width = 180 });
        var timeoutInput = BuildNumericStepper(1, 180, idleSettings.IdleTimeoutMinutes, "мин");
        timeoutRow.Children.Add(timeoutInput);
        root.Children.Add(timeoutRow);

        var dimPercentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        dimPercentRow.Children.Add(new TextBlock { Text = "Яркость при простое:", VerticalAlignment = VerticalAlignment.Center, Width = 180 });
        var dimPercentInput = BuildNumericStepper(0, 100, idleSettings.DimPercent, "%");
        dimPercentRow.Children.Add(dimPercentInput);
        root.Children.Add(dimPercentRow);

        var pollIntervalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var pollIntervalLabel = new TextBlock { Text = "Проверка простоя:", VerticalAlignment = VerticalAlignment.Center, Width = 180 };
        ToolTip.SetTip(pollIntervalLabel, "Как часто проверяется, не пошевелили ли вы мышью/клавиатурой. Меньше — " +
            "отзывчивее восстановление после простоя, но чуть чаще фоновая проверка.");
        pollIntervalRow.Children.Add(pollIntervalLabel);
        var pollIntervalInput = BuildNumericStepper(1, 60, idleSettings.PollIntervalSeconds, "сек");
        pollIntervalRow.Children.Add(pollIntervalInput);
        root.Children.Add(pollIntervalRow);

        enabledCheckBox.IsCheckedChanged += (_, _) =>
        {
            idleSettings.IsEnabled = enabledCheckBox.IsChecked ?? true;
            idleStore.Save(idleSettings);
        };
        timeoutInput.ValueChanged += (_, _) =>
        {
            idleSettings.IdleTimeoutMinutes = (int)(timeoutInput.Value ?? 5);
            idleStore.Save(idleSettings);
        };
        dimPercentInput.ValueChanged += (_, _) =>
        {
            idleSettings.DimPercent = (int)(dimPercentInput.Value ?? 10);
            idleStore.Save(idleSettings);
        };
        pollIntervalInput.ValueChanged += (_, _) =>
        {
            idleSettings.PollIntervalSeconds = (int)(pollIntervalInput.Value ?? 1);
            idleStore.Save(idleSettings);
            // Иначе новый интервал подхватится только на следующем перезапуске
            // приложения — таймер уже создан со старым значением.
            _idleEngine?.Start();
        };

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        // FP17 Фаза 4, п.16 — "Сейчас" + значение на двух строках → одна
        // строка "Состояние: ..." (согласовано с пользователем).
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        statusRow.Children.Add(new TextBlock { Text = "Состояние:", FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(statusText);
        root.Children.Add(statusRow);

        void RefreshStatus()
        {
            statusText.Text = _idleEngine?.IsDimmed == true
                ? "Приглушено по бездействию"
                : "Активно (не приглушено)";
        }

        RefreshStatus();
        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        statusTimer.Tick += (_, _) => RefreshStatus();
        statusTimer.Start();
    }

    private ComboBox BuildThemeSelector()
    {
        var options = new (AppThemePreference Value, string Label)[]
        {
            (AppThemePreference.System, "Системная"),
            (AppThemePreference.Light, "Светлая"),
            (AppThemePreference.Dark, "Тёмная"),
        };

        var comboBox = new ComboBox
        {
            ItemsSource = options.Select(o => o.Label).ToList(),
            SelectedIndex = Array.FindIndex(options, o => o.Value == _appSettings.Theme),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedIndex < 0)
            {
                return;
            }

            var selected = options[comboBox.SelectedIndex].Value;
            App.ApplyTheme(selected);
            _appSettings.Theme = selected;
            _appSettingsStore.Save(_appSettings);
        };

        return comboBox;
    }

    // Слайдер "прилипает" к настраиваемому шагу (см. "Шаг слайдеров" выше), а
    // кнопки ± дают точную подстройку на 1% в обход прилипания — например, до 29
    // удобнее дойти кнопкой от 30, чем медленно тащить слайдер между тиками.
    //
    // Во время реального перетаскивания слайдер пересекает много тиков подряд —
    // если писать в железо на КАЖДЫЙ тик, капризные DDC/CI-мониторы (см. FP1/FP2)
    // захлёбываются частыми командами и перестают отвечать. Поэтому во время
    // перетаскивания меняется только подпись (live), а в железо значение
    // применяется один раз — по отпусканию кнопки мыши, по клику ±, или когда
    // слайдер двигает не пользователь напрямую (например, синхронизация от
    // общего слайдера "Все сразу").
    // Internal — переиспользуется GlobalSliderPopup (FP9 Фаза 2), не только этим окном.
    // nameColumnWidth — под длинные названия мониторов в узком поповере название
    // едет "бегущей строкой", а не обрезается; в широком окне настроек места и так
    // хватает, поэтому запас пошире и анимация практически никогда не включается.
    // Единственный вызывающий (GlobalSliderPopup) — окно уже отмасштабировано
    // (FP17 Фаза 3), поэтому 15px зашито прямо здесь, без отдельного параметра.
    internal static Slider AddSliderRow(StackPanel root, string label, int initialPercent, int tickStep, Action<int> onChanged, double nameColumnWidth = 360, bool allowForceResync = false)
        => AddSliderRow(root, BuildMarqueeLabel(label, nameColumnWidth, fontSize: 15), initialPercent, tickStep, onChanged, allowForceResync);

    // Перегрузка, принимающая уже готовый control вместо голой строки — нужна
    // MonitorSlidersPopup (FP8/переименование мониторов), где название должно быть
    // кликабельным (переключается в поле ввода) и нести маленькую иконку пера, а
    // не просто быть бегущей строкой без взаимодействия.
    // trailingAccessory — необязательный контрол (FP17 Фаза 3: замочек блокировки
    // монитора), встаёт СЛЕВА от процента, единой группой у правого края строки —
    // согласовано с пользователем по макету (вариант C: "справа, у процента").
    // Только MonitorSlidersPopup передаёт значение; остальные вызовы — null,
    // поведение не меняется.
    internal static Slider AddSliderRow(StackPanel root, Control nameLabel, int initialPercent, int tickStep, Action<int> onChanged, bool allowForceResync = false, Control? trailingAccessory = null)
    {
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        // Ширина ФИКСИРОВАНА (не по содержимому) — иначе "Auto"-колонка меняла размер
        // на каждый тик процента (9% уже, 100% шире), сосед в "*"-колонке от этого
        // ужимался/расширялся и дёргался при каждом изменении яркости.
        // FP17 Фаза 3 — 52/15px вместо 42/по умолчанию (масштаб +23%, согласовано).
        var percentLabel = new TextBlock { Text = $"{initialPercent}%", Width = 52, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameLabel, 0);
        headerRow.Children.Add(nameLabel);

        if (trailingAccessory is null)
        {
            Grid.SetColumn(percentLabel, 1);
            headerRow.Children.Add(percentLabel);
        }
        else
        {
            var trailingGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            trailingGroup.Children.Add(trailingAccessory);
            trailingGroup.Children.Add(percentLabel);
            Grid.SetColumn(trailingGroup, 1);
            headerRow.Children.Add(trailingGroup);
        }

        // FP15 — клик по проценту принудительно ПЕРЕОТПРАВЛЯЕТ ТЕКУЩЕЕ
        // значение на все мониторы, без изменения самого числа: тот же
        // эффект, что раньше пользователь получал вручную через "−1", потом
        // "+1" (значение визуально не меняется, но в железо уходит новая
        // команда) — нужно, если какой-то монитор физически "разъехался" со
        // значением, которое помнит слайдер (например, яркость подкрутили
        // прямо на самом мониторе кнопками). Это НЕ поле ввода — просто
        // повторный вызов onChanged с уже текущим значением. Пользователь
        // явно попросил ТОЛЬКО для глобального слайдера ("Все мониторы" в
        // GlobalSliderPopup), не для слайдеров по отдельным мониторам —
        // отсюда параметр allowForceResync, а не безусловно для всех
        // вызовов AddSliderRow.
        if (allowForceResync)
        {
            percentLabel.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            ToolTip.SetTip(percentLabel, "Нажмите, чтобы заново применить это значение ко всем мониторам");
        }

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = initialPercent,
            TickFrequency = tickStep,
            IsSnapToTickEnabled = true,
        };
        // FP17 Фаза 3 — увеличенный трек/бегунок (+23%), только для этих двух
        // окон: остальные слайдеры приложения (вкладки SettingsWindow) продолжают
        // использовать обычный {x:Type Slider} без правки, AddSliderRow — их
        // единственный вызывающий (см. комментарий у trailingAccessory выше).
        if (root.TryFindResource("ScaledSliderTheme", out var scaledSliderTheme) && scaledSliderTheme is Avalonia.Styling.ControlTheme controlTheme)
        {
            slider.Theme = controlTheme;
        }

        // Всплывающий пузырёк с процентом прямо над кружком слайдера — статичная
        // подпись "название: процент" не влезает в узкий поповер (см. percentLabel
        // выше — по той же причине она вынесена отдельно), а пузырёк даёт точную
        // обратную связь именно там, где палец/курсор тянет слайдер.
        //
        // Ширина/высота ФИКСИРОВАНЫ (не подстраиваются под текст) — раньше центр
        // считался через bubble.Bounds.Width, а она меняется в зависимости от
        // количества цифр (9% против 100%), из-за чего пузырёк ощутимо "шатался"
        // при перетаскивании. С фиксированным размером делитель в формуле центрирования
        // всегда один и тот же — дрожи по X больше нет.
        // FP17 Фаза 3 — 42/25/12/4 вместо 34/20/10/3 (масштаб +23%, согласовано).
        const double bubbleWidth = 42;
        const double bubbleBodyHeight = 25;
        const double bubbleTailSize = 12;
        const double bubbleGap = 4;
        const double bubbleTotalHeight = bubbleBodyHeight + bubbleTailSize / 2;

        var bubbleText = new TextBlock
        {
            Text = $"{initialPercent}%",
            Foreground = Avalonia.Media.Brushes.White,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bubbleBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xE8, 0x30, 0x30, 0x30));
        var bubbleBody = new Border
        {
            Width = bubbleWidth,
            Height = bubbleBodyHeight,
            // FP17 — сведено к "мелкому" уровню шкалы радиусов (8), было 5.
            CornerRadius = new CornerRadius(8),
            Background = bubbleBrush,
            Child = bubbleText,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // Повёрнутый на 45° квадрат: верхняя половина спрятана ПОД телом пузырька
        // (добавлен в Panel раньше него, значит рисуется ниже по z-order), снизу
        // торчит только острый кончик — классический приём для "хвостика" подсказки,
        // конец которого всегда точно над кружком слайдера.
        var bubbleTail = new Border
        {
            Width = bubbleTailSize,
            Height = bubbleTailSize,
            Background = bubbleBrush,
            RenderTransform = new Avalonia.Media.RotateTransform(45),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness((bubbleWidth - bubbleTailSize) / 2, bubbleBodyHeight - bubbleTailSize / 2, 0, 0),
        };
        var bubble = new Panel
        {
            Width = bubbleWidth,
            Height = bubbleTotalHeight,
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        bubble.Children.Add(bubbleTail);
        bubble.Children.Add(bubbleBody);

        // Panel (не StackPanel) — чтобы пузырёк мог рисоваться поверх и НАД строкой
        // слайдера, не будучи прижатым к её собственным границам. Объявлен здесь
        // (заполнится ниже), чтобы TranslatePoint пересчитывал позицию пузырька
        // именно в его системе координат, а не в системе координат Grid со слайдером.
        var sliderHost = new Panel();

        Thumb? thumb = null;

        void RepositionBubble()
        {
            thumb ??= slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            if (thumb is null)
            {
                return;
            }

            // Y=0 в системе координат самого thumb — это его верхний край; переводим
            // именно эту точку, чтобы хвостик всегда указывал строго на верх кружка,
            // а не на его центр (тогда кончик "прятался" бы внутри кружка).
            var top = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, 0), sliderHost);
            if (top is not { } point)
            {
                return;
            }

            bubble.Margin = new Thickness(point.X - bubbleWidth / 2, point.Y - bubbleGap - bubbleTotalHeight, 0, 0);
        }

        var isDragging = false;

        // Раньше пузырёк переставлялся сразу внутри обработчика PropertyChanged —
        // но на этот момент Avalonia ещё не успела ЗАНОВО РАСПОЛОЖИТЬ сам кружок
        // (Value уже новое, а Arrange кружка происходит на СЛЕДУЮЩЕМ проходе
        // layout) — из-за этого пузырёк читал СТАРУЮ позицию кружка, на шаг позади
        // реальной, и при быстром перетаскивании туда-сюда это выглядело как
        // дрожь/шатание. LayoutUpdated срабатывает уже ПОСЛЕ фактического Arrange —
        // подписка живёт, только пока пузырёк реально виден.
        void OnSliderLayoutUpdated(object? sender, EventArgs e) => RepositionBubble();

        void UpdateBubbleVisibility()
        {
            var shouldShow = isDragging || slider.IsPointerOver;
            if (shouldShow == bubble.IsVisible)
            {
                return;
            }

            bubble.IsVisible = shouldShow;
            if (shouldShow)
            {
                slider.LayoutUpdated += OnSliderLayoutUpdated;
                RepositionBubble();
            }
            else
            {
                slider.LayoutUpdated -= OnSliderLayoutUpdated;
            }
        }

        slider.PointerEntered += (_, _) => UpdateBubbleVisibility();
        slider.PointerExited += (_, _) => UpdateBubbleVisibility();

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
            {
                return;
            }

            var percent = (int)slider.Value;
            percentLabel.Text = $"{percent}%";
            bubbleText.Text = $"{percent}%";

            if (!isDragging)
            {
                onChanged(percent);
            }
        };

        slider.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            isDragging = true;
            UpdateBubbleVisibility();
        }, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            isDragging = false;
            onChanged((int)slider.Value);
            UpdateBubbleVisibility();
        }, handledEventsToo: true);

        if (allowForceResync)
        {
            percentLabel.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                e.Handled = true;
                onChanged((int)slider.Value);
            }, handledEventsToo: true);
        }

        // FP15 — колесо мыши над строкой слайдера меняет значение, по
        // аналогии со скроллом над иконкой трея (FP2). Шаг — tickStep (тот
        // же параметр, что уже используется для прилипания при
        // перетаскивании, отдельной настройки не заводили). Shift — точная
        // подстройка ±1% в обход шага. Пишем в
        // slider.Value, а не напрямую вызываем onChanged — тогда срабатывает
        // тот же PropertyChanged-обработчик выше (раз isDragging=false,
        // onChanged вызовется сразу на каждый тик), без дублирования кода
        // применения; коалесцирование в железо — забота вызывающей стороны
        // (GlobalSliderPopup/MonitorSlidersPopup уже оборачивают onChanged в
        // CoalescingBrightnessApplier, как и скролл над иконкой трея).
        //
        // Обработчик висит НЕ на самом слайдере, а на широкой обёртке ВСЕЙ
        // строки (подпись+процент сверху, минус/слайдер/плюс снизу) — сам
        // визуальный трек слайдера слишком тонкий, пользователю было трудно
        // "попасть" в него курсором именно для скролла (найдено по живому
        // фидбеку: "мышка не считается над активной областью").
        void HandleWheel(object? sender, PointerWheelEventArgs e)
        {
            e.Handled = true;
            var notches = Math.Sign(e.Delta.Y);
            if (notches == 0)
            {
                return;
            }

            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : tickStep;
            slider.Value = Math.Clamp(slider.Value + notches * step, slider.Minimum, slider.Maximum);
        }

        // Padding=0 — общий Button ControlTheme (FP12) задаёт Padding="14,8" для
        // обычных текстовых кнопок ("Сохранить" и т.п.); в узкой квадратной кнопке
        // это почти не оставляет места самому символу "−"/"+".
        // FP17 Фаза 4, п.4 — векторные глифы вместо текстовых "−"/"+" (тот же
        // класс риска, что уже реально стрельнул с "▼": не гарантирован рисунок
        // символа шрифтом кнопки в узкой кнопке).
        // AddSliderRow — static-метод (переиспользуется из SettingsWindow и
        // GlobalSliderPopup/MonitorSlidersPopup), поэтому цвет резолвится через
        // root.TryFindResource (root уже подключён к дереву окна), а не через
        // ResolveThemeColor (тот — инстанс-метод конкретно SettingsWindow).
        var stepperGlyphColor = root.TryFindResource("AppInk", out var appInkRes) && appInkRes is Avalonia.Media.SolidColorBrush appInkBrush
            ? appInkBrush.Color
            : Avalonia.Media.Colors.White;
        var minusGlyphBrush = new Avalonia.Media.SolidColorBrush(stepperGlyphColor);
        var plusGlyphBrush = new Avalonia.Media.SolidColorBrush(stepperGlyphColor);
        // FP17 Фаза 3 — 38/12/1.8 вместо 32/10/1.6 (масштаб +23%, согласовано).
        var minusButton = new Button
        {
            Content = BuildMinusGlyph(12, 1.8, minusGlyphBrush),
            Width = 38,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var plusButton = new Button
        {
            Content = BuildPlusGlyph(12, 1.8, plusGlyphBrush),
            Width = 38,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        minusButton.Click += (_, _) => slider.Value = Math.Clamp(slider.Value - 1, slider.Minimum, slider.Maximum);
        plusButton.Click += (_, _) => slider.Value = Math.Clamp(slider.Value + 1, slider.Minimum, slider.Maximum);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        Grid.SetColumn(minusButton, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(plusButton, 2);
        row.Children.Add(minusButton);
        row.Children.Add(slider);
        row.Children.Add(plusButton);

        sliderHost.Children.Add(row);
        sliderHost.Children.Add(bubble);

        var rowContainer = new StackPanel();
        rowContainer.Children.Add(headerRow);
        rowContainer.Children.Add(sliderHost);
        // StackPanel без явного Background не участвует в хит-тесте за
        // пределами своих детей (та же история, что уже чинили для кнопки
        // "+" — Border/Panel без Background не ловит клики/скролл в
        // "пустых" промежутках между children), поэтому без этого колесо
        // между headerRow и sliderHost попросту не долетало бы до обработчика.
        rowContainer.Background = Avalonia.Media.Brushes.Transparent;
        rowContainer.AddHandler(InputElement.PointerWheelChangedEvent, HandleWheel, handledEventsToo: true);

        root.Children.Add(rowContainer);
        return slider;
    }

    // Название монитора едет туда-обратно бегущей строкой от начала до самого конца,
    // только если реально не помещается в отведённую ширину — короткие названия
    // остаются статичными без анимации.
    //
    // Ширина текста меряется через FormattedText (полностью отдельная, "бумажная"
    // операция) — НЕ через textBlock.Measure(...): вызов Measure() напрямую на
    // TextBlock, который уже присоединён к живому дереву и участвует в обычном
    // цикле layout, сбивает его с толку и портит реальную раскладку (ровно это и
    // сломало строку "Все мониторы" — текст начал переноситься/резаться). Сам
    // шрифт/размер для FormattedText читаются ЛЕНИВО на первом тике таймера (а не
    // сразу при создании) — до присоединения к дереву стиль темы ещё не применён,
    // и раннее чтение FontFamily/FontSize даёт метрики "по умолчанию", не совпадающие
    // с реально отрисованными (отсюда была неверная амплитуда прокрутки).
    // Раньше это был RenderTransform (сдвиг X) + Border с ClipToBounds — на практике
    // ломало раскладку всей строки (текст "убегал" на соседнюю строку окна ниже),
    // видимо из-за того, как Avalonia сочетает трансформацию рендера с обрезкой у
    // родителя. Полностью отказались от transform/clip: вместо визуального сдвига
    // готового TextBlock просто подменяем САМ ТЕКСТ на видимое окно символов (как
    // старая бегущая строка на LCD-табло) — это не может сломать раскладку, потому
    // что каждый кадр — это просто обычная строка, умещающаяся в отведённую ширину.
    // Internal — переиспользуется MonitorSlidersPopup (FP8/переименование мониторов)
    // для построения кликабельного названия монитора, не только этим окном.
    // Число колонок для галереи форм иконки трея (см. BuildTrayIconSection) — не
    // просто "сколько влезает по ширине", а лучшее среди [maxColumns-1, maxColumns]
    // по заполненности последней строки. Без этого ограничения снизу (только -1,
    // не перебор всех вариантов до 1) идеальным "нулевым остатком" всегда выглядит
    // 1 колонка (последняя "строка" из одного элемента тривиально заполнена целиком) —
    // формально верно, но превращает галерею в бесполезный вертикальный список.
    // Например, 6 форм при maxColumns=4 лягут в 3 колонки (3+3), а не в 4 (4+2).
    private static int ComputeOptimalColumns(int totalCount, int maxColumns)
    {
        if (totalCount <= 0)
        {
            return Math.Max(1, maxColumns);
        }

        var minColumns = Math.Max(1, maxColumns - 1);
        var best = maxColumns;
        var bestPadding = int.MaxValue;

        for (var cols = maxColumns; cols >= minColumns; cols--)
        {
            var rows = (int)Math.Ceiling(totalCount / (double)cols);
            var padding = cols * rows - totalCount;

            if (padding < bestPadding || (padding == bestPadding && cols > best))
            {
                bestPadding = padding;
                best = cols;
            }
        }

        return best;
    }

    // fontSize — необязательный явный override (FP17 Фаза 3: окна слайдеров
    // используют увеличенный шрифт 15px, без этого параметра остальные вызовы
    // продолжают работать со стандартным размером темы, как и раньше).
    internal static Control BuildMarqueeLabel(string text, double width, double? fontSize = null)
    {
        var textBlock = new TextBlock
        {
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        };
        if (fontSize.HasValue)
        {
            textBlock.FontSize = fontSize.Value;
        }

        // Сколько символов от offset реально помещается в width — раньше это была
        // грубая оценка "7px на символ", независимая от реального шрифта. Проблема:
        // заглавные буквы и цифры ("...DISPLAY3)") заметно шире этой средней
        // оценки, поэтому реально отрисованная строка оказывалась ШИРЕ отведённого
        // места, и Avalonia молча обрезала лишний хвост — обычно как раз последний
        // символ (закрывающую скобку), хотя сама логика прокрутки считала его
        // показанным целиком.
        //
        // Первая попытка честного измерения мерила текст через САМ textBlock — но у
        // него уже задано фиксированное Width, а явно заданное Width у Avalonia
        // ограничивает результат Measure() сверху: DesiredSize.Width никогда не
        // превышал width, даже когда реальный текст был шире, — из-за этого
        // "проверка" всегда считала, что текст помещается целиком, и анимация вообще
        // переставала запускаться (текст просто показывался статично обрезанным).
        // Измеряем поэтому ОТДЕЛЬНЫМ TextBlock без заданной ширины — со скопированным
        // шрифтом реального лейбла (стиль темы к моменту AttachedToVisualTree уже
        // точно применён), но без ограничения, которое мешало бы Measure() увидеть
        // реальный размер контента.
        var measurer = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.NoWrap };

        int CountFittingChars(int offset)
        {
            var maxChars = text.Length - offset;
            for (var count = maxChars; count > 1; count--)
            {
                measurer.Text = text.Substring(offset, count);
                measurer.Measure(Size.Infinity);
                if (measurer.DesiredSize.Width <= width)
                {
                    return count;
                }
            }

            return Math.Min(1, maxChars);
        }

        DispatcherTimer? timer = null;
        var initialized = false;

        textBlock.AttachedToVisualTree += (_, _) =>
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            measurer.FontFamily = textBlock.FontFamily;
            measurer.FontSize = textBlock.FontSize;
            measurer.FontWeight = textBlock.FontWeight;
            measurer.FontStyle = textBlock.FontStyle;

            if (CountFittingChars(0) >= text.Length)
            {
                textBlock.Text = text;
                return;
            }

            var offset = 0;
            var forward = true;
            var pauseTicksRemaining = 0;
            const int pauseTicksAtEnds = 8; // ~1.6с на паузу, чтобы конец/начало успевали прочитаться

            // Максимальный сдвиг вправо — минимальный offset, при котором ОСТАТОК
            // строки уже помещается в width целиком (дальше двигать некуда, конец
            // текста и так весь виден).
            var maxOffset = 0;
            while (maxOffset < text.Length && CountFittingChars(maxOffset) < text.Length - maxOffset)
            {
                maxOffset++;
            }

            void Refresh() => textBlock.Text = text.Substring(offset, CountFittingChars(offset));
            Refresh();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                if (pauseTicksRemaining > 0)
                {
                    pauseTicksRemaining--;
                    return;
                }

                offset += forward ? 1 : -1;
                if (offset >= maxOffset)
                {
                    offset = maxOffset;
                    forward = false;
                    pauseTicksRemaining = pauseTicksAtEnds;
                }
                else if (offset <= 0)
                {
                    offset = 0;
                    forward = true;
                    pauseTicksRemaining = pauseTicksAtEnds;
                }

                Refresh();
            };
            timer.Start();
        };

        // Иначе таймер продолжит тикать вечно в фоне после закрытия окна —
        // строка больше не в дереве, значения меняются, но их никто не видит.
        textBlock.DetachedFromVisualTree += (_, _) => timer?.Stop();

        return textBlock;
    }
}
