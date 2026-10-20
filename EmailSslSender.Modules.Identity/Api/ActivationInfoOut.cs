using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Что показывает экран активации до ввода пароля.</summary>
/// <remarks>
/// ⚠ ⚠ ⚠ <b>ТРЕБОВАНИЯ К ПАРОЛЮ ЕДУТ ЗДЕСЬ ЖЕ (этап 37.1), и это исключение с обоснованием.</b>
/// Отдельный адрес <c>GET /api/auth/password-policy</c> требует входа осознанно: набор правил —
/// подсказка перебирающему. Но активирующий войти <b>не может</b> — пароля у него ещё нет, — а
/// узнавать требования отказом на первом же своём пароле хуже всего. Держатель действующего
/// приглашения при этом не неизвестный: он подтвердил доступ к почтовому ящику.
///
/// ⚠ Поле <b>добавлено</b>, а не заменило существующее: изменение неломающее (правило 11).
/// </remarks>
internal sealed record ActivationInfoOut(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("policy")] PasswordPolicyOut Policy);
