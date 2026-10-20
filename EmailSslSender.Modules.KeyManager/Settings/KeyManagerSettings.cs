using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.KeyManager.Settings;

/// <summary>
/// Настройки связи с ManageEngine Key Manager Plus.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Ключа API здесь НЕТ.</b> <c>system_settings</c> хранит значения открытым <c>jsonb</c>, а
/// ключ API отдаёт любые сертификаты вместе с закрытыми ключами. Он лежит запечатанным в
/// <c>keymanager_state</c> и наружу отдаётся только признаком «задан».
///
/// ⚠ <b>Все помечены настройками защиты.</b> Подменивший адрес Key Manager Plus получает ключ
/// API первым же опросом, а снявший отпечаток — открывает дорогу тому, кто встал посередине.
///
/// ⚠ Подписи по-английски (решение 0027), как у остальных модулей.
/// </remarks>
internal static class KeyManagerSettings
{
    /// <summary>Раздел на экране настроек.</summary>
    public const string Category = "keymanager";

    /// <summary>Опрашивать ли Key Manager Plus и рассылать ли сертификаты.</summary>
    public static readonly SettingKey<bool> Enabled = new(
        "keymanager.enabled",
        Default: false,
        "Key Manager Plus integration",
        "Poll ManageEngine Key Manager Plus for SSL certificates and mail them to subscribed "
        + "recipients. Off by default: nothing is polled until the address and API key are set.",
        Category,
        IsSecurity: true);

    /// <summary>Адрес сервера.</summary>
    public static readonly SettingKey<string?> BaseUrl = new(
        "keymanager.base_url",
        Default: null,
        "Key Manager Plus address",
        "HTTPS address of the Key Manager Plus server with its port, for example "
        + "https://kmp.example.local:6565. Plain HTTP is refused: the API key travels in a header.",
        Category,
        IsSecurity: true,
        Maximum: 2048);

    /// <summary>Как часто опрашивать.</summary>
    /// <remarks>
    /// ⚠ Вебхуков у Key Manager Plus нет: продление замечается только опросом. Чем реже опрос,
    /// тем дольше новый сертификат ждёт отправки; ручной опрос — <c>POST /api/keymanager/poll</c>.
    /// </remarks>
    public static readonly SettingKey<int> PollMinutes = new(
        "keymanager.poll_minutes",
        Default: 15,
        "Poll interval, minutes",
        "How often Key Manager Plus is asked for certificates. A renewed certificate is noticed "
        + "and mailed on the next poll.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 1440);

    /// <summary>Закреплённый отпечаток сертификата сервера.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Замена проверке, а не её отключение.</b> Key Manager Plus обычно стоит с
    /// самоподписанным сертификатом. Выключить проверку TLS значило бы отдать ключ API любому,
    /// кто встал посередине. Закреплённый SHA-256 принимает ровно один сертификат — тот, что
    /// администратор сверил сам.
    /// </remarks>
    public static readonly SettingKey<string?> ServerSha256 = new(
        "keymanager.server_sha256",
        Default: null,
        "Pinned server certificate (SHA-256)",
        "SHA-256 fingerprint of the Key Manager Plus server certificate, hex with or without "
        + "colons. Set it when the server uses a self-signed certificate: only that exact "
        + "certificate is accepted. Empty means the usual chain validation.",
        Category,
        IsSecurity: true,
        Maximum: 128);
}

/// <summary>Объявления настроек модуля.</summary>
internal sealed class KeyManagerSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        KeyManagerSettings.Enabled,
        KeyManagerSettings.BaseUrl,
        KeyManagerSettings.PollMinutes,
        KeyManagerSettings.ServerSha256,
    ];
}
