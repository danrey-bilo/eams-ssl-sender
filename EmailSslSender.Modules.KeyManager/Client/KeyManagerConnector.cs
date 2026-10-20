using System.Net;
using System.Security.Cryptography.X509Certificates;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Settings;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>Что задано для связи с Key Manager Plus — без открытия ключа API.</summary>
internal sealed record KeyManagerConfiguration(bool Enabled, string? BaseUrl, string? ServerSha256, bool ApiKeySet)
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
    /// Адрес или отпечаток заданы неверно либо сменились после ввода ключа API.
    /// </exception>
    Task<IKeyManagerClient> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Открывает клиента на свежем <see cref="SocketsHttpHandler"/> по текущим настройкам.
/// </summary>
/// <remarks>
/// ⚠ <b>Соединение собирается на проход, а не живёт синглтоном.</b> Адрес и отпечаток правятся
/// без перезапуска, а проверка TLS зашивается в обработчик при создании; опрос же ходит раз в
/// четверть часа, и цена нового рукопожатия ничтожна.
///
/// ⚠ ⚠ <b>Перенаправления выключены.</b> <c>HttpClient</c> при перенаправлении снимает только
/// <c>Authorization</c>, а свой заголовок <c>AUTHTOKEN</c> понёс бы на любой хост, куда
/// укажет <c>Location</c>.
///
/// ⚠ ⚠ <b>Ключ открывается только для адреса, для которого его ввели</b> (<c>ApiKeyStore</c>):
/// сменивший <c>keymanager.base_url</c> или <c>keymanager.server_sha256</c> получает отказ, а не
/// соединение с ключом в заголовке.
///
/// ⚠ <b>Без закреплённого отпечатка проверяется и отзыв</b> (<c>Online</c>): у живого сервера
/// владельца сертификат Sectigo, и отозванный сертификат обязан не пройти. Значит, серверу нужен
/// выход к OCSP/CRL удостоверяющего центра. С отпечатком отзыв не спрашивается: принимается ровно
/// закреплённый лист, и цепочки у него обычно нет.
/// </remarks>
internal sealed class KeyManagerConnector(ISettingsReader settings, ApiKeyStore apiKey) : IKeyManagerConnector
{
    /// <summary>Сколько ждём ответа целиком.</summary>
    private static readonly TimeSpan Таймаут = TimeSpan.FromSeconds(30);

    /// <summary>Потолок ответа: файлы сертификатов весят килобайты, список — сотни килобайт.</summary>
    private const int ПотолокОтвета = 16 * 1024 * 1024;

    public async Task<KeyManagerConfiguration> ReadAsync(CancellationToken cancellationToken = default) =>
        new(
            await settings.GetAsync(KeyManagerSettings.Enabled, cancellationToken),
            await settings.GetAsync(KeyManagerSettings.BaseUrl, cancellationToken),
            await settings.GetAsync(KeyManagerSettings.ServerSha256, cancellationToken),
            await apiKey.HasAsync(cancellationToken));

    public async Task<IKeyManagerClient> OpenAsync(CancellationToken cancellationToken = default)
    {
        var настройки = await ReadAsync(cancellationToken);

        if (!настройки.Ready)
        {
            throw new AppException(
                ErrorCodes.KeyManagerNotConfigured,
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

        var отпечаток = ServerCertificatePin.Normalize(настройки.ServerSha256);

        if (отпечаток is null && !string.IsNullOrWhiteSpace(настройки.ServerSha256))
        {
            throw new KeyManagerException(
                "keymanager.server_sha256 не похож на отпечаток SHA-256: нужны 64 шестнадцатеричных "
                + "знака, двоеточия допустимы.");
        }

        var ключ = await apiKey.GetForAsync(
                ApiKeyStore.Target(настройки.BaseUrl, настройки.ServerSha256), cancellationToken)
            ?? throw new AppException(
                ErrorCodes.KeyManagerNotConfigured, "Ключ API Key Manager Plus не задан.", HttpStatusCode.Conflict);

        var обработчик = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        обработчик.SslOptions.RemoteCertificateValidationCallback =
            (_, сертификат, _, ошибки) => ServerCertificatePin.Accept(сертификат, ошибки, отпечаток);

        // ⚠ Без отпечатка — обычная цепочка вместе с отзывом; с отпечатком решает только он.
        обработчик.SslOptions.CertificateRevocationCheckMode =
            отпечаток is null ? X509RevocationMode.Online : X509RevocationMode.NoCheck;

        var http = new HttpClient(обработчик, disposeHandler: true)
        {
            BaseAddress = new Uri(адрес.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"),
            Timeout = Таймаут,
            MaxResponseContentBufferSize = ПотолокОтвета,
        };

        return new KeyManagerClient(http, ключ);
    }
}
