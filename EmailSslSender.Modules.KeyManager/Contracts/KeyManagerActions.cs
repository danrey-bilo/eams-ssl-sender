using EmailSslSender.Shared.Contracts.Audit;

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
}
