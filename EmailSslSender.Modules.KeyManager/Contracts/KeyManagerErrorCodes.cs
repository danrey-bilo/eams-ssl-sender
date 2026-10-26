namespace EmailSslSender.Modules.KeyManager.Contracts;

/// <summary>
/// Коды ошибок модуля Key Manager Plus.
/// </summary>
/// <remarks>
/// Свои коды модуль держит у себя, общие — в <c>Modules.Core.Errors.ErrorCodes</c> (решение EAMS
/// 0113). Строки прежние: по ним панель и клиенты решают, что показать.
/// </remarks>
public static class KeyManagerErrorCodes
{
    /// <summary>Не заданы адрес Key Manager Plus или ключ API.</summary>
    /// <remarks>⚠ <c>409</c>, как у ненастроенной почты: это состояние установки, а не ошибка запроса.</remarks>
    public const string NotConfigured = "KEYMANAGER_NOT_CONFIGURED";

    /// <summary>Key Manager Plus не ответил или ответил отказом: <c>details.reason</c>.</summary>
    /// <remarks>
    /// ⚠ <c>409</c>, а не <c>502</c>: Cloudflare подменяет тело любого <c>5xx</c> своей страницей, и
    /// причина до администратора не доехала бы.
    /// </remarks>
    public const string Unavailable = "KEYMANAGER_UNAVAILABLE";

    /// <summary>Сертификата с таким Common Name в Key Manager Plus нет.</summary>
    /// <remarks>
    /// Ручная отправка подписки отвечает им <c>409</c> (сертификата нет в Key Manager Plus), а
    /// выгрузка сертификата и показ ключа — <c>404</c>: пары «Common Name + серийный номер» нет в
    /// снимке опроса (<c>keymanager_certificates</c>).
    /// </remarks>
    public const string CertificateNotFound = "KEYMANAGER_CERTIFICATE_NOT_FOUND";

    /// <summary>Эту подписку прямо сейчас отправляет другой запрос или фоновый опрос.</summary>
    public const string DeliveryInProgress = "DELIVERY_IN_PROGRESS";
}
