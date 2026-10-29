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

/// <summary>Закрытый ключ текстом PEM и пароль к нему.</summary>
/// <param name="PrivateKey">Ключ в PEM без BOM и ведущих пробелов.</param>
/// <param name="Passphrase">Пароль ключа; <c>null</c> — KMP его не хранит или не отдал.</param>
internal sealed record PrivateKeyText(string PrivateKey, string? Passphrase)
{
    /// <summary>Без содержимого: запись могут вывести в лог.</summary>
    public override string ToString() =>
        $"{nameof(PrivateKeyText)} ({PrivateKey.Length} знаков, пароль {(Passphrase is null ? "не задан" : "задан")})";
}

/// <summary>Сертификат и закрытый ключ из KMP — администратору в ответ, а не письмом.</summary>
/// <remarks>
/// ⚠ Только пары «имя + серийный» из снимка опроса — иначе сквозная труба ко всему KMP.
/// Ключ и пароль — никуда, кроме ответа; второй фактор проверяет эндпоинт.
/// </remarks>
internal sealed class CertificateExport(
    AppDbContext db,
    IKeyManagerConnector connector,
    IAuditWriter audit,
    ILogger<CertificateExport> log)
{
    internal const string НеPem = "Key Manager Plus отдал ключ не в виде PEM";

    /// <summary>Строгий UTF-8: негодная последовательность — отказ, а не «?» в ключе.</summary>
    private static readonly UTF8Encoding СтрогийUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Файл сертификата в одном из типов <see cref="CertificateFormats"/>.</summary>
    /// <exception cref="AppException"><c>400</c>, <c>404</c> (пары нет в снимке), <c>409</c> (не настроен).</exception>
    /// <exception cref="KeyManagerException">KMP не ответил, отказал или прислал не файл.</exception>
    public async Task<KmpFile> DownloadAsync(
        string? commonName, string? serialNumber, string? format, CancellationToken cancellationToken = default)
    {
        var (имя, серийный) = Пара(commonName, serialNumber);

        var тип = CertificateFormats.Normalize(format)
            ?? throw Неверно("format", $"Тип файла — один из: {string.Join(", ", CertificateFormats.All)}.");

        var сертификат = await ИзСнимкаAsync(имя, серийный, cancellationToken);

        using var клиент = await connector.OpenAsync(cancellationToken);

        var файл = await клиент.DownloadAsync(сертификат.CommonName, сертификат.SerialNumber, тип, cancellationToken);

        // ⚠ Журнал сохраняется ДО выдачи: без SaveChanges строка пропадёт; не записалась — файла нет.
        audit.Write(Строка(KeyManagerActions.CertificateDownloaded, сертификат, тип));
        await db.SaveChangesAsync(cancellationToken);

        return файл;
    }

    /// <summary>Закрытый ключ PEM и пароль — по возможности: отказ в пароле не отменяет ключ.</summary>
    /// <exception cref="AppException">Как у <see cref="DownloadAsync"/>.</exception>
    /// <exception cref="KeyManagerException">KMP не ответил, отказал или отдал не PEM.</exception>
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
            // Не держать байты ключа до сборки мусора.
            CryptographicOperations.ZeroMemory(файл.Content);
        }

        if (ключ is null)
        {
            // ⚠ Без содержимого: там может быть тот же ключ в DER.
            throw new KeyManagerException(НеPem);
        }

        var пароль = await ПарольAsync(клиент, сертификат, cancellationToken);

        // ⚠ Как у выгрузки: журнал сохраняется до выдачи.
        audit.Write(Строка(KeyManagerActions.PrivateKeyViewed, сертификат, формат: null));
        await db.SaveChangesAsync(cancellationToken);

        return new PrivateKeyText(ключ, пароль);
    }

    /// <summary>Текст PEM или <c>null</c>.</summary>
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
            // Текст отказа KeyManagerException секретов не несёт.
            log.LogWarning(
                "keymanager_passphrase_unavailable {CommonName} {SerialNumber} {Error}",
                сертификат.CommonName, сертификат.SerialNumber, ошибка.Message);

            return null;
        }
    }

    /// <summary>Какой сертификат и в каком виде — без содержимого; значения не секретны и идут в лог.</summary>
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

    /// <summary>Пара из снимка опроса, сравнение точное, — или 404.</summary>
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
