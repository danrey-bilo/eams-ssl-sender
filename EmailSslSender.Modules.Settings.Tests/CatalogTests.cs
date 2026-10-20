using EmailSslSender.Modules.Settings.Catalog;
using EmailSslSender.Modules.Settings.Settings;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Settings.Tests;

/// <summary>
/// Справочник настроек: точка расширения и спор за имя.
/// </summary>
/// <remarks>
/// ⚠ <b>Смысл первой группы — в том, что в модуле настроек не меняется НИ СТРОКИ.</b> Модуль,
/// в котором приходится что-то дописать при появлении соседа, нарушает правило 1: одиннадцатый
/// модуль потребовал бы открыть первые десять. Образец того же приёма —
/// <c>EmailSslSender.Modules.Audit.Tests/ExtensibilityTests.cs</c>.
///
/// ⚠ <b>Жёсткого числа объявлений здесь нет намеренно.</b> К 16-му этапу настройки объявляют
/// семеро, к 21-му будет больше, и тест, сверяющий итог с константой, краснел бы на каждом
/// следующем этапе, ничего при этом не находя. Проверяется присутствие своего и чужого, а не
/// счёт.
/// </remarks>
public sealed class CatalogTests
{
    /// <summary>Чужой модуль со своими ключами попадает в справочник.</summary>
    [Fact]
    public void Объявления_чужого_модуля_попадают_в_справочник()
    {
        var catalog = new SettingsCatalog([new SettingsSettingDeclarations(), new ЧужойМодуль()]);

        catalog.Find("stranger.first").ShouldNotBeNull();
        catalog.Find("stranger.second").ShouldNotBeNull();
        catalog.Find("ui.locale").ShouldNotBeNull();
    }

    /// <summary>Ключа, которого никто не объявлял, в справочнике нет.</summary>
    /// <remarks>
    /// ⚠ Зеркальный факт, и без него первый доказывает лишь, что справочник что-то содержит.
    /// Именно на этом различии стоит весь модуль: «строки в базе нет, действует умолчание» и
    /// «такого ключа никто не объявлял» — разные ответы, и без справочника они выглядят
    /// одинаково.
    /// </remarks>
    [Fact]
    public void Незаявленного_ключа_в_справочнике_нет()
    {
        var catalog = new SettingsCatalog([new SettingsSettingDeclarations()]);

        catalog.Find("ui.locale.typo").ShouldBeNull();
        catalog.Find(string.Empty).ShouldBeNull();
    }

    /// <summary>Порядок — по разделу, потом по имени.</summary>
    /// <remarks>
    /// Стабильный порядок избавляет экран от сортировки, а тесты — от зависимости от порядка
    /// регистрации модулей в хосте: контейнер отдаёт поставщиков в порядке регистрации, и
    /// перестановка двух строк в <c>Program.cs</c> иначе меняла бы выдачу API.
    /// </remarks>
    [Fact]
    public void Порядок_не_зависит_от_порядка_регистрации()
    {
        var прямой = new SettingsCatalog([new SettingsSettingDeclarations(), new ЧужойМодуль()]);
        var обратный = new SettingsCatalog([new ЧужойМодуль(), new SettingsSettingDeclarations()]);

        прямой.All.Select(d => d.Name).ShouldBe(обратный.All.Select(d => d.Name));
        прямой.All.Select(d => d.Category).ShouldBeInOrder(SortDirection.Ascending);
    }

    /// <summary>Два модуля с одним именем и разным содержимым роняют сборку справочника.</summary>
    /// <remarks>
    /// ⚠ <b>Роняет СТАРТ, а не первый запрос</b> — за это отвечает <c>SettingsCatalogService</c>,
    /// трогающий справочник в <c>StartAsync</c>. Разница по последствиям большая: отказ при
    /// старте останавливает выкатку и виден одной строкой; отказ при первом обращении оставляет
    /// систему поднятой, проверку живости зелёной, а экран настроек — пятисоткой.
    /// </remarks>
    [Fact]
    public void Спор_двух_модулей_за_имя_роняет_справочник()
    {
        var отказ = Should.Throw<InvalidOperationException>(
            () => new SettingsCatalog([new ЧужойМодуль(), new СпорщикЗаИмя()]));

        // Сообщение обязано назвать спорное имя: без него разбираться придётся перебором
        // всех объявлений всех модулей.
        отказ.Message.ShouldContain("stranger.first");
    }

    /// <summary>Одинаковое объявление одного имени спором не считается.</summary>
    /// <remarks>
    /// ⚠ <b>И это не послабление, а необходимость.</b> Четыре ключа <c>auth.*</c> объявляют и
    /// <c>Identity</c>, и <c>Tokens</c>: сессия принадлежит второму, а срок её проверяет
    /// первый. Заставить один модуль ссылаться на другой ради текста подписи значило бы
    /// нарушить правило 1 ради косметики; заставить справочник падать на этом — не дать системе
    /// стартовать вовсе.
    /// </remarks>
    [Fact]
    public void Повтор_одинакового_объявления_законен()
    {
        var catalog = new SettingsCatalog([new ЧужойМодуль(), new ТочнаяКопия()]);

        catalog.Find("stranger.first").ShouldNotBeNull();
        catalog.All.Count(d => d.Name == "stranger.first").ShouldBe(1);
    }

    /// <summary>Расхождение в подписи спором не считается, расхождение в границе — считается.</summary>
    /// <remarks>
    /// ⚠ Граница между «спор» и «повтор» проведена по последствиям, а не по строгости.
    /// Разный текст пояснения не ломает ничего: экран покажет первый, и оба верны. Разная
    /// граница ломает всё: экран предложит значение, которое проверка не примет, — настройку
    /// можно будет задать и нельзя прочитать обратно.
    /// </remarks>
    [Fact]
    public void Спор_меряется_содержимым_а_не_текстом()
    {
        Should.NotThrow(() => new SettingsCatalog([new ЧужойМодуль(), new ДругаяПодпись()]));
        Should.Throw<InvalidOperationException>(
            () => new SettingsCatalog([new ЧужойМодуль(), new ДругаяГраница()]));
    }

    /// <summary>Модуль настроек объявляет свою — точка расширения проверена на себе.</summary>
    [Fact]
    public void Свои_настройки_объявлены_и_они_общие()
    {
        var catalog = new SettingsCatalog([new SettingsSettingDeclarations()]);

        // ⚠ Личных предпочтений в этой установке нет: язык писем один на всех.
        catalog.All.Count.ShouldBe(1);
        catalog.All.ShouldAllBe(d => d.Category == "ui");
        catalog.All.ShouldAllBe(d => !d.IsSecurity);
    }

    /// <summary>У настроек с вариантами умолчание входит в список вариантов.</summary>
    /// <remarks>
    /// ⚠ Мелочь на вид, и ловится она только так: умолчание вне списка означает выпадающий
    /// список, в котором не выбрано ничего, и первое же сохранение молча меняет значение.
    /// </remarks>
    [Fact]
    public void Умолчание_входит_в_список_вариантов()
    {
        var catalog = new SettingsCatalog([new SettingsSettingDeclarations()]);

        foreach (var declaration in catalog.All.Where(d => d.Choices.Count > 0))
        {
            declaration.Choices.ShouldContain(
                (string)declaration.DefaultValue!,
                $"умолчание настройки {declaration.Name} не входит в её же список вариантов");
        }
    }

    // --- подделки -------------------------------------------------------------------------

    private sealed class ЧужойМодуль : ISettingDeclarationProvider
    {
        public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
        [
            new SettingKey<int>("stranger.first", 1, "First", "", "stranger", Minimum: 1, Maximum: 10),
            new SettingKey<bool>("stranger.second", true, "Second", "", "stranger"),
        ];
    }

    /// <summary>То же имя, другое умолчание и другая граница.</summary>
    private sealed class СпорщикЗаИмя : ISettingDeclarationProvider
    {
        public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
        [
            new SettingKey<int>("stranger.first", 99, "First", "", "stranger", Minimum: 1, Maximum: 1000),
        ];
    }

    /// <summary>Ровно то же объявление — так делают Identity и Tokens.</summary>
    private sealed class ТочнаяКопия : ISettingDeclarationProvider
    {
        public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
        [
            new SettingKey<int>("stranger.first", 1, "First", "", "stranger", Minimum: 1, Maximum: 10),
        ];
    }

    /// <summary>Тот же ключ, другой текст — законно.</summary>
    private sealed class ДругаяПодпись : ISettingDeclarationProvider
    {
        public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
        [
            new SettingKey<int>(
                "stranger.first", 1, "Первое поле", "иначе сказано", "stranger",
                Minimum: 1, Maximum: 10),
        ];
    }

    /// <summary>Тот же ключ, другая верхняя граница — спор.</summary>
    private sealed class ДругаяГраница : ISettingDeclarationProvider
    {
        public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
        [
            new SettingKey<int>("stranger.first", 1, "First", "", "stranger", Minimum: 1, Maximum: 11),
        ];
    }
}
