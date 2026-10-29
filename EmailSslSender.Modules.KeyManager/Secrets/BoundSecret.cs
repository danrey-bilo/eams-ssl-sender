using System.Text.Json;

namespace EmailSslSender.Modules.KeyManager.Secrets;

/// <summary>Секрет вместе с адресом, для которого его ввели: сменивший адрес вводит секрет заново.</summary>
/// <remarks>
/// ⚠ Привязка — внутри запечатанного, AEAD не даст её переставить. Класс, а не <c>record</c>:
/// напечатанный <c>record</c> показал бы секрет в логе.
/// </remarks>
public sealed class BoundSecret(string target, string secret)
{
    private const int Версия = 1;

    /// <summary>Адрес в нормальном виде владельца секрета.</summary>
    public string Target { get; } = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>Сам секрет.</summary>
    public string Secret { get; } = secret ?? throw new ArgumentNullException(nameof(secret));

    /// <summary>Сравнение точное: нормализует владелец.</summary>
    public bool IsFor(string target) => string.Equals(Target, target, StringComparison.Ordinal);

    /// <summary>Текст для запечатывания.</summary>
    public string Pack() => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["v"] = Версия,
        ["target"] = Target,
        ["secret"] = Secret,
    });

    /// <summary>Разобрать открытый текст; <c>null</c> — голый секрет без привязки.</summary>
    /// <remarks>Признаётся только объект этой версии с обоими полями-строками.</remarks>
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

    /// <summary>Без секрета.</summary>
    public override string ToString() => $"<секрет для {Target}>";
}
