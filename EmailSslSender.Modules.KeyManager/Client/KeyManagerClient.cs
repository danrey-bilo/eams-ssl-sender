using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>Сертификат из списка Key Manager Plus.</summary>
/// <param name="CommonName">Common Name.</param>
/// <param name="SerialNumber">Серийный номер в записи KMP.</param>
/// <param name="ExpiresOn">Действует до; <c>null</c>, если дата не разобралась.</param>
internal sealed record KmpCertificate(string CommonName, string SerialNumber, DateOnly? ExpiresOn);

/// <summary>Файл сертификата, выгруженный из Key Manager Plus.</summary>
internal sealed record KmpFile(string FileName, string ContentType, byte[] Content)
{
    /// <summary>Без содержимого: в файле бывает закрытый ключ.</summary>
    public override string ToString() => $"{FileName} ({Content.Length} байт)";
}

/// <summary>Отказ или недоступность Key Manager Plus.</summary>
/// <remarks>⚠ Текст уходит в лог, <c>last_error</c> и администратору — без ключа API и содержимого файлов.</remarks>
internal sealed class KeyManagerException(string message, Exception? inner = null) : Exception(message, inner);

internal interface IKeyManagerClient : IDisposable
{
    Task<IReadOnlyList<KmpCertificate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Выгрузить сертификат в одном из типов <see cref="CertificateFormats"/>.</summary>
    Task<KmpFile> DownloadAsync(
        string commonName, string serialNumber, string format, CancellationToken cancellationToken = default);

    /// <summary>Пароль закрытого ключа; <c>null</c>, если KMP его не хранит.</summary>
    Task<string?> PassphraseAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default);
}

/// <summary>Клиент REST API Key Manager Plus (<c>/api/pki/restapi/&lt;метод&gt;</c>).</summary>
/// <remarks>
/// ⚠ Ключ API — только заголовком <c>AUTHTOKEN</c>: адрес оседает в логах. KMP отвергает необъявленные
/// параметры (<c>EXTRA_PARAM_FOUND</c>) — список запрашивается без <c>INPUT_DATA</c>; конверт ответа непостоянен.
/// </remarks>
internal sealed partial class KeyManagerClient(HttpClient http, string apiKey) : IKeyManagerClient
{
    internal const string Заголовок = "AUTHTOKEN";

    internal const string Путь = "api/pki/restapi/";

    public async Task<IReadOnlyList<KmpCertificate>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var ответ = await ЗапроситьAsync("getAllSSLCertsExpiryDate", детали: null, cancellationToken);

        var корень = await JsonAsync(ответ, cancellationToken);
        Проверить(корень, "список сертификатов");

        // ⚠ Нераспознанный ответ — отказ, а не пустой список: иначе снимок молча опустеет.
        var детали = корень["details"] ?? throw new KeyManagerException(
            "Key Manager Plus ответил на запрос списка сертификатов без поля details — формат ответа не "
            + $"распознан (поля ответа: {string.Join(", ", корень.Select(поле => поле.Key))}).");

        var элементы = Элементы(детали).ToList();
        var список = new List<KmpCertificate>();

        foreach (var элемент in элементы)
        {
            var имя = Строка(элемент, "Common Name");
            var серийный = Строка(элемент, "serialNumber");

            // Без имени или серийного её не выгрузить — пропускаем, а не роняем опрос.
            if (string.IsNullOrWhiteSpace(имя) || string.IsNullOrWhiteSpace(серийный))
            {
                continue;
            }

            список.Add(new KmpCertificate(имя.Trim(), серийный.Trim(), Дата(элемент)));
        }

        if (элементы.Count > 0 && список.Count == 0)
        {
            throw new KeyManagerException(
                $"Key Manager Plus вернул {элементы.Count} записей о сертификатах, но ни в одной нет полей "
                + "«Common Name» и «serialNumber» — формат ответа не распознан (поля записи: "
                + $"{string.Join(", ", элементы[0].Select(поле => поле.Key))}).");
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
            // ⚠ Без содержимого: текст уходит в лог и last_error.
            throw new KeyManagerException(
                $"Key Manager Plus вернул не файл сертификата для {тип} {commonName} "
                + $"({ответ.Content.Headers.ContentType?.MediaType ?? "тип не указан"}, {байты.Length} байт).");
        }

        // ⚠ Имя своё: KMP 7.1.1 называет PEM и KEY одинаково и без расширения.
        var zip = байты.AsSpan().StartsWith("PK\x03\x04"u8);

        return new KmpFile(
            ИмяПоУмолчанию(commonName, тип, zip ? "zip" : null),
            zip ? "application/zip" : CertificateFormats.ContentType(тип),
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

        var детали = Элементы(корень["details"]).FirstOrDefault();
        var пароль = детали is null ? null : Строка(детали, "Passphrase") ?? Строка(детали, "passphrase");

        return string.IsNullOrEmpty(пароль) ? null : пароль;
    }

    public void Dispose() => http.Dispose();

    /// <summary>GET с ключом в заголовке и <c>INPUT_DATA</c>, если есть детали; неуспешный HTTP — отказ.</summary>
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
            // ⚠ Только вид сбоя, не сообщение исключения: в нём бывает адрес с параметрами.
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

    private static string Вид(HttpRequestException ошибка) => ошибка.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "имя сервера не разрешается",
        HttpRequestError.ConnectionError => "соединение не устанавливается",
        HttpRequestError.SecureConnectionError =>
            "TLS не согласован — сертификат сервера не прошёл проверку (цепочка, имя, отзыв): нужен "
            + "сертификат удостоверяющего центра на имя из адреса, самоподписанный не принимается",
        HttpRequestError.ProxyTunnelError => "прокси не пропустил соединение",
        _ => $"сбой HTTP ({ошибка.HttpRequestError})",
    };

    /// <summary>Сообщение конверта или код фильтра KMP; прочее тело (HTML прокси) не берётся.</summary>
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

    /// <summary>Код фильтра безопасности KMP: одно слово заглавными.</summary>
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

    /// <summary>Успех — только явное <c>success</c> в любом регистре.</summary>
    /// <remarks>⚠ Неверный ключ KMP отдаёт с HTTP 200 и <c>"name":"AuthenticationFailure"</c>.</remarks>
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

    /// <summary><c>details</c> — массив, у некоторых методов один объект.</summary>
    private static IEnumerable<JsonObject> Элементы(JsonNode? details) => details switch
    {
        JsonArray массив => массив.OfType<JsonObject>(),
        JsonObject один => [один],
        _ => [],
    };

    private static string? Строка(JsonObject? объект, string имя)
    {
        if (объект?[имя] is not JsonValue значение)
        {
            return null;
        }

        return значение.TryGetValue<string>(out var строка) ? строка : значение.ToJsonString();
    }

    /// <summary>Срок из <c>ExpiryDate</c> («Oct 2, 2025»); неразобранный — <c>null</c>, не отказ.</summary>
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

    /// <summary>JSON вместо файла — по первым байтам: заявленному типу KMP верить нельзя.</summary>
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

    /// <summary>PEM, ASN.1 (DER/PKCS#7/PKCS#12), JKS или ZIP.</summary>
    /// <remarks>⚠ Иначе страница ошибки с HTTP 200 ушла бы получателю как <c>site.key</c>.</remarks>
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

                // ⚠ Ключи, не значения: в значениях может быть закрытый ключ.
                var ключи = string.Join(", ", корень.Select(пара => пара.Key));

                return $"Key Manager Plus вернул JSON вместо файла {тип} сертификата {commonName} "
                    + $"(ключи ответа: {ключи}).";
            }
        }
        catch (JsonException)
        {
            // Не JSON — общий текст ниже.
        }

        return $"Key Manager Plus вернул неразборчивый ответ вместо файла {тип} сертификата {commonName}.";
    }

    /// <summary><c>*.example.com</c> + PEM → <c>wildcard.example.com.pem</c>; ZIP — <c>.pem.zip</c>.</summary>
    internal static string ИмяПоУмолчанию(string commonName, string тип, string? упаковка = null)
    {
        var имя = $"{CertificateFormats.FileBase(commonName)}.{CertificateFormats.Extension(тип)}";

        return упаковка is null ? имя : $"{имя}.{упаковка}";
    }
}
