using System.Net;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Modules.KeyManager.Export;
using EmailSslSender.Modules.KeyManager.Secrets;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Api;

/// <summary>Состояние связи с Key Manager Plus.</summary>
/// <remarks>⚠ Ключ API наружу не отдаётся — только признак «задан».</remarks>
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

/// <summary>Сертификат из последнего опроса.</summary>
internal sealed record KnownCertificateOut(
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("expires_on")] DateOnly? ExpiresOn,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);

/// <summary>Какой сертификат выгрузить и в каком виде.</summary>
/// <param name="CommonName">Common Name из <c>GET /api/keymanager/certificates</c>.</param>
/// <param name="SerialNumber">Серийный номер оттуда же.</param>
/// <param name="Format">Тип файла: <c>CER</c>, <c>CRT</c>, <c>DER</c>, <c>P7B</c>, <c>PKCS</c>, <c>JKS</c>, <c>PEM</c>, <c>KEY</c>.</param>
internal sealed record CertificateDownloadIn(
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("serial_number")] string? SerialNumber,
    [property: JsonPropertyName("format")] string? Format);

/// <summary>Чей закрытый ключ показать.</summary>
/// <param name="CommonName">Common Name из <c>GET /api/keymanager/certificates</c>.</param>
/// <param name="SerialNumber">Серийный номер оттуда же.</param>
internal sealed record PrivateKeyIn(
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("serial_number")] string? SerialNumber);

/// <summary>Закрытый ключ текстом PEM и пароль к нему.</summary>
/// <param name="PrivateKey">Ключ в PEM.</param>
/// <param name="Passphrase">Пароль ключа; <c>null</c>, если Key Manager Plus его не отдал.</param>
internal sealed record PrivateKeyOut(
    [property: JsonPropertyName("private_key")] string PrivateKey,
    [property: JsonPropertyName("passphrase")] string? Passphrase)
{
    /// <summary>⚠ Без содержимого: запись может попасть в лог.</summary>
    public override string ToString() => nameof(PrivateKeyOut);
}

/// <summary>Связь с Key Manager Plus: состояние, ключ API, проверка, опрос, выгрузка.</summary>
/// <remarks>⚠ Ключ API и выгрузка — со свежим вторым фактором: они открывают закрытые ключи.</remarks>
[ApiController]
[AuthenticatedEndpoint("ролей в проекте нет: каждая учётная запись — администратор Key Manager Plus")]
[Route("api/keymanager")]
internal sealed class KeyManagerController(
    AppDbContext db,
    ApiKeyStore apiKey,
    IKeyManagerConnector connector,
    CertificateExport export,
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
    /// <remarks>⚠ Ключ привязывается к текущему <c>keymanager.base_url</c>: сначала адрес, потом ключ.</remarks>
    [HttpPut("api-key")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SetApiKey([FromBody] ApiKeyIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var настройки = await connector.ReadAsync(cancellationToken);

        await db.InTransactionAsync(
            async token =>
            {
                await apiKey.PutAsync(
                    body.ApiKey, ApiKeyStore.Target(настройки.BaseUrl), token);

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

    /// <summary>Проверить связь живым запросом списка; работает и при выключенной интеграции.</summary>
    /// <remarks>Отказ — <c>409 KEYMANAGER_UNAVAILABLE</c>, причина в <c>details.reason</c>.</remarks>
    [HttpPost("test")]
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

    /// <summary>Сертификаты из последнего опроса: по Common Name, замеченные позже — первыми.</summary>
    [HttpGet("certificates")]
    public async Task<IReadOnlyList<KnownCertificateOut>> Certificates(CancellationToken cancellationToken) =>
        await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .OrderBy(с => с.CommonName)
            .ThenByDescending(с => с.FirstSeenAt)
            .ThenByDescending(с => с.ExpiresOn)
            .Select(с => new KnownCertificateOut(
                с.CommonName, с.SerialNumber, с.ExpiresOn, с.FirstSeenAt, с.LastSeenAt))
            .ToListAsync(cancellationToken);

    /// <summary>Скачать файл сертификата; пара «имя + серийный» — из снимка, иначе <c>404</c>.</summary>
    /// <remarks>⚠ Тип всегда <c>application/octet-stream</c>: иначе Chrome на телефоне дописывает своё расширение.</remarks>
    [HttpPost("certificates/download")]
    [RequireRecentSecondFactor]
    [ProducesResponseType(typeof(Stream), (int)HttpStatusCode.OK, "application/octet-stream")]
    public async Task<IActionResult> DownloadCertificate(
        [FromBody] CertificateDownloadIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        KmpFile файл;

        try
        {
            файл = await export.DownloadAsync(body.CommonName, body.SerialNumber, body.Format, cancellationToken);
        }
        catch (KeyManagerException ошибка)
        {
            throw Unavailable(ошибка);
        }

        // ⚠ no-store повторён намеренно, не полагаясь на конвейер: в ответе бывает закрытый ключ.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = Вложение(файл.FileName);

        return File(файл.Content, "application/octet-stream");
    }

    /// <summary>Показать закрытый ключ сертификата текстом PEM и пароль к нему.</summary>
    /// <remarks>Ключ не в PEM — <c>409 KEYMANAGER_UNAVAILABLE</c>.</remarks>
    [HttpPost("certificates/private-key")]
    [RequireRecentSecondFactor]
    public async Task<PrivateKeyOut> ViewPrivateKey([FromBody] PrivateKeyIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            var ключ = await export.PrivateKeyAsync(body.CommonName, body.SerialNumber, cancellationToken);

            Response.Headers.CacheControl = "no-store";

            return new PrivateKeyOut(ключ.PrivateKey, ключ.Passphrase);
        }
        catch (KeyManagerException ошибка)
        {
            throw Unavailable(ошибка);
        }
    }

    /// <summary><c>Content-Disposition</c> вложения.</summary>
    /// <remarks>⚠ Заголовок — только ASCII: не-латинское имя едет в <c>filename*</c> (RFC 6266).</remarks>
    internal static string Вложение(string имя)
    {
        var ascii = new string([.. имя.Select(с => с is >= ' ' and <= '~' and not '"' and not '\\' ? с : '_')]);

        return string.Equals(ascii, имя, StringComparison.Ordinal)
            ? $"attachment; filename=\"{ascii}\""
            : $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(имя)}";
    }

    /// <summary>Отказ Key Manager Plus наружу.</summary>
    /// <remarks>⚠ <c>409</c>, а не <c>502</c>: Cloudflare подменяет тело любого <c>5xx</c> своей страницей.</remarks>
    internal static AppException Unavailable(KeyManagerException ошибка) => new(
        KeyManagerErrorCodes.Unavailable,
        "Key Manager Plus не ответил или отказал.",
        HttpStatusCode.Conflict,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = ошибка.Message });
}
