using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Список сертификатов Key Manager Plus на момент запроса и когда каждый замечен впервые.</summary>
/// <remarks>⚠ Текущий сертификат — замеченный позже всех, а не самый долгий: новые выпуски бывают короче старых.</remarks>
internal sealed class CertificateSnapshot(
    IReadOnlyList<KmpCertificate> certificates,
    IReadOnlyDictionary<(string CommonName, string Serial), DateTimeOffset> firstSeen,
    DateTimeOffset fetchedAt)
{
    public IReadOnlyList<KmpCertificate> Certificates { get; } = certificates;

    /// <summary>Когда список запрошен; отправка позже значит, что снимок устарел.</summary>
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

/// <summary>Запросить список у Key Manager Plus, запомнить его и вернуть снимок.</summary>
internal sealed class CertificateSnapshots(AppDbContext db, TimeProvider clock)
{
    /// <summary>Сколько помнить пропавшую пару: не ноль, иначе сбой списка сделал бы её «новой».</summary>
    internal static readonly TimeSpan Хранить = TimeSpan.FromDays(30);

    /// <summary>Пары из последнего удачного опроса (наибольшее <c>last_seen_at</c>).</summary>
    public static IQueryable<KnownCertificate> Current(AppDbContext db) =>
        db.Set<KnownCertificate>().Where(с =>
            с.LastSeenAt >= db.Set<KnownCertificate>().Max(в => в.LastSeenAt));

    /// <exception cref="KeyManagerException">Key Manager Plus не ответил или отказал.</exception>
    public async Task<CertificateSnapshot> TakeAsync(IKeyManagerClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // ⚠ Время — до запроса: отправленное позже снимок мог не увидеть.
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

    /// <summary>Новые пары добавить, виденные — отметить.</summary>
    /// <remarks>⚠ <c>ON CONFLICT</c> — для параллельных опросов; <c>GREATEST</c> — чтобы <c>last_seen_at</c> не шёл назад.</remarks>
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
                        SET expires_on = EXCLUDED.expires_on,
                            last_seen_at = GREATEST(keymanager_certificates.last_seen_at, EXCLUDED.last_seen_at)
                        """,
                        token);
                }

                // ⚠ Забывать — после отметки увиденных: иначе долгий перерыв в опросе стёр бы и живые пары.
                var давно = когда - Хранить;
                await db.Set<KnownCertificate>()
                    .Where(с => с.LastSeenAt < давно)
                    .ExecuteDeleteAsync(token);
            },
            cancellationToken);
}
