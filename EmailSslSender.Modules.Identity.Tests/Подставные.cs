using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>Очередь писем, которая ничего не ставит.</summary>
/// <remarks>
/// ⚠ Контракт даёт СОСЕДНИЙ МОДУЛЬ (почта), и здесь он подменён — как журнал и токены. Смысл
/// проверки в том, что модуль личностей собирается ОДИН: подключи сюда настоящую почту, и она
/// перестанет отвечать на свой вопрос.
/// </remarks>
public sealed class БезПочты : IMailQueue
{
    /// <summary>Что ставили. Тестам нужно знать, звали ли вообще.</summary>
    public List<(string To, string Kind, string Locale)> Письма { get; } = [];

    /// <summary>
    /// То же, но с полями письма.
    /// </summary>
    /// <remarks>
    /// ⚠ Заведено подэтапом 49.4g: проверка «потерявший аутентификатор входит кодом из письма»
    /// обязана взять код ИЗ ПИСЬМА, а не сгенерировать свой. Трёх полей для этого мало, а менять
    /// форму <see cref="Письма"/> — значит переписать её читателей ради одного нового.
    /// </remarks>
    public List<Письмо> Отправленные { get; } = [];

    /// <summary>Настроена ли почта — задаёт тест.</summary>
    public bool Настроена { get; set; }

    /// <summary>Что отвечать на постановку — задаёт тест.</summary>
    public MailQueueResult Исход { get; set; } = MailQueueResult.NotConfigured;

    public Task<bool> CanSendAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Настроена);

    public Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        Письма.Add((toAddress, kind, locale));
        Отправленные.Add(new Письмо(toAddress, kind, locale, values));

        return Task.FromResult(Исход);
    }

    /// <summary>Модуль личностей вложений не шлёт; перегрузка — ради полноты контракта.</summary>
    public Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<MailAttachment> attachments,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(toAddress, kind, locale, values, cancellationToken);

    /// <summary>Последнее письмо на этот адрес. <c>null</c> — не слали.</summary>
    public Письмо? Последнее(string адрес) =>
        Отправленные.FindLast(письмо => string.Equals(письмо.To, адрес, StringComparison.OrdinalIgnoreCase));

    /// <summary>Поставленное письмо целиком.</summary>
    public sealed record Письмо(
        string To, string Kind, string Locale, IReadOnlyDictionary<string, string> Values);
}

/// <summary>Язык получателя: всегда английский, если тест не сказал иначе.</summary>
public sealed class ЯзыкПоУмолчанию : IUserLocale
{
    /// <summary>Что отвечать — задаёт тест.</summary>
    public string Язык { get; set; } = MailLocales.En;

    public Task<string> ForAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Язык);
}

/// <summary>Внешний адрес установки — задаёт тест.</summary>
/// <remarks>
/// ⚠ `null` по умолчанию: это состояние установки, где адрес не задан, и письма со ссылками в
/// нём не ставятся вовсе. Умолчание выбрано так, чтобы тест, забывший его задать, увидел
/// правильное поведение, а не случайное.
/// </remarks>
public sealed class ВнешнийАдрес : IPublicOrigin
{
    /// <summary>Заданный адрес.</summary>
    public string? Адрес { get; set; }

    public Task<string?> ConfiguredAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Адрес);

    public Task<string?> ResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Адрес);
}
