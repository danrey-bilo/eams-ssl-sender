using Modules.Core.Contracts.Settings;

namespace EmailSslSender.Modules.KeyManager.Settings;

/// <summary>Настройки связи с Key Manager Plus; все — настройки защиты.</summary>
/// <remarks>⚠ Ключа API здесь нет: <c>system_settings</c> открытый, ключ запечатан в <c>keymanager_state</c>.</remarks>
internal static class KeyManagerSettings
{
    public const string Category = "keymanager";

    public static readonly SettingKey<bool> Enabled = new(
        "keymanager.enabled",
        Default: false,
        "Key Manager Plus integration",
        "Poll ManageEngine Key Manager Plus for SSL certificates and mail them to subscribed "
        + "recipients. Off by default: nothing is polled until the address and API key are set.",
        Category,
        IsSecurity: true);

    public static readonly SettingKey<string?> BaseUrl = new(
        "keymanager.base_url",
        Default: null,
        "Key Manager Plus address",
        "HTTPS address of the Key Manager Plus server with its port, for example "
        + "https://kmp.example.local:6565. Plain HTTP is refused: the API key travels in a header.",
        Category,
        IsSecurity: true,
        Maximum: 2048);

    /// <summary>Вебхуков у KMP нет: продление замечается только опросом.</summary>
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
}

internal sealed class KeyManagerSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        KeyManagerSettings.Enabled,
        KeyManagerSettings.BaseUrl,
        KeyManagerSettings.PollMinutes,
    ];
}
