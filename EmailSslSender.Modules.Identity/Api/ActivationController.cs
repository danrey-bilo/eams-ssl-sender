using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Активация по приглашению.
/// </summary>
/// <remarks>
/// ⚠ <b>Единственные открытые эндпоинты модуля, и открыты они по существу:</b> человек,
/// который активирует учётную запись, ещё не может войти — у него нет пароля. Требовать
/// здесь право означало бы запереть его снаружи навсегда.
///
/// Пропуском служит сама ссылка: 32 байта случайности, живущая час, гасящаяся с первого
/// применения и хранящаяся в базе только хешем. Отсюда и то, что все отказы выглядят
/// одинаково: различать «нет ссылки», «просрочена» и «уже использована» значило бы
/// подсказывать перебирающему, насколько он близок.
/// </remarks>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/activation")]
internal sealed class ActivationController(UserService users, Users.PasswordPolicy policy) : ControllerBase
{
    /// <summary>
    /// Кого приглашает эта ссылка.
    /// </summary>
    /// <remarks>
    /// Ссылка НЕ гасится: экран показывает адрес до того, как человек что-то ввёл, и
    /// случайно закрытая вкладка не должна требовать нового приглашения.
    ///
    /// Ссылка приходит параметром запроса, а не телом, потому что это чтение — и по той же
    /// причине она осядет в логе прокси. Это осознанная плата: ссылка одноразовая и живёт
    /// час, а альтернатива (POST на чтение) ломает переход по ссылке из письма.
    /// </remarks>
    [HttpGet]
    [PublicEndpoint("активирующий ещё не может войти: пароля у него нет, пропуск — сама ссылка")]
    public async Task<ActionResult<ActivationInfoOut>> Info(
        [FromQuery(Name = "token")] string token, CancellationToken cancellationToken)
    {
        var user = await users.PeekActivationAsync(token, cancellationToken);

        // Одинаковый отказ на всё: нет ссылки, просрочена, использована, учётка отключена.
        //
        // ⚠ Своим исключением, а не NotFound(): голый результат MVC уходит без конверта, то
        // есть без машинного кода и номера обращения, и клиент, написанный под наш формат,
        // показал бы пустую ошибку. Найдено разбором 2026-08-22.
        if (user is null)
        {
            throw AppException.NotFound("Ссылка недействительна");
        }

        // ⚠ ⚠ Требования к паролю едут ВМЕСТЕ с проверкой ссылки (этап 37.1): активирующий войти
        // не может, а узнавать их отказом на первом своём пароле — худший из способов. Отдельный
        // адрес политики требует входа, и правильно делает.
        var правила = await policy.CurrentAsync(cancellationToken);

        return new ActivationInfoOut(
            user.Email,
            user.DisplayName,
            new PasswordPolicyOut(
                правила.MinLength,
                правила.MaxLengthBytes,
                правила.RequireUppercase,
                правила.RequireLowercase,
                правила.RequireDigit,
                правила.RequireSymbol));
    }

    /// <summary>Задать пароль и активировать учётную запись.</summary>
    [HttpPost]
    [PublicEndpoint("завершение приглашения: единственный способ у человека получить доступ")]
    public async Task<UserOut> Activate(
        [FromBody] ActivateIn body, CancellationToken cancellationToken) =>
        UserOut.From(await users.ActivateAsync(body.Token, body.Password, cancellationToken));
}
