using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Выданная ссылка сброса пароля.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Поле стало обнуляемым на этапе 17.6, и это единственное записанное исключение
/// из решения 0004.</b> Ссылка возвращается <b>только тогда, когда письмо не ушло</b>: почта не
/// настроена, предел частоты исчерпан, внешний адрес не задан. Ушло — секрет уехал по своему
/// каналу, и второй раз показывать его в ответе API незачем: он попадёт в лог прокси, в историю
/// вкладки и в отчёт об ошибке.
///
/// ⚠ Пустая строка вместо <c>null</c> была бы ловушкой для клиента: «есть поле, а в нём
/// ничего» читается как ошибка сервера, а не как «ссылка ушла письмом». Разбор — решение 0031 и
/// <c>docs/api/COMPATIBILITY.md</c>.
/// </remarks>
internal sealed record ResetIssuedOut(
    [property: JsonPropertyName("reset_token")] string? ResetToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
