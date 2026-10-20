using System.Globalization;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Identity.Mail;

/// <summary>
/// Письма, которые ставит модуль личностей.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Ссылку собирает ЗДЕСЬ, а не почта.</b> Иначе почта обязана знать, что
/// <c>/activate</c> и <c>/password-reset</c> — адреса веб-интерфейса, то есть знать про чужой
/// клиент. Побочный и приятный итог: почте внешний адрес установки не нужен вовсе.
///
/// ⚠ ⚠ <b>Ссылка строится ТОЛЬКО из заданного настройкой адреса.</b> Заголовок <c>Host</c>
/// подделывается: письмо со ссылкой, собранной по нему, уехало бы вместе с токеном на сервер
/// атакующего, и перешедший по ней отдал бы ему свою учётную запись. Адрес не задан — письмо не
/// ставится вовсе, и об этом говорится строкой в логе.
///
/// ⚠ <b>Ни одна неудача постановки НЕ роняет само действие.</b> Человека завели — значит завели;
/// непоставленное письмо это повод показать администратору ссылку, а не откатить заведение.
/// </remarks>
internal sealed class MailNotices(
    IMailQueue mail,
    IUserLocale locale,
    IPublicOrigin origin,
    ILogger<MailNotices> log)
{
    /// <summary>Поставить приглашение. Возвращает, ушло ли оно в очередь.</summary>
    public Task<bool> InvitationAsync(
        User user, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        ПоставитьAsync(user, IdentityMailKinds.Invitation, "activate", token, expiresAt, cancellationToken);

    /// <summary>Поставить письмо сброса пароля.</summary>
    public Task<bool> PasswordResetAsync(
        User user, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        ПоставитьAsync(user, IdentityMailKinds.PasswordReset, "password-reset", token, expiresAt, cancellationToken);

    /// <summary>
    /// Поставить письмо «доступ приостановлен».
    /// </summary>
    /// <remarks>
    /// ⚠ Ссылки в нём нет, и внешний адрес для него не нужен: письмо ничего не предлагает
    /// сделать. Поэтому оно ставится и при незаданном адресе установки — в отличие от двух
    /// остальных.
    /// </remarks>
    public async Task<bool> AccountSuspendedAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        return await ПоставитьПисьмоAsync(
            user,
            IdentityMailKinds.AccountSuspended,
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken);
    }

    private async Task<bool> ПоставитьAsync(
        User user,
        string kind,
        string route,
        string token,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var адрес = await origin.ConfiguredAsync(cancellationToken);

        if (адрес is null)
        {
            // ⚠ Громко, но без адреса получателя и без токена: строка уезжает в общий лог.
            log.LogWarning("mail_link_origin_missing {Kind}", kind);
            return false;
        }

        var минут = (int)Math.Max(1, Math.Round((expiresAt - DateTimeOffset.UtcNow).TotalMinutes));

        return await ПоставитьПисьмоAsync(
            user,
            kind,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = $"{адрес}/{route}#token={Uri.EscapeDataString(token)}",
                ["minutes"] = минут.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken);
    }

    private async Task<bool> ПоставитьПисьмоAsync(
        User user,
        string kind,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var язык = await locale.ForAsync(user.Id, cancellationToken);

        var исход = await mail.EnqueueAsync(user.Email, kind, язык, values, cancellationToken);

        if (исход != MailQueueResult.Queued)
        {
            // ⚠ Вид письма есть, адреса нет: сам факт «на этот адрес просили сброс» является
            // сведением о человеке (правило 14).
            log.LogInformation("mail_not_queued {Kind} {Reason}", kind, исход);
        }

        return исход == MailQueueResult.Queued;
    }
}
