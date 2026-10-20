using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Задать новый пароль по ссылке сброса.</summary>
internal sealed record ResetPasswordIn
{
    /// <summary>Ссылка, выданная администратором.</summary>
    [Required]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Новый пароль.</summary>
    /// <remarks>
    /// ⚠ Предел — как у <c>LoginIn</c> и <c>ActivateIn</c>; до этапа 59.6 его здесь не было, и
    /// путь сброса отвечал на слишком длинный пароль пятисоткой из хешера вместо конверта.
    /// Настоящая граница считается в байтах и живёт в <c>PasswordPolicy</c>: этот атрибут
    /// дешевле и отсекает раньше, но символы — не байты.
    /// </remarks>
    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Кто просит ссылку на смену пароля.</summary>
internal sealed record ResetRequestIn
{
    /// <summary>Адрес, на который слать ссылку.</summary>
    [Required]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Ссылка сброса в теле запроса: почтовый секрет не попадает в URL API.</summary>
internal sealed record ResetLinkIn
{
    [Required]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}

/// <summary>
/// Сброс пароля по ссылке.
/// </summary>
/// <remarks>
/// Устроено так же, как активация, и по тем же причинам: пришедший по ссылке ещё не вошёл,
/// пропуск — сама ссылка, а отказ один на все случаи.
///
/// ⚠ <b>Начало появилось на этапе 17.6, и появилось оно вместе с почтой.</b> Прежде
/// публичного «забыл пароль» здесь не было намеренно: отдать ссылку неаутентифицированному
/// значит по одному лишь адресу забрать чужую учётную запись. Почта убирает это целиком —
/// ссылка уходит <b>на ящик</b>, а в ответе её нет.
/// </remarks>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/password-reset")]
internal sealed class PasswordResetController(UserService users, PasswordPolicy policy) : ControllerBase
{
    /// <summary>До ввода пароля проверить ссылку и показать действующие требования.</summary>
    [HttpPost("info")]
    [PublicEndpoint("владелец ссылки ещё не вошёл; пропуском служит действующий одноразовый токен")]
    public async Task<PasswordPolicyOut> Info([FromBody] ResetLinkIn body, CancellationToken cancellationToken)
    {
        if (!await users.CanResetPasswordAsync(body.Token, cancellationToken))
            throw AppException.NotFound("Ссылка недействительна");
        var rules = await policy.CurrentAsync(cancellationToken);
        return new PasswordPolicyOut(rules.MinLength, rules.MaxLengthBytes, rules.RequireUppercase,
            rules.RequireLowercase, rules.RequireDigit, rules.RequireSymbol);
    }

    /// <summary>Задать новый пароль.</summary>
    [HttpPost]
    [PublicEndpoint("сбрасывающий пароль по определению не вошёл: пропуск — сама ссылка")]
    public async Task<UserOut> Reset(
        [FromBody] ResetPasswordIn body, CancellationToken cancellationToken) =>
        UserOut.From(await users.ResetPasswordAsync(body.Token, body.Password, cancellationToken));

    /// <summary>
    /// Попросить ссылку на смену пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b><c>202</c> при ЛЮБОМ исходе, и это главное свойство эндпоинта.</b> Адреса
    /// нет, адрес есть, учётная запись отключена, почта не настроена, предел частоты исчерпан —
    /// ответ один и тот же. Различие ответов сделало бы форму <b>определителем существующих
    /// учётных записей</b>: перебрав список сотрудников компании, посторонний узнал бы, у кого
    /// здесь есть доступ.
    ///
    /// ⚠ <b>Ссылка НЕ возвращается ни при каком исходе</b>, включая ненастроенную почту. Это
    /// отличие от административного сброса: там ссылку показывают тому, кто уже вошёл и имеет
    /// право, здесь — неизвестно кому.
    ///
    /// ⚠ <b>Перебор ограничивает предел частоты почты</b> (пара «адрес + вид»), а не отдельный
    /// счётчик: письмо просто перестаёт ставиться, а ответ остаётся тем же <c>202</c>.
    /// </remarks>
    [HttpPost("request")]
    [PublicEndpoint("забывший пароль по определению не вошёл; ответ одинаков при любом исходе")]
    // ⚠ Не `Request`: у `ControllerBase` уже есть свойство с таким именем, и совпадение
    // скрыло бы наследника. Тот же случай, что у `НетТакой` вместо `NotFound` в ленте
    // уведомлений — и там он однажды дал бы пустой 404 мимо конверта ошибок.
    public async Task<IActionResult> Ask(
        [FromBody] ResetRequestIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await users.RequestPasswordResetAsync(body.Email, cancellationToken);

        return Accepted();
    }
}
