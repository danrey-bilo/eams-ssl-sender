using EmailSslSender.Modules.KeyManager.Contracts;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Settings;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;

namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>Что задано для связи с Key Manager Plus — без открытия ключа API.</summary>
internal sealed record KeyManagerConfiguration(bool Enabled, string? BaseUrl, bool ApiKeySet)
{
    /// <summary>Можно ли хотя бы попробовать соединиться: адрес и ключ заданы.</summary>
    public bool Ready => !string.IsNullOrWhiteSpace(BaseUrl) && ApiKeySet;

    /// <summary>Опрашивать ли: включено и готово.</summary>
    public bool Configured => Enabled && Ready;
}

/// <summary>
/// Кто открывает клиента Key Manager Plus по текущим настройкам.
/// </summary>
/// <remarks>
/// ⚠ Интерфейс заведён ради тестов доставки: они проверяют выбор сертификата, сроки и
/// атомарность, и настоящий HTTPS-сервер им не нужен. Сам клиент проверяется отдельно на
/// заглушке <c>HttpMessageHandler</c>.
/// </remarks>
internal interface IKeyManagerConnector
{
    /// <summary>Прочитать настройки и признак «ключ задан».</summary>
    Task<KeyManagerConfiguration> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Открыть клиента.</summary>
    /// <exception cref="AppException"><c>KEYMANAGER_NOT_CONFIGURED</c>: нет адреса или ключа.</exception>
    /// <exception cref="KeyManagerException">
    /// Адрес задан неверно либо сменился после ввода ключа API.
    /// </exception>
    Task<IKeyManagerClient> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Открывает клиента на свежем <see cref="SocketsHttpHandler"/> по текущим настройкам.
/// </summary>
/// <remarks>
/// ⚠ <b>Соединение собирается на проход, а не живёт синглтоном.</b> Адрес правится без
/// перезапуска, а проверка TLS зашивается в обработчик при создании; опрос же ходит раз в
/// четверть часа, и цена нового рукопожатия ничтожна.
///
/// ⚠ ⚠ <b>Перенаправления выключены.</b> <c>HttpClient</c> при перенаправлении снимает только
/// <c>Authorization</c>, а свой заголовок <c>AUTHTOKEN</c> понёс бы на любой хост, куда
/// укажет <c>Location</c>.
///
/// ⚠ ⚠ <b>Ключ открывается только для адреса, для которого его ввели</b> (<c>ApiKeyStore</c>):
/// сменивший <c>keymanager.base_url</c> получает отказ, а не соединение с ключом в заголовке.
///
/// ⚠ ⚠ <b>Сертификат сервера — только по цепочке удостоверяющего центра</b> (решение владельца
/// 2026-10-28, версия 2.5): цепочка, имя хоста и отзыв (<c>Online</c>). У живого сервера владельца
/// сертификат Sectigo, и отозванный сертификат обязан не пройти — значит, серверу нужен выход к
/// OCSP/CRL центра. Закреплённого отпечатка для самоподписанного сертификата больше нет, и
/// отключить проверку нечем; свой центр — корневой сертификат в <c>SSL_CERT_FILE</c> контейнера.
/// </remarks>
internal sealed class KeyManagerConnector(
    ISettingsReader settings, ApiKeyStore apiKey, KeyManagerModuleOptions options) : IKeyManagerConnector
{
    /// <summary>Сколько ждём ответа целиком.</summary>
    private static readonly TimeSpan Таймаут = TimeSpan.FromSeconds(30);

    /// <summary>Потолок ответа: файлы сертификатов весят килобайты, список — сотни килобайт.</summary>
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
            // Только тесты хоста: заглушка Key Manager Plus — корень своей цепочки. Проверка та же, меняется
            // лишь хранилище корней.
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
