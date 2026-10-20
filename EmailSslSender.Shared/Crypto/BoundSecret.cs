using System.Text.Json;

namespace EmailSslSender.Shared.Crypto;

/// <summary>
/// Секрет вместе с адресом, для которого его ввели.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Секрет уходит только туда, для чего его ввели.</b> Учётная запись, перехваченная на
/// время, иначе могла бы сменить адрес Key Manager Plus (или сервер SMTP) на свой, закрепить свой
/// сертификат и нажать «проверить» — сервер сам отнёс бы ключ API (пароль SMTP) по новому адресу.
/// С привязкой новый адрес требует ввести секрет заново, то есть знать его.
///
/// ⚠ ⚠ <b>Привязка лежит ВНУТРИ запечатанного, а не колонкой рядом.</b> Так не нужна смена схемы,
/// и тот, кто пишет в базу, не переставит её: AEAD не откроется. Строки разделения ключей
/// (<c>sender:system-secret:…</c>, <c>sender:keymanager:api-key</c>) при этом не меняются.
///
/// ⚠ <b>Старый формат — голый секрет без привязки</b> — <see cref="Unpack"/> отличает и отвечает
/// <c>null</c>. Хранилище секрета привязывает такой к адресу, действующему при первом
/// использовании, и перепечатывает (доверие с первого раза): иначе обновление сломало бы
/// работающую установку владельца.
///
/// ⚠ Класс, а не <c>record</c>: напечатанный <c>record</c> показал бы секрет в логе.
/// </remarks>
public sealed class BoundSecret(string target, string secret)
{
    /// <summary>Версия формата. Другая — не наш формат.</summary>
    private const int Версия = 1;

    /// <summary>Адрес, для которого секрет ввели, в нормальном виде владельца секрета.</summary>
    public string Target { get; } = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>Сам секрет.</summary>
    public string Secret { get; } = secret ?? throw new ArgumentNullException(nameof(secret));

    /// <summary>Введён ли секрет для этого адреса. Сравнение точное: нормализует владелец.</summary>
    public bool IsFor(string target) => string.Equals(Target, target, StringComparison.Ordinal);

    /// <summary>Текст для запечатывания.</summary>
    public string Pack() => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["v"] = Версия,
        ["target"] = Target,
        ["secret"] = Secret,
    });

    /// <summary>
    /// Разобрать открытый текст; <c>null</c> — старый формат, голый секрет без привязки.
    /// </summary>
    /// <remarks>
    /// ⚠ Старый секрет, по случайности похожий на JSON, остаётся старым: признаётся только объект
    /// ровно этой версии с обоими полями-строками.
    /// </remarks>
    public static BoundSecret? Unpack(string opened)
    {
        ArgumentNullException.ThrowIfNull(opened);

        if (!opened.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var документ = JsonDocument.Parse(opened);
            var корень = документ.RootElement;

            return корень.ValueKind == JsonValueKind.Object
                && корень.TryGetProperty("v", out var версия)
                && версия.ValueKind == JsonValueKind.Number
                && версия.TryGetInt32(out var номер)
                && номер == Версия
                && корень.TryGetProperty("target", out var адрес)
                && адрес.ValueKind == JsonValueKind.String
                && корень.TryGetProperty("secret", out var значение)
                && значение.ValueKind == JsonValueKind.String
                    ? new BoundSecret(адрес.GetString()!, значение.GetString()!)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Сам секрет не печатается никогда.</summary>
    public override string ToString() => $"<секрет для {Target}>";
}
