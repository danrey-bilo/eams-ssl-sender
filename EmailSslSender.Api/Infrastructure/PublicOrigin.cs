using EmailSslSender.Modules.Mail.Contracts;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Внешний адрес установки: заданный настройкой и выведенный из запроса.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Живёт в ХОСТЕ, а не в модуле, и это не мелочь размещения.</b> Выведение адреса из
/// запроса требует <c>IHttpContextAccessor</c> — то есть знания о том, что процесс вообще
/// обслуживает HTTP. Модуль этого знать не должен: он же работает и в воркере, где запроса нет
/// вовсе.
///
/// ⚠ <b>Настройка читается через общий читатель</b>, а не своим запросом: у неё те же три слоя
/// и то же кэширование на область, что у всех остальных.
/// </remarks>
internal sealed class PublicOrigin(
    ISettingsReader settings, IHttpContextAccessor context) : IPublicOrigin
{
    public async Task<string?> ConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var адрес = await settings.GetAsync(PublicUrlSetting.Key, cancellationToken);

        return string.IsNullOrWhiteSpace(адрес) ? null : СоСхемой(адрес.Trim().TrimEnd('/'));
    }

    /// <summary>
    /// Дописать схему, если администратор её не написал.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заведено по следу настоящей поломки, прожившей на стенде незамеченной.</b> Адрес
    /// без схемы браузер считает <b>относительным</b>: ссылка из письма, собранная из
    /// <c>sender.example.com</c>, вела в
    /// <c>https://sender.example.com/sender.example.com/activate…</c> — и выглядела при этом
    /// совершенно правдоподобно. Лечится дописыванием схемы, а не правкой настройки: значение
    /// уже лежит в базах поднятых установок, и отказ на чтении сломал бы им ссылки разом.
    ///
    /// ⚠ <b>Дописывается <c>https</c>, а не <c>http</c>.</b> Наружу установка смотрит через
    /// туннель; предположи мы <c>http</c> — и ссылка вела бы на понижение протокола.
    ///
    /// ⚠ <b>Проверять при записи было бы правильнее, но недостаточно:</b> значения, сохранённые
    /// до появления проверки, никуда не денутся. Проверка на записи — законное дополнение, а не
    /// замена.
    /// </remarks>
    private static string СоСхемой(string адрес) =>
        адрес.Contains("://", StringComparison.Ordinal) ? адрес : $"https://{адрес}";

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (await ConfiguredAsync(cancellationToken) is { } заданный)
        {
            return заданный;
        }

        // ⚠ Только для показа. Заголовки подделываются, и ссылка в письме, собранная отсюда,
        // уехала бы вместе с токеном на сервер атакующего.
        var запрос = context.HttpContext?.Request;

        return запрос is null ? null : $"{запрос.Scheme}://{запрос.Host}";
    }
}
