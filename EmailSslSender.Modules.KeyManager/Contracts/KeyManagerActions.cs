using Modules.Core.Contracts.Audit;

namespace EmailSslSender.Modules.KeyManager.Contracts;

/// <summary>События журнала модуля.</summary>
public static class KeyManagerActions
{
    /// <summary>Вид ресурса подписки в строке журнала.</summary>
    public const string SubscriptionResource = "certificate_subscription";

    public static readonly AuditAction ApiKeySet = new("keymanager.api_key_set", "Задан ключ API Key Manager Plus");

    public static readonly AuditAction SubscriptionCreated =
        new("keymanager.subscription_created", "Заведена подписка на сертификат");

    public static readonly AuditAction SubscriptionUpdated =
        new("keymanager.subscription_updated", "Изменена подписка на сертификат");

    public static readonly AuditAction SubscriptionDeleted =
        new("keymanager.subscription_deleted", "Удалена подписка на сертификат");

    public static readonly AuditAction CertificateSent =
        new("keymanager.certificate_sent", "Сертификат поставлен в очередь писем");

    /// <summary>Вид ресурса сертификата в строке журнала: пара «Common Name + серийный номер».</summary>
    public const string CertificateResource = "certificate";

    /// <summary>Администратор выгрузил файл сертификата себе (не письмом).</summary>
    public static readonly AuditAction CertificateDownloaded =
        new("keymanager.certificate_downloaded", "Файл сертификата выгружен администратору");

    /// <summary>Администратору показан закрытый ключ сертификата.</summary>
    public static readonly AuditAction PrivateKeyViewed =
        new("keymanager.private_key_viewed", "Показан закрытый ключ сертификата");

    /// <summary>Все события модуля — для справочника журнала.</summary>
    public static IReadOnlyList<AuditAction> All { get; } =
    [
        ApiKeySet,
        SubscriptionCreated,
        SubscriptionUpdated,
        SubscriptionDeleted,
        CertificateSent,
        CertificateDownloaded,
        PrivateKeyViewed,
    ];
}

/// <summary>События журнала модуля — объявлением, а не строками по коду (решение EAMS 0117).</summary>
internal sealed class KeyManagerAuditActions : IAuditActionProvider
{
    /// <inheritdoc />
    public IReadOnlyList<AuditActionGroup> Groups { get; } =
        [new("keymanager", KeyManagerActions.All, "Key Manager Plus")];
}
