namespace EmailSslSender.Modules.KeyManager.Contracts;

/// <summary>Коды ошибок модуля; строки не менять — по ним решает панель.</summary>
public static class KeyManagerErrorCodes
{
    /// <summary>Не заданы адрес Key Manager Plus или ключ API (<c>409</c>).</summary>
    public const string NotConfigured = "KEYMANAGER_NOT_CONFIGURED";

    /// <summary>Key Manager Plus недоступен или отказал: <c>details.reason</c>.</summary>
    /// <remarks>⚠ <c>409</c>, не <c>5xx</c>: Cloudflare подменяет тело <c>5xx</c> своей страницей.</remarks>
    public const string Unavailable = "KEYMANAGER_UNAVAILABLE";

    /// <summary>Сертификата нет в KMP (<c>409</c>) или пары «имя + серийный» нет в снимке (<c>404</c>).</summary>
    public const string CertificateNotFound = "KEYMANAGER_CERTIFICATE_NOT_FOUND";

    /// <summary>Подписку прямо сейчас отправляет другой запрос или опрос.</summary>
    public const string DeliveryInProgress = "DELIVERY_IN_PROGRESS";
}
