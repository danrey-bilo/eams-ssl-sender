using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Mail;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Справочник писем: точка расширения, полнота языков и подстановка.
/// </summary>
/// <remarks>
/// ⚠ <b>Смысл первой группы — в том, что в модуле почты не меняется НИ СТРОКИ.</b> Письма
/// объявляет тот, кто их шлёт: приглашение и сброс принадлежат <c>Identity</c>, а не почте.
/// Модуль, который приходится править при появлении нового письма, нарушает правило 1.
/// </remarks>
public sealed class TemplateTests
{
    [Fact]
    public void Чужой_модуль_добавляет_письмо_не_трогая_почту()
    {
        var справочник = new MailTemplates([new MailOwnTemplates(), new ЧужойМодуль()]);

        справочник.Knows("stranger_letter").ShouldBeTrue();
        справочник.Knows(MailKinds.Test).ShouldBeTrue();
    }

    [Fact]
    public void Незаявленного_вида_справочник_не_знает()
    {
        var справочник = new MailTemplates([new MailOwnTemplates()]);

        справочник.Knows("нет_такого").ShouldBeFalse();

        // ⚠ И отрисовать его нельзя: вид, которого нет, — опечатка вызывающего, а письмо по
        // такой опечатке не уйдёт никогда. Молчаливый пропуск сделал бы это незаметным.
        Should.Throw<InvalidOperationException>(
            () => справочник.Render("нет_такого", MailLocales.Ru, ЗначенийНет));
    }

    [Fact]
    public void Дубль_пары_вид_и_язык_роняет_справочник()
    {
        var отказ = Should.Throw<InvalidOperationException>(
            () => new MailTemplates([new MailOwnTemplates(), new СпорщикЗаТест()]));

        // Сообщение обязано назвать спорную пару: на дюжине писем четырёх модулей без имени
        // разбираться придётся перебором.
        отказ.Message.ShouldContain(MailKinds.Test);
    }

    [Fact]
    public void Неполный_по_языкам_вид_роняет_справочник()
    {
        var отказ = Should.Throw<InvalidOperationException>(
            () => new MailTemplates([new MailOwnTemplates(), new НеполныйМодуль()]));

        // ⚠ И называет, ЧЕЙ вид неполон и каких языков не хватает: идти придётся в чужой
        // модуль, а красный тест без имени на вопрос «куда» не отвечает.
        отказ.Message.ShouldContain("half_letter");
        отказ.Message.ShouldContain(MailLocales.Uk);
    }

    [Fact]
    public void Язык_вне_списка_роняет_справочник()
    {
        // Язык, которого нет в настройке `ui.locale`, выбрать нельзя — значит письмо на нём
        // не придёт никому, и объявление такого текста это опечатка.
        var отказ = Should.Throw<InvalidOperationException>(
            () => new MailTemplates([new ЧужойЯзык()]));

        отказ.Message.ShouldContain("de");
    }

    [Fact]
    public void Свои_три_языка_объявлены()
    {
        var справочник = new MailTemplates([new MailOwnTemplates()]);

        foreach (var язык in MailLocales.All)
        {
            var (тема, тело) = справочник.Render(MailKinds.Test, язык, ЗначенийНет);

            тема.ShouldNotBeNullOrWhiteSpace();
            тело.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Незнакомый_язык_откатывается_на_английский()
    {
        // ⚠ Откат, а не отказ: язык мог появиться в настройке раньше перевода, и молча
        // написать по-английски лучше, чем не написать вовсе.
        var справочник = new MailTemplates([new MailOwnTemplates()]);

        var (тема, _) = справочник.Render(MailKinds.Test, "fr", ЗначенийНет);

        тема.ShouldBe("EAMS ssl sender: mail check");
    }

    /// <summary>
    /// Каждое письмо — с приветствием в начале и благодарностью с именем сервиса в конце.
    /// </summary>
    /// <remarks>
    /// Решение владельца 2026-10-26: письмо человеческое, как уведомления Key Manager Plus. Рамка
    /// одна на все виды, поэтому проверяется на чужом шаблоне — модуль, объявивший письмо, о ней
    /// не знает.
    /// </remarks>
    [Theory]
    [InlineData(MailLocales.Ru, "Здравствуйте!", "Спасибо,")]
    [InlineData(MailLocales.Uk, "Вітаємо!", "Дякуємо,")]
    [InlineData(MailLocales.En, "Hi,", "Thank You,")]
    public void Письмо_начинается_приветствием_и_кончается_подписью(string язык, string привет, string спасибо)
    {
        var справочник = new MailTemplates([new MailOwnTemplates()]);

        var (_, тело) = справочник.Render(MailKinds.Test, язык, ЗначенийНет);

        тело.ShouldStartWith(привет + "\n\n");
        тело.ShouldEndWith("\n\n" + спасибо + "\nEAMS ssl sender\n");
    }

    // --- подстановка ------------------------------------------------------------------------

    [Fact]
    public void Значения_подставляются_в_тему_и_в_тело()
    {
        var справочник = new MailTemplates([new ЧужойМодуль()]);

        var (тема, тело) = справочник.Render(
            "stranger_letter",
            MailLocales.Ru,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = "https://sender.example.com/reset?token=abc",
                ["minutes"] = "60",
            });

        тема.ShouldBe("Тема 60");
        тело.ShouldContain("https://sender.example.com/reset?token=abc");
    }

    [Fact]
    public void Значение_с_фигурными_скобками_не_роняет_и_не_съедается()
    {
        // ⚠ ⚠ Ради этого подстановка написана своим проходом, а не `string.Format`. Тот
        // разбирает фигурные скобки В ЗНАЧЕНИИ — и значение со скобкой либо роняет отрисовку,
        // либо съедается. Подставляемое приходит снаружи: ссылка, код, число минут.
        var справочник = new MailTemplates([new ЧужойМодуль()]);

        var (_, тело) = справочник.Render(
            "stranger_letter",
            MailLocales.Ru,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = "https://host/{странно}/путь",
                ["minutes"] = "5",
            });

        тело.ShouldContain("https://host/{странно}/путь");
    }

    [Fact]
    public void Неизвестный_токен_остаётся_видимым_а_не_пустеет()
    {
        // ⚠ Пустота — это письмо «перейдите по ссылке:» без ссылки, и человек не поймёт, что
        // оно сломано. Оставшийся токен читается как ошибка сразу.
        var справочник = new MailTemplates([new ЧужойМодуль()]);

        var (_, тело) = справочник.Render("stranger_letter", MailLocales.Ru, ЗначенийНет);

        тело.ShouldContain("{link}");
    }

    [Fact]
    public void Незакрытая_скобка_в_шаблоне_не_роняет_отрисовку()
    {
        var справочник = new MailTemplates([new КривойШаблон()]);

        var (_, тело) = справочник.Render("broken_letter", MailLocales.Ru, ЗначенийНет);

        тело.ShouldContain("{нет закрытия");
    }

    // --- подделки ---------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> ЗначенийНет =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed class ЧужойМодуль : IMailTemplateProvider
    {
        public IReadOnlyList<MailTemplate> Templates { get; } =
        [
            new("stranger_letter", MailLocales.Ru, "Тема {minutes}", "Ссылка: {link}"),
            new("stranger_letter", MailLocales.Uk, "Тема {minutes}", "Посилання: {link}"),
            new("stranger_letter", MailLocales.En, "Subject {minutes}", "Link: {link}"),
        ];
    }

    private sealed class СпорщикЗаТест : IMailTemplateProvider
    {
        public IReadOnlyList<MailTemplate> Templates { get; } =
        [
            new(MailKinds.Test, MailLocales.Ru, "Другая тема", "Другое тело"),
            new(MailKinds.Test, MailLocales.Uk, "Інша тема", "Інше тіло"),
            new(MailKinds.Test, MailLocales.En, "Other subject", "Other body"),
        ];
    }

    private sealed class НеполныйМодуль : IMailTemplateProvider
    {
        public IReadOnlyList<MailTemplate> Templates { get; } =
        [
            new("half_letter", MailLocales.Ru, "Тема", "Тело"),
            new("half_letter", MailLocales.En, "Subject", "Body"),
        ];
    }

    private sealed class ЧужойЯзык : IMailTemplateProvider
    {
        public IReadOnlyList<MailTemplate> Templates { get; } =
        [
            new("german_letter", "de", "Betreff", "Text"),
        ];
    }

    private sealed class КривойШаблон : IMailTemplateProvider
    {
        public IReadOnlyList<MailTemplate> Templates { get; } =
        [
            new("broken_letter", MailLocales.Ru, "Тема", "Тело {нет закрытия"),
            new("broken_letter", MailLocales.Uk, "Тема", "Тіло {нет закрытия"),
            new("broken_letter", MailLocales.En, "Subject", "Body {нет закрытия"),
        ];
    }
}
