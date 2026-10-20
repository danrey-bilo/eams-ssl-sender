using System.Net;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Api;

/// <summary>Состояние связи с Key Manager Plus.</summary>
/// <remarks>⚠ Ключа API здесь нет ни в каком виде — только признак «задан».</remarks>
internal sealed record KeyManagerStatusOut(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("api_key_set")] bool ApiKeySet,
    [property: JsonPropertyName("last_poll_at")] DateTimeOffset? LastPollAt,
    [property: JsonPropertyName("last_poll_error")] string? LastPollError,
    [property: JsonPropertyName("last_poll_count")] int? LastPollCount);

/// <summary>Новый ключ API. Пустая строка снимает его.</summary>
internal sealed record ApiKeyIn([property: JsonPropertyName("api_key")] string? ApiKey);

/// <summary>Итог проверки связи.</summary>
internal sealed record KeyManagerTestOut([property: JsonPropertyName("certificates")] int Certificates);

/// <summary>Сертификат из снимка последних опросов.</summary>
internal sealed record KnownCertificateOut(
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("expires_on")] DateOnly? ExpiresOn,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);

/// <summary>
/// Связь с Key Manager Plus: состояние, ключ API, проверка, опрос, снимок сертификатов.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Ключ API и проверка требуют свежего второго фактора.</b> Ключ API отдаёт любые
/// сертификаты вместе с закрытыми ключами. Проверка к тому же ходит на адрес из настроек и
/// показывает ответ — то есть является зондом наружу из-под сервера.
/// </remarks>
[ApiController]
[Route("api/keymanager")]
internal sealed class KeyManagerController(
    SenderDbContext db,
    ApiKeyStore apiKey,
    IKeyManagerConnector connector,
    PollWakeup wakeup,
    IAuditWriter audit) : ControllerBase
{
    /// <summary>Состояние: включено ли, задан ли ключ, чем кончился последний опрос.</summary>
    [HttpGet]
    public async Task<KeyManagerStatusOut> Status(CancellationToken cancellationToken)
    {
        var настройки = await connector.ReadAsync(cancellationToken);

        var состояние = await db.Set<KeyManagerState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, cancellationToken);

        return new KeyManagerStatusOut(
            настройки.Enabled,
            настройки.Configured,
            настройки.ApiKeySet,
            состояние?.LastPollAt,
            состояние?.LastPollError,
            состояние?.LastPollCount);
    }

    /// <summary>Задать или снять ключ API (Settings → General Settings → API Key в Key Manager Plus).</summary>
    /// <remarks>⚠ В журнал идёт факт, а не значение.</remarks>
    [HttpPut("api-key")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SetApiKey([FromBody] ApiKeyIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await db.InTransactionAsync(
            async token =>
            {
                await apiKey.PutAsync(body.ApiKey, token);

                audit.Write(new AuditEntry(
                    KeyManagerActions.ApiKeySet,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["set"] = !string.IsNullOrWhiteSpace(body.ApiKey),
                    }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Проверить связь: живой запрос списка сертификатов.
    /// </summary>
    /// <remarks>
    /// ⚠ Работает и при выключенной интеграции: проверяют как раз до того, как включить.
    /// Отказ — <c>409 KEYMANAGER_UNAVAILABLE</c> с текстом в <c>details.reason</c>.
    /// </remarks>
    [HttpPost("test")]
    [RequireRecentSecondFactor]
    public async Task<KeyManagerTestOut> Test(CancellationToken cancellationToken)
    {
        try
        {
            using var клиент = await connector.OpenAsync(cancellationToken);

            return new KeyManagerTestOut((await клиент.ListAsync(cancellationToken)).Count);
        }
        catch (KeyManagerException ошибка)
        {
            throw Unavailable(ошибка);
        }
    }

    /// <summary>Опросить Key Manager Plus сейчас, не дожидаясь срока.</summary>
    [HttpPost("poll")]
    public IActionResult Poll()
    {
        wakeup.Signal();

        return Accepted();
    }

    /// <summary>Сертификаты, которые видел опрос: по Common Name, свежие первыми.</summary>
    [HttpGet("certificates")]
    public async Task<IReadOnlyList<KnownCertificateOut>> Certificates(CancellationToken cancellationToken) =>
        await db.Set<KnownCertificate>()
            .AsNoTracking()
            .OrderBy(с => с.CommonName)
            .ThenByDescending(с => с.ExpiresOn)
            .Select(с => new KnownCertificateOut(
                с.CommonName, с.SerialNumber, с.ExpiresOn, с.FirstSeenAt, с.LastSeenAt))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Отказ Key Manager Plus наружу.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>409</c>, а не <c>502</c>: Cloudflare подменяет тело любого <c>5xx</c> своей страницей, и
    /// причина до администратора не доехала бы (разбор у <c>MAIL_SEND_FAILED</c>).
    /// </remarks>
    internal static AppException Unavailable(KeyManagerException ошибка) => new(
        ErrorCodes.KeyManagerUnavailable,
        "Key Manager Plus не ответил или отказал.",
        HttpStatusCode.Conflict,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = ошибка.Message });
}
