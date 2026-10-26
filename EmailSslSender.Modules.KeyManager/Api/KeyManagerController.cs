using System.Net;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Modules.KeyManager.Export;
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

/// <summary>Сертификат из последнего опроса.</summary>
internal sealed record KnownCertificateOut(
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("expires_on")] DateOnly? ExpiresOn,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);

/// <summary>Какой сертификат выгрузить и в каком виде.</summary>
/// <param name="CommonName">Common Name — как в <c>GET /api/keymanager/certificates</c>.</param>
/// <param name="SerialNumber">Серийный номер — как там же.</param>
/// <param name="Format">Тип файла: <c>CER</c>, <c>CRT</c>, <c>DER</c>, <c>P7B</c>, <c>PKCS</c>, <c>JKS</c>, <c>PEM</c>, <c>KEY</c>.</param>
internal sealed record CertificateDownloadIn(
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("serial_number")] string? SerialNumber,
    [property: JsonPropertyName("format")] string? Format);

/// <summary>Чей закрытый ключ показать.</summary>
/// <param name="CommonName">Common Name — как в <c>GET /api/keymanager/certificates</c>.</param>
/// <param name="SerialNumber">Серийный номер — как там же.</param>
internal sealed record PrivateKeyIn(
    [property: JsonPropertyName("common_name")] string? CommonName,
    [property: JsonPropertyName("serial_number")] string? SerialNumber);

/// <summary>Закрытый ключ текстом PEM и пароль к нему.</summary>
/// <param name="PrivateKey">Ключ в PEM (<c>-----BEGIN …</c>).</param>
/// <param name="Passphrase">Пароль ключа; <c>null</c> — Key Manager Plus его не хранит или не отдал.</param>
internal sealed record PrivateKeyOut(
    [property: JsonPropertyName("private_key")] string PrivateKey,
    [property: JsonPropertyName("passphrase")] string? Passphrase)
{
    /// <summary>Содержимое не печатается: запись целиком могут вывести в лог или в отчёт об ошибке.</summary>
    public override string ToString() => nameof(PrivateKeyOut);
}

/// <summary>
/// Связь с Key Manager Plus: состояние, ключ API, проверка, опрос, снимок сертификатов, выгрузка
/// сертификата и закрытого ключа.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Ключ API и выгрузка требуют свежего второго фактора</b>
/// (<c>[RequireRecentSecondFactor]</c>, решение владельца 2026-10-20). Ключ API отдаёт любые
/// сертификаты вместе с закрытыми ключами и наружу не возвращается никогда; выгрузка отдаёт их
/// прямо в руки.
///
/// ⚠ Проверка ходит на адрес из настроек и показывает ответ — то есть является зондом наружу
/// из-под сервера; доступна, как и всё, только вошедшему. Ключа она не показывает, и ключ уходит
/// только на адрес, для которого его ввели (<see cref="ApiKeyStore"/>).
/// </remarks>
[ApiController]
[Route("api/keymanager")]
internal sealed class KeyManagerController(
    SenderDbContext db,
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
    /// <remarks>
    /// ⚠ ⚠ <b>Ключ привязывается к адресу и отпечатку, заданным на момент ввода</b>
    /// (<c>keymanager.base_url</c>, <c>keymanager.server_sha256</c>): сначала они, потом ключ.
    /// Сменили адрес или отпечаток — ключ вводится заново, иначе проверка и опрос отказывают.
    ///
    /// ⚠ В журнал идёт факт, а не значение.
    /// </remarks>
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
                    body.ApiKey, ApiKeyStore.Target(настройки.BaseUrl, настройки.ServerSha256), token);

                audit.Write(new AuditEntry(
                    KeyManagerActions.ApiKeySet,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["set"] = !string.IsNullOrWhiteSpace(body.ApiKey),
                    },
                    LogChanges: true));

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
    ///
    /// ⚠ ⚠ Ключ API уходит только на адрес и отпечаток, для которых его ввели: подменённый адрес
    /// получает отказ «введите ключ заново» без единого запроса к нему.
    /// </remarks>
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

    /// <summary>
    /// Выгрузить файл сертификата из Key Manager Plus — себе, а не письмом.
    /// </summary>
    /// <remarks>
    /// Ответ — сам файл: <c>Content-Type: application/octet-stream</c>,
    /// <c>Content-Disposition: attachment; filename="…"</c> с тем же именем, что у файла в архиве
    /// письма (<c>*.example.com</c> + <c>PEM</c> → <c>wildcard.example.com.pem</c>),
    /// <c>Cache-Control: no-store</c>.
    ///
    /// ⚠ ⚠ Тип — всегда <c>application/octet-stream</c>, а не по формату (замечание владельца
    /// 2026-10-26): Chrome на телефоне дописывает к имени расширение «своего» типа —
    /// <c>.crt</c> с <c>application/pkix-cert</c> сохранялся как <c>.crt.cer</c>, ключ с
    /// <c>application/x-pem-file</c> — как <c>.key.pem</c>. У «просто байтов» своего расширения нет.
    ///
    /// ⚠ Только для пары «Common Name + серийный номер» из <c>GET /api/keymanager/certificates</c>;
    /// иначе <c>404 KEYMANAGER_CERTIFICATE_NOT_FOUND</c>. Отказ Key Manager Plus —
    /// <c>409 KEYMANAGER_UNAVAILABLE</c> с причиной в <c>details.reason</c>; не заданы адрес или
    /// ключ — <c>409 KEYMANAGER_NOT_CONFIGURED</c>. В журнал — сертификат и тип, без содержимого.
    /// </remarks>
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

        // ⚠ no-store ставит и общий конвейер на всё под /api — здесь повторено намеренно: в ответе
        // бывает закрытый ключ, и кэш прокси или браузера не должен его хранить, даже если
        // конвейер однажды поменяют.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = Вложение(файл.FileName);

        return File(файл.Content, "application/octet-stream");
    }

    /// <summary>
    /// Показать закрытый ключ сертификата текстом PEM и пароль к нему.
    /// </summary>
    /// <remarks>
    /// Ответ: <c>{"private_key": "-----BEGIN …", "passphrase": "…" | null}</c>. Пароль — по
    /// возможности: Key Manager Plus мог его не хранить или отказать, и тогда <c>null</c>.
    ///
    /// ⚠ Ключ пришёл не текстом PEM (DER, ZIP, не UTF-8) — <c>409 KEYMANAGER_UNAVAILABLE</c> с
    /// причиной «Key Manager Plus отдал ключ не в виде PEM». Прочие отказы — как у выгрузки файла.
    /// В журнал — сертификат, без ключа и пароля.
    /// </remarks>
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

    /// <summary>
    /// <c>Content-Disposition</c> вложения: <c>attachment; filename="…"</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Заголовок HTTP — только ASCII, а Common Name может прийти и не латиницей: такое имя едет
    /// вторым параметром <c>filename*</c> (RFC 6266, UTF-8), а в <c>filename</c> остаётся замена
    /// подчёркиваниями. Кавычек и обратной косой в имени не бывает: их вычищает
    /// <c>KeyManagerClient.ИмяПоУмолчанию</c>, но и здесь они заменяются — заголовок не должен
    /// зависеть от чужой чистки.
    /// </remarks>
    internal static string Вложение(string имя)
    {
        var ascii = new string([.. имя.Select(с => с is >= ' ' and <= '~' and not '"' and not '\\' ? с : '_')]);

        return string.Equals(ascii, имя, StringComparison.Ordinal)
            ? $"attachment; filename=\"{ascii}\""
            : $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(имя)}";
    }

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
