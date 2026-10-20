using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>
/// Список сертификатов Key Manager Plus на момент запроса — и когда каждый замечен впервые.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Текущий сертификат Common Name — замеченный ПОЗЖЕ ВСЕХ, а не самый долгий.</b>
/// Перевыпуск у удостоверяющего центра часто сохраняет срок прежнего сертификата, а новые
/// сертификаты живут короче старых (с 2026 года — не дольше 200 дней). «Самый долгий» в таких
/// случаях — старый, и замена не ушла бы никогда и молча. Срок решает только среди замеченных в
/// одном опросе — например, в самом первом.
///
/// ⚠ <b>Время запроса — часть снимка.</b> Отправка, состоявшаяся позже него, значит, что снимок
/// устарел: другой процесс или ручная отправка уже разослали то, чего этот список не знает.
/// </remarks>
internal sealed class CertificateSnapshot(
    IReadOnlyList<KmpCertificate> certificates,
    IReadOnlyDictionary<(string CommonName, string Serial), DateTimeOffset> firstSeen,
    DateTimeOffset fetchedAt)
{
    public IReadOnlyList<KmpCertificate> Certificates { get; } = certificates;

    /// <summary>Когда список запрошен у Key Manager Plus.</summary>
    public DateTimeOffset FetchedAt { get; } = fetchedAt;

    /// <summary>Снимок без истории — для проверок, где «впервые» не важно.</summary>
    public static CertificateSnapshot Of(IReadOnlyList<KmpCertificate> certificates, DateTimeOffset fetchedAt) =>
        new(certificates, new Dictionary<(string, string), DateTimeOffset>(), fetchedAt);

    /// <summary>Текущий сертификат Common Name или <c>null</c>, если такого нет.</summary>
    public KmpCertificate? Current(string commonName) =>
        Certificates
            .Where(с => string.Equals(с.CommonName, commonName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(с => firstSeen.GetValueOrDefault((с.CommonName, с.SerialNumber), FetchedAt))
            .ThenByDescending(с => с.ExpiresOn ?? DateOnly.MinValue)
            .ThenByDescending(с => с.SerialNumber, StringComparer.Ordinal)
            .FirstOrDefault();
}

/// <summary>
/// Запросить список у Key Manager Plus, запомнить его и вернуть снимок.
/// </summary>
internal sealed class CertificateSnapshots(SenderDbContext db, TimeProvider clock)
{
    /// <exception cref="KeyManagerException">Key Manager Plus не ответил или отказал.</exception>
    public async Task<CertificateSnapshot> TakeAsync(IKeyManagerClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // ⚠ Время — ДО запроса: всё, что отправлено после этой точки, снимок мог не увидеть.
        var когда = clock.GetUtcNow();
        var список = await client.ListAsync(cancellationToken);

        await ЗапомнитьAsync(список, когда, cancellationToken);

        var имена = список.Select(с => с.CommonName).Distinct(StringComparer.Ordinal).ToArray();

        var впервые = await db.Set<KnownCertificate>()
            .AsNoTracking()
            .Where(с => имена.Contains(с.CommonName))
            .Select(с => new { с.CommonName, с.SerialNumber, с.FirstSeenAt })
            .ToListAsync(cancellationToken);

        return new CertificateSnapshot(
            список,
            впервые.ToDictionary(с => (с.CommonName, с.SerialNumber), с => с.FirstSeenAt),
            когда);
    }

    /// <summary>
    /// Обновить снимок: новые пары «Common Name + серийный» добавить, виденные — отметить.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>ON CONFLICT</c>, а не «прочитать и добавить недостающие»: два процесса, опросившие
    /// одновременно, иначе споткнулись бы о первичный ключ друг друга.
    /// </remarks>
    private Task ЗапомнитьAsync(IReadOnlyList<KmpCertificate> список, DateTimeOffset когда, CancellationToken cancellationToken) =>
        db.InTransactionAsync(
            async token =>
            {
                foreach (var с in список.DistinctBy(с => (с.CommonName, с.SerialNumber)))
                {
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO keymanager_certificates
                            (common_name, serial_number, expires_on, first_seen_at, last_seen_at)
                        VALUES ({с.CommonName}, {с.SerialNumber}, {с.ExpiresOn}, {когда}, {когда})
                        ON CONFLICT (common_name, serial_number) DO UPDATE
                        SET expires_on = EXCLUDED.expires_on, last_seen_at = EXCLUDED.last_seen_at
                        """,
                        token);
                }
            },
            cancellationToken);
}
