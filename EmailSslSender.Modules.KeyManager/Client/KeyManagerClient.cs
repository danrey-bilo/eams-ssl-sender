using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>Сертификат из списка Key Manager Plus.</summary>
/// <param name="CommonName">Common Name.</param>
/// <param name="SerialNumber">Серийный номер — как его пишет Key Manager Plus.</param>
/// <param name="ExpiresOn">Действует до; <c>null</c>, если дата не разобралась.</param>
internal sealed record KmpCertificate(string CommonName, string SerialNumber, DateOnly? ExpiresOn);

/// <summary>Файл сертификата, выгруженный из Key Manager Plus.</summary>
internal sealed record KmpFile(string FileName, string ContentType, byte[] Content)
{
    /// <summary>Содержимое не печатается: в файле бывает закрытый ключ.</summary>
    public override string ToString() => $"{FileName} ({Content.Length} байт)";
}

/// <summary>
/// Key Manager Plus не ответил или ответил отказом.
/// </summary>
/// <remarks>
/// ⚠ <b>Текст сообщения составлен здесь и секретов не несёт</b>: ни ключа API, ни содержимого
/// файлов. Он уходит в <c>last_error</c>, в лог и администратору в ответе — ради этого и
/// составлен по-человечески, а не <c>SafeError</c>-цепочкой типов.
/// </remarks>
internal sealed class KeyManagerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Что модуль спрашивает у Key Manager Plus.</summary>
internal interface IKeyManagerClient : IDisposable
{
    /// <summary>Все SSL-сертификаты: Common Name, серийный номер, срок.</summary>
    Task<IReadOnlyList<KmpCertificate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Выгрузить сертификат в одном из типов <see cref="CertificateFormats"/>.</summary>
    Task<KmpFile> DownloadAsync(
        string commonName, string serialNumber, string format, CancellationToken cancellationToken = default);

    /// <summary>Пароль закрытого ключа; <c>null</c>, если Key Manager Plus его не хранит.</summary>
    Task<string?> PassphraseAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Клиент REST API Key Manager Plus (<c>/api/pki/restapi/&lt;метод&gt;</c>).
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Ключ API — только заголовком <c>AUTHTOKEN</c>, никогда в адресе.</b> Документация
/// показывает и вариант «ключ в пути», но адрес запроса оседает в логах — нашем
/// (<c>System.Net.Http.HttpClient</c>), прокси и самого Key Manager Plus.
///
/// ⚠ <b>Вход — JSON в параметре <c>INPUT_DATA</c></b> вида <c>{"operation":{"Details":{…}}}</c>,
/// для GET — в строке запроса, закодированным.
///
/// ⚠ ⚠ <b><c>getAllSSLCertsExpiryDate</c> зовётся без <c>INPUT_DATA</c>.</b> Фильтр
/// безопасности Key Manager Plus пускает к каждому адресу только объявленные параметры, и 7.1.1
/// (build 7110) на <c>INPUT_DATA</c> у этого метода отвечает <c>400 EXTRA_PARAM_FOUND</c> — хотя
/// документация обещает <c>customExpiryDateFormat</c> с build 7040. Срок берётся из
/// <c>ExpiryDate</c> («Oct 2, 2025»). Неизвестный параметр на любом методе даёт тот же отказ.
///
/// ⚠ ⚠ <b>Конверт ответа у Key Manager Plus непостоянен</b>, и разбор терпим к этому нарочно:
/// <c>result.status</c> у одних методов и плоский <c>Status</c> у других, <c>Success</c> и
/// <c>SUCCESS</c>, <c>details</c> — то массив, то объект. Коды ошибок и формат отказа не описаны
/// вовсе. Поэтому успехом считается только явное <c>success</c> в любом регистре, а всё прочее
/// — отказом с текстом, который прислал сервер.
///
/// ⚠ <b>Формат ответа <c>getCertificate</c> документация не описывает</b> («Certificate object»).
/// Принимается файл как есть; JSON вместо файла — отказ, с перечнем ключей ответа (не значений:
/// в них может лежать ключ) — по нему клиент дописывается после сверки с живым сервером.
/// </remarks>
internal sealed partial class KeyManagerClient(HttpClient http, string apiKey) : IKeyManagerClient
{
    /// <summary>Заголовок с ключом API.</summary>
    internal const string Заголовок = "AUTHTOKEN";

    /// <summary>Путь методов относительно адреса сервера.</summary>
    internal const string Путь = "api/pki/restapi/";

    public async Task<IReadOnlyList<KmpCertificate>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var ответ = await ЗапроситьAsync("getAllSSLCertsExpiryDate", детали: null, cancellationToken);

        var корень = await JsonAsync(ответ, cancellationToken);
        Проверить(корень, "список сертификатов");

        var список = new List<KmpCertificate>();

        foreach (var элемент in Элементы(корень["details"]))
        {
            var имя = Строка(элемент, "Common Name");
            var серийный = Строка(элемент, "serialNumber");

            // ⚠ Строка без имени или серийного номера пропускается, а не роняет опрос: по ней
            // нельзя ни выбрать сертификат, ни выгрузить его, а остальные от неё не зависят.
            if (string.IsNullOrWhiteSpace(имя) || string.IsNullOrWhiteSpace(серийный))
            {
                continue;
            }

            список.Add(new KmpCertificate(имя.Trim(), серийный.Trim(), Дата(элемент)));
        }

        return список;
    }

    public async Task<KmpFile> DownloadAsync(
        string commonName, string serialNumber, string format, CancellationToken cancellationToken = default)
    {
        var тип = CertificateFormats.Normalize(format)
            ?? throw new ArgumentException($"Неизвестный тип файла «{format}»", nameof(format));

        using var ответ = await ЗапроситьAsync(
            "getCertificate",
            new JsonObject
            {
                ["common_name"] = commonName,
                ["serial_number"] = serialNumber,
                ["fileType"] = тип,
            },
            cancellationToken);

        var байты = await ответ.Content.ReadAsByteArrayAsync(cancellationToken);

        if (ПохожНаJson(байты))
        {
            throw new KeyManagerException(ОтказВместоФайла(байты, commonName, тип));
        }

        if (байты.Length == 0)
        {
            throw new KeyManagerException(
                $"Key Manager Plus вернул пустой файл {тип} для сертификата {commonName}.");
        }

        if (!ПохожНаСертификат(байты))
        {
            // ⚠ Без содержимого в тексте: что бы там ни лежало, оно уходит в лог и в last_error.
            throw new KeyManagerException(
                $"Key Manager Plus вернул не файл сертификата для {тип} {commonName} "
                + $"({ответ.Content.Headers.ContentType?.MediaType ?? "тип не указан"}, {байты.Length} байт).");
        }

        return new KmpFile(
            ИмяФайла(ответ.Content.Headers.ContentDisposition) ?? ИмяПоУмолчанию(commonName, тип),
            CertificateFormats.ContentType(тип),
            байты);
    }

    public async Task<string?> PassphraseAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default)
    {
        using var ответ = await ЗапроситьAsync(
            "getCertificatePassphrase",
            new JsonObject { ["common_name"] = commonName, ["serial_number"] = serialNumber },
            cancellationToken);

        var корень = await JsonAsync(ответ, cancellationToken);
        Проверить(корень, $"пароль ключа сертификата {commonName}");

        // ⚠ details — то объект, то массив (разбор у класса): берём первый элемент любого вида.
        var детали = Элементы(корень["details"]).FirstOrDefault();
        var пароль = детали is null ? null : Строка(детали, "Passphrase") ?? Строка(детали, "passphrase");

        return string.IsNullOrEmpty(пароль) ? null : пароль;
    }

    public void Dispose() => http.Dispose();

    /// <summary>
    /// Отправить GET с ключом в заголовке и <c>INPUT_DATA</c>, если есть детали; неуспешный
    /// HTTP — отказ.
    /// </summary>
    private async Task<HttpResponseMessage> ЗапроситьAsync(
        string метод, JsonObject? детали, CancellationToken cancellationToken)
    {
        var адрес = Путь + метод;

        if (детали is not null)
        {
            var вход = new JsonObject { ["operation"] = new JsonObject { ["Details"] = детали } }.ToJsonString();
            адрес += $"?INPUT_DATA={Uri.EscapeDataString(вход)}";
        }

        using var запрос = new HttpRequestMessage(HttpMethod.Get, адрес);

        запрос.Headers.TryAddWithoutValidation(Заголовок, apiKey);

        HttpResponseMessage ответ;

        try
        {
            ответ = await http.SendAsync(запрос, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ошибка)
        {
            // ⚠ В текст — вид сбоя (имя, соединение, TLS), а не сообщение исключения целиком:
            // этого достаточно, чтобы понять, куда смотреть, и туда точно не попадёт адрес с
            // параметрами.
            throw new KeyManagerException(
                $"Key Manager Plus недоступен ({метод}): {Вид(ошибка)}.", ошибка);
        }
        catch (TaskCanceledException ошибка) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KeyManagerException($"Key Manager Plus не ответил вовремя ({метод}).", ошибка);
        }

        if (ответ.IsSuccessStatusCode)
        {
            return ответ;
        }

        using (ответ)
        {
            var причина = await ПричинаAsync(ответ, cancellationToken);

            throw new KeyManagerException(ответ.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    $"Key Manager Plus не принял ключ API ({(int)ответ.StatusCode}{причина}).",
                HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther =>
                    $"Key Manager Plus перенаправляет запрос ({(int)ответ.StatusCode}): проверьте адрес — "
                    + "по перенаправлениям ключ API не отправляется.",
                _ => $"Key Manager Plus ответил {(int)ответ.StatusCode} на {метод}{причина}.",
            });
        }
    }

    /// <summary>Вид сетевого сбоя словами.</summary>
    private static string Вид(HttpRequestException ошибка) => ошибка.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "имя сервера не разрешается",
        HttpRequestError.ConnectionError => "соединение не устанавливается",
        HttpRequestError.SecureConnectionError =>
            "TLS не согласован — сертификат сервера не прошёл проверку; для самоподписанного задайте "
            + "keymanager.server_sha256",
        HttpRequestError.ProxyTunnelError => "прокси не пропустил соединение",
        _ => $"сбой HTTP ({ошибка.HttpRequestError})",
    };

    /// <summary>
    /// Текст отказа из тела ответа: сообщение конверта Key Manager Plus или код фильтра
    /// безопасности (<c>EXTRA_PARAM_FOUND</c>, <c>INVALID_METHOD</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Прочее тело (HTML прокси, страница Tomcat) в текст не попадает: он едет в лог и в
    /// <c>last_error</c>.
    /// </remarks>
    private static async Task<string> ПричинаAsync(HttpResponseMessage ответ, CancellationToken cancellationToken)
    {
        var текст = (await ответ.Content.ReadAsStringAsync(cancellationToken)).Trim();

        if (КодОтказа().IsMatch(текст))
        {
            return $": {текст}";
        }

        try
        {
            var корень = JsonNode.Parse(текст) as JsonObject;
            var сообщение = корень is null ? null : Сообщение(корень);

            return string.IsNullOrWhiteSpace(сообщение) ? string.Empty : $": {сообщение}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Тело-код фильтра безопасности Key Manager Plus: одно слово заглавными.</summary>
    [GeneratedRegex("^[A-Z][A-Z0-9_]{2,63}$")]
    private static partial Regex КодОтказа();

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage ответ, CancellationToken cancellationToken)
    {
        var текст = await ответ.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            return JsonNode.Parse(текст) as JsonObject
                ?? throw new KeyManagerException("Key Manager Plus ответил не объектом JSON.");
        }
        catch (JsonException ошибка)
        {
            throw new KeyManagerException("Key Manager Plus ответил не JSON.", ошибка);
        }
    }

    /// <summary>
    /// Успех — только явное <c>success</c> в любом регистре; иначе отказ с текстом сервера.
    /// </summary>
    /// <remarks>
    /// ⚠ Неверный ключ Key Manager Plus отдаёт с кодом <b>200</b>:
    /// <c>{"name":"AuthenticationFailure","result":{"status":"Failure","message":"Login Failed!"}}</c>.
    /// </remarks>
    private static void Проверить(JsonObject корень, string что)
    {
        var статус = Статус(корень);

        if (string.Equals(статус, "success", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var сообщение = Сообщение(корень);
        var текст = string.IsNullOrWhiteSpace(сообщение) ? $"статус «{статус ?? "нет"}»" : сообщение;

        throw new KeyManagerException(ОтказВхода(корень)
            ? $"Key Manager Plus не принял ключ API ({текст})."
            : $"Key Manager Plus отказал ({что}): {текст}");
    }

    private static bool ОтказВхода(JsonObject корень) =>
        string.Equals(Строка(корень, "name"), "AuthenticationFailure", StringComparison.OrdinalIgnoreCase);

    private static string? Статус(JsonObject корень) =>
        Строка(корень["result"] as JsonObject, "status") ?? Строка(корень, "Status") ?? Строка(корень, "status");

    private static string? Сообщение(JsonObject корень) =>
        Строка(корень["result"] as JsonObject, "message") ?? Строка(корень, "Message") ?? Строка(корень, "message");

    /// <summary><c>details</c> — массив; у некоторых методов это один объект.</summary>
    private static IEnumerable<JsonObject> Элементы(JsonNode? details) => details switch
    {
        JsonArray массив => массив.OfType<JsonObject>(),
        JsonObject один => [один],
        _ => [],
    };

    /// <summary>Значение поля строкой; число или логическое — их JSON-записью.</summary>
    private static string? Строка(JsonObject? объект, string имя)
    {
        if (объект?[имя] is not JsonValue значение)
        {
            return null;
        }

        return значение.TryGetValue<string>(out var строка) ? строка : значение.ToJsonString();
    }

    /// <summary>
    /// Срок действия из <c>ExpiryDate</c> вида «Oct 2, 2025»; со временем или в ISO — тоже.
    /// </summary>
    /// <remarks>
    /// ⚠ Неразобранная дата — <c>null</c>, а не отказ: срок только показывается и упорядочивает
    /// сертификаты, а текущий выбирается по времени появления.
    /// </remarks>
    internal static DateOnly? Дата(JsonObject элемент)
    {
        var текст = Строка(элемент, "ExpiryDate")?.Trim();

        if (DateOnly.TryParseExact(
                текст, ["MMM d, yyyy", "MMM dd, yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var дата))
        {
            return дата;
        }

        return DateTime.TryParse(
            текст, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var сВременем)
            ? DateOnly.FromDateTime(сВременем)
            : null;
    }

    /// <summary>
    /// Пришёл ли JSON вместо файла.
    /// </summary>
    /// <remarks>
    /// ⚠ Смотрятся первые байты, а не заявленный тип: сервер может назвать отказ
    /// <c>text/plain</c>, а файл — <c>application/octet-stream</c>. PEM начинается с
    /// <c>-----BEGIN</c>, DER и PKCS#12 — с байта <c>0x30</c>, JKS — с <c>0xFEEDFEED</c>; с
    /// <c>{</c> не начинается ни один.
    /// </remarks>
    private static bool ПохожНаJson(byte[] байты)
    {
        ReadOnlySpan<byte> бом = [0xEF, 0xBB, 0xBF];
        var начало = байты.AsSpan().StartsWith(бом) ? бом.Length : 0;

        foreach (var б in байты.AsSpan(начало))
        {
            if (б is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return б == (byte)'{';
        }

        return false;
    }

    /// <summary>
    /// Похож ли ответ на файл сертификата или ключа хоть в одном из известных видов.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Страница ошибки с кодом 200 — HTML прокси или текст «Session expired» — иначе ушла бы
    /// получателю под именем <c>site.key</c>, а подписка отметилась бы отправленной. Принимаются:
    /// PEM (<c>-----BEGIN</c>), DER, PKCS#7 и PKCS#12 (ASN.1 SEQUENCE, байт <c>0x30</c>), JKS
    /// (<c>0xFEEDFEED</c>) и ZIP (<c>PK\x03\x04</c>) — если Key Manager Plus упакует несколько файлов.
    /// </remarks>
    internal static bool ПохожНаСертификат(byte[] байты)
    {
        ReadOnlySpan<byte> бом = [0xEF, 0xBB, 0xBF];
        var начало = байты.AsSpan();

        if (начало.StartsWith(бом))
        {
            начало = начало[бом.Length..];
        }

        начало = начало.TrimStart(" \t\r\n"u8);

        return начало.StartsWith("-----BEGIN"u8)
            || начало.StartsWith([(byte)0x30])
            || начало.StartsWith([(byte)0xFE, (byte)0xED, (byte)0xFE, (byte)0xED])
            || начало.StartsWith("PK\x03\x04"u8);
    }

    /// <summary>Отказ, пришедший JSON-ом вместо файла.</summary>
    private static string ОтказВместоФайла(byte[] байты, string commonName, string тип)
    {
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(байты)) is JsonObject корень)
            {
                var статус = Статус(корень);
                var сообщение = Сообщение(корень);

                if (!string.Equals(статус, "success", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(сообщение))
                {
                    return $"Key Manager Plus отказал в выгрузке {тип} сертификата {commonName}: {сообщение}";
                }

                // ⚠ Ключи, а не значения: в значениях может лежать сам закрытый ключ.
                var ключи = string.Join(", ", корень.Select(пара => пара.Key));

                return $"Key Manager Plus вернул JSON вместо файла {тип} сертификата {commonName} "
                    + $"(ключи ответа: {ключи}).";
            }
        }
        catch (JsonException)
        {
            // Не JSON всё-таки — ниже общий текст.
        }

        return $"Key Manager Plus вернул неразборчивый ответ вместо файла {тип} сертификата {commonName}.";
    }

    private static string? ИмяФайла(ContentDispositionHeaderValue? disposition)
    {
        var имя = disposition?.FileNameStar ?? disposition?.FileName;

        return string.IsNullOrWhiteSpace(имя) ? null : имя.Trim('"', ' ');
    }

    /// <summary>
    /// Имя файла из Common Name: <c>*.example.com</c> → <c>wildcard.example.com.pem</c>.
    /// </summary>
    internal static string ИмяПоУмолчанию(string commonName, string тип)
    {
        var основа = commonName.Replace("*", "wildcard", StringComparison.Ordinal);
        var чистое = new string([.. основа.Select(с => char.IsLetterOrDigit(с) || с is '.' or '-' or '_' ? с : '_')]);

        return $"{(чистое.Length == 0 ? "certificate" : чистое)}.{CertificateFormats.Extension(тип)}";
    }
}
