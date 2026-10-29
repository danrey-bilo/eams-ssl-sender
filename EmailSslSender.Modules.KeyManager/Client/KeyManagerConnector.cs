using EmailSslSender.Modules.KeyManager.Contracts;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Settings;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;

namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>Настройки связи с Key Manager Plus — без открытия ключа API.</summary>
internal sealed record KeyManagerConfiguration(bool Enabled, string? BaseUrl, bool ApiKeySet)
{
    /// <summary>Адрес и ключ заданы.</summary>
    public bool Ready => !string.IsNullOrWhiteSpace(BaseUrl) && ApiKeySet;

    /// <summary>Включено и готово — опрашивать.</summary>
    public bool Configured => Enabled && Ready;
}

/// <summary>Открывает клиента KMP; интерфейс — ради тестов доставки без HTTPS-сервера.</summary>
internal interface IKeyManagerConnector
{
    Task<KeyManagerConfiguration> ReadAsync(CancellationToken cancellationToken = default);

    /// <exception cref="AppException"><c>KEYMANAGER_NOT_CONFIGURED</c>: нет адреса или ключа.</exception>
    /// <exception cref="KeyManagerException">Адрес неверен или сменился после ввода ключа.</exception>
    Task<IKeyManagerClient> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Клиент на свежем обработчике на каждый проход: адрес и TLS правятся без перезапуска.</summary>
/// <remarks>
/// ⚠ Перенаправления выключены: <c>AUTHTOKEN</c> ушёл бы на хост из <c>Location</c>.
/// TLS — только цепочка УЦ, имя хоста и отзыв; отключить проверку нельзя.
/// </remarks>
internal sealed class KeyManagerConnector(
    ISettingsReader settings, ApiKeyStore apiKey, KeyManagerModuleOptions options) : IKeyManagerConnector
{
    private static readonly TimeSpan Таймаут = TimeSpan.FromSeconds(30);

    private const int ПотолокОтвета = 16 * 1024 * 1024;

    public async Task<KeyManagerConfiguration> ReadAsync(CancellationToken cancellationToken = default) =>
        new(
            await settings.GetAsync(KeyManagerSettings.Enabled, cancellationToken),
            await settings.GetAsync(KeyManagerSettings.BaseUrl, cancellationToken),
            await apiKey.HasAsync(cancellationToken));

    public async Task<IKeyManagerClient> OpenAsync(CancellationToken cancellationToken = default)
    {
        var настройки = await ReadAsync(cancellationToken);

        if (!настройки.Ready)
        {
            throw new AppException(
                KeyManagerErrorCodes.NotConfigured,
                "Key Manager Plus не настроен: задайте keymanager.base_url и ключ API.",
                HttpStatusCode.Conflict);
        }

        if (!Uri.TryCreate(настройки.BaseUrl!.Trim(), UriKind.Absolute, out var адрес)
            || адрес.Scheme != Uri.UriSchemeHttps)
        {
            throw new KeyManagerException(
                "keymanager.base_url должен быть адресом https://: ключ API едет в заголовке, и "
                + "по открытому HTTP его прочтёт любой на пути.");
        }

        var ключ = await apiKey.GetForAsync(ApiKeyStore.Target(настройки.BaseUrl), cancellationToken)
            ?? throw new AppException(
                KeyManagerErrorCodes.NotConfigured, "Ключ API Key Manager Plus не задан.", HttpStatusCode.Conflict);

        var обработчик = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        // ⚠ Принимается только сертификат без единой ошибки: цепочка, имя хоста, отзыв.
        обработчик.SslOptions.RemoteCertificateValidationCallback = (_, _, _, ошибки) => ошибки == SslPolicyErrors.None;
        обработчик.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;

        if (options.TrustedRoots.Count > 0)
        {
            // Только тесты хоста: проверка та же, меняется лишь хранилище корней.
            var политика = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.Online,
            };
            политика.CustomTrustStore.AddRange(options.TrustedRoots);
            обработчик.SslOptions.CertificateChainPolicy = политика;
        }

        var http = new HttpClient(обработчик, disposeHandler: true)
        {
            BaseAddress = new Uri(адрес.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"),
            Timeout = Таймаут,
            MaxResponseContentBufferSize = ПотолокОтвета,
        };

        return new KeyManagerClient(http, ключ);
    }
}
