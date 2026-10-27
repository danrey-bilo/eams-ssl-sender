using System.Net;
using System.Security.Cryptography;
using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Export;

/// <summary>Закрытый ключ текстом PEM и пароль к нему — то, что видит администратор.</summary>
/// <param name="PrivateKey">Ключ в PEM, как его отдал Key Manager Plus, без BOM и ведущих пробелов.</param>
/// <param name="Passphrase">Пароль ключа; <c>null</c> — Key Manager Plus его не хранит или не отдал.</param>
internal sealed record PrivateKeyText(string PrivateKey, string? Passphrase)
{
    /// <summary>Содержимое не печатается: запись целиком могут вывести в лог или в отчёт об ошибке.</summary>
    public override string ToString() =>
        $"{nameof(PrivateKeyText)} ({PrivateKey.Length} знаков, пароль {(Passphrase is null ? "не задан" : "задан")})";
}

/// <summary>
/// Сертификат и закрытый ключ из Key Manager Plus — администратору в ответ, а не письмом.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Только для пары «Common Name + серийный номер» из последнего опроса</b>
/// (<c>keymanager_certificates</c>). Иначе выгрузка стала бы сквозной трубой к Key Manager Plus:
/// любой Common Name и любой серийный номер — всё, что отдаёт ключ API, а он отдаёт всё. Снимок —
/// это ровно тот список, который администратор видит на экране; выдаётся ровно он. Сертификат,
/// которого опрос ещё не видел, становится доступен после опроса (<c>POST /api/keymanager/poll</c>).
///
/// ⚠ ⚠ <b>В журнал — какой сертификат и в каком виде, без содержимого.</b> Ключ и пароль не
/// пишутся никуда: ни в журнал, ни в лог, ни в текст отказа. Строка журнала пишется только после
/// удачной выгрузки: отказ Key Manager Plus ничего не выдал, и «показан ключ» было бы неправдой.
///
/// ⚠ <b>Свежий второй фактор проверяет эндпоинт</b> (<c>[RequireRecentSecondFactor]</c>): служба не
/// знает, кто её позвал и зачем.
///
/// ⚠ Работает и при выключенной интеграции (<c>keymanager.enabled</c>), как проверка связи и ручная
/// отправка: выключатель останавливает опрос и рассылку, а не доступ администратора к ключам.
/// </remarks>
internal sealed class CertificateExport(
    AppDbContext db,
    IKeyManagerConnector connector,
    IAuditWriter audit,
    ILogger<CertificateExport> log)
{
    /// <summary>Что отвечаем, когда ключ пришёл не текстом PEM.</summary>
    internal const string НеPem = "Key Manager Plus отдал ключ не в виде PEM";

    /// <summary>Строгий UTF-8: негодная последовательность — отказ, а не знак вопроса в ключе.</summary>
    private static readonly UTF8Encoding СтрогийUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Выгрузить файл сертификата в одном из типов <see cref="CertificateFormats"/>.</summary>
    /// <exception cref="AppException">
    /// <c>400 VALIDATION_ERROR</c> (поле — в <c>details.field</c>), <c>404 KEYMANAGER_CERTIFICATE_NOT_FOUND</c>
    /// (пары нет в снимке), <c>409 KEYMANAGER_NOT_CONFIGURED</c>.
    /// </exception>
    /// <exception cref="KeyManagerException">Key Manager Plus не ответил, отказал или прислал не файл.</exception>
    public async Task<KmpFile> DownloadAsync(
        string? commonName, string? serialNumber, string? format, CancellationToken cancellationToken = default)
    {
        var (имя, серийный) = Пара(commonName, serialNumber);

        var тип = CertificateFormats.Normalize(format)
            ?? throw Неверно("format", $"Тип файла — один из: {string.Join(", ", CertificateFormats.All)}.");

        var сертификат = await ИзСнимкаAsync(имя, серийный, cancellationToken);

        using var клиент = await connector.OpenAsync(cancellationToken);

        var файл = await клиент.DownloadAsync(сертификат.CommonName, сертификат.SerialNumber, тип, cancellationToken);

        // ⚠ ⚠ Строка журнала сохраняется ДО выдачи: журнал в базе пишется единицей работы вызывающего, и без
        // сохранения строка о выгрузке молча пропала бы. Не записалась — файл не выдаётся.
        audit.Write(Строка(KeyManagerActions.CertificateDownloaded, сертификат, тип));
        await db.SaveChangesAsync(cancellationToken);

        return файл;
    }

    /// <summary>Закрытый ключ текстом PEM и, если Key Manager Plus его хранит, пароль к нему.</summary>
    /// <exception cref="AppException">Как у <see cref="DownloadAsync"/>.</exception>
    /// <exception cref="KeyManagerException">
    /// Key Manager Plus не ответил, отказал или отдал ключ не текстом PEM (<see cref="НеPem"/>).
    /// </exception>
    /// <remarks>
    /// ⚠ <b>Пароль — по возможности.</b> Отказ Key Manager Plus в пароле не отменяет выдачу ключа:
    /// многие ключи без пароля, и <c>null</c> здесь законен. Отказ в самом ключе — отказ всего.
    /// </remarks>
    public async Task<PrivateKeyText> PrivateKeyAsync(
        string? commonName, string? serialNumber, CancellationToken cancellationToken = default)
    {
        var (имя, серийный) = Пара(commonName, serialNumber);
        var сертификат = await ИзСнимкаAsync(имя, серийный, cancellationToken);

        using var клиент = await connector.OpenAsync(cancellationToken);

        var файл = await клиент.DownloadAsync(
            сертификат.CommonName, сертификат.SerialNumber, CertificateFormats.Key, cancellationToken);

        string? ключ;

        try
        {
            ключ = Pem(файл.Content);
        }
        finally
        {
            // Байты ключа больше не нужны: текст уже собран, а массив иначе жил бы до сборки мусора.
            CryptographicOperations.ZeroMemory(файл.Content);
        }

        if (ключ is null)
        {
            // ⚠ Без содержимого и без его начала: в «не том виде» может лежать тот же ключ в DER.
            throw new KeyManagerException(НеPem);
        }

        var пароль = await ПарольAsync(клиент, сертификат, cancellationToken);

        // ⚠ ⚠ Как у выгрузки: показ ключа без сохранённой строки журнала не состоялся.
        audit.Write(Строка(KeyManagerActions.PrivateKeyViewed, сертификат, формат: null));
        await db.SaveChangesAsync(cancellationToken);

        return new PrivateKeyText(ключ, пароль);
    }

    /// <summary>
    /// Текст PEM или <c>null</c>: байты — строгий UTF-8, после BOM и пробелов идёт <c>-----BEGIN</c>.
    /// </summary>
    internal static string? Pem(byte[] байты)
    {
        ArgumentNullException.ThrowIfNull(байты);

        string текст;

        try
        {
            текст = СтрогийUtf8.GetString(байты);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var начало = текст.TrimStart('﻿').TrimStart();

        return начало.StartsWith("-----BEGIN", StringComparison.Ordinal) ? начало : null;
    }

    private async Task<string?> ПарольAsync(
        IKeyManagerClient клиент, KnownCertificate сертификат, CancellationToken cancellationToken)
    {
        try
        {
            return await клиент.PassphraseAsync(сертификат.CommonName, сертификат.SerialNumber, cancellationToken);
        }
        catch (KeyManagerException ошибка)
        {
            // ⚠ Текст отказа составлен клиентом и секретов не несёт (разбор у KeyManagerException).
            log.LogWarning(
                "keymanager_passphrase_unavailable {CommonName} {SerialNumber} {Error}",
                сертификат.CommonName, сертификат.SerialNumber, ошибка.Message);

            return null;
        }
    }

    /// <summary>Строка журнала: какой сертификат и в каком виде — без содержимого.</summary>
    /// <remarks>
    /// ⚠ Изменения пишутся в лог со значениями (писатель журнала хоста делает так для событий
    /// модуля): Common Name — имя сервера, серийный номер и тип файла — не сведения о человеке и не
    /// секрет. Без них строка «показан ключ» не отвечала бы на главный вопрос: чей.
    /// </remarks>
    private static AuditEntry Строка(AuditAction событие, KnownCertificate сертификат, string? формат)
    {
        var изменения = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["common_name"] = сертификат.CommonName,
            ["serial_number"] = сертификат.SerialNumber,
        };

        if (формат is not null)
        {
            изменения["format"] = формат;
        }

        return new AuditEntry(
            событие,
            ResourceType: KeyManagerActions.CertificateResource,
            Changes: изменения);
    }

    /// <summary>Пара из снимка опроса — или 404.</summary>
    /// <remarks>
    /// ⚠ Сравнение точное: панель присылает ровно то, что получила из
    /// <c>GET /api/keymanager/certificates</c>, а в Key Manager Plus уходят значения из снимка.
    /// </remarks>
    private async Task<KnownCertificate> ИзСнимкаAsync(string имя, string серийный, CancellationToken cancellationToken) =>
        await CertificateSnapshots.Current(db)
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.CommonName == имя && с.SerialNumber == серийный, cancellationToken)
        ?? throw new AppException(
            KeyManagerErrorCodes.CertificateNotFound,
            "Такого сертификата нет среди тех, что видел опрос Key Manager Plus.",
            HttpStatusCode.NotFound);

    private static (string Имя, string Серийный) Пара(string? commonName, string? serialNumber)
    {
        var имя = commonName?.Trim();
        var серийный = serialNumber?.Trim();

        if (string.IsNullOrEmpty(имя))
        {
            throw Неверно("common_name", "Нужен Common Name сертификата.");
        }

        if (string.IsNullOrEmpty(серийный))
        {
            throw Неверно("serial_number", "Нужен серийный номер сертификата.");
        }

        return (имя, серийный);
    }

    private static AppException Неверно(string поле, string текст) => new(
        ErrorCodes.Validation,
        текст,
        HttpStatusCode.BadRequest,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = поле });
}
