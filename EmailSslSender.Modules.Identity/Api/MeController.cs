using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Пароль — переспрашивается там, где угнанная вкладка иначе сняла бы защиту.</summary>
internal sealed record TwoFactorConfirmIn
{
    /// <summary>Код из приложения.</summary>
    [Required]
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
}

/// <summary>Действие, требующее подтвердить, кто просит.</summary>
internal sealed record PasswordIn
{
    /// <summary>Текущий пароль.</summary>
    [Required]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Что показать человеку, включающему фактор.</summary>
internal sealed record TwoFactorSetupOut(
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("uri")] string Uri);

/// <summary>Состояние второго фактора.</summary>
/// <param name="Enrolled">Заведён ли аутентификатор.</param>
/// <param name="Confirmed">Подтверждён ли он кодом.</param>
/// <param name="Required">Требует ли система фактор от этого человека.</param>
/// <param name="ChosenMethod">Что человек выбрал себе. <c>null</c> — «как в системе».</param>
/// <param name="EffectiveMethod">
/// Что будет действовать при следующем входе.
///
/// ⚠ ⚠ Два поля, а не одно, и разница видна человеку. Выбранное — то, что он нажал;
/// действующее — то, что получится с учётом послаблений: <c>totp</c> без заведённого
/// аутентификатора становится почтой, почта без настроенной почты — ничем. Показав одно
/// «действующее», экран соврал бы про его выбор; показав одно «выбранное» — про то, что
/// произойдёт.
/// </param>
/// <param name="AvailableMethods">Какие способы подтверждения сейчас доступны на установке.</param>
internal sealed record TwoFactorStatusOut(
    [property: JsonPropertyName("enrolled")] bool Enrolled,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("chosen_method")] string? ChosenMethod = null,
    [property: JsonPropertyName("effective_method")] string? EffectiveMethod = null,
    [property: JsonPropertyName("available_methods")] IReadOnlyList<string>? AvailableMethods = null);

/// <summary>Каким быть своему второму фактору.</summary>
internal sealed record TwoFactorMethodIn(
    [property: JsonPropertyName("method")] string? Method);

/// <summary>Смена своего пароля.</summary>
internal sealed record ChangePasswordIn
{
    /// <summary>Текущий пароль. Спрашивается обязательно — иначе угнанная вкладка запирает хозяина.</summary>
    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("current_password")]
    public string CurrentPassword { get; init; } = string.Empty;

    /// <summary>Новый.</summary>
    /// <remarks>
    /// ⚠ Предел добавлен этапом 59.6 обоим полям: у <c>LoginIn</c> и <c>ActivateIn</c> он стоял,
    /// у смены и сброса — нет. Настоящая граница в байтах живёт в <c>PasswordPolicy</c>.
    /// </remarks>
    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("new_password")]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>Код для переподтверждения личности.</summary>
internal sealed record StepUpIn
{
    /// <summary>Код из приложения или код восстановления.</summary>
    [Required]
    [StringLength(64, MinimumLength = 4)]
    public string Code { get; init; } = string.Empty;
}

/// <summary>Когда личность подтвердили.</summary>
/// <remarks>
/// ⚠ <b>Времени жизни отметки здесь нет намеренно.</b> Оно принадлежит защищаемому ресурсу,
/// а не подтверждению: окно называет тот, кто отказывает, — в подробностях отказа
/// <c>SECOND_FACTOR_REQUIRED</c>. Назови его здесь — и появилось бы второе место, где
/// написано, сколько живёт подтверждение.
/// </remarks>
internal sealed record StepUpOut(
    [property: JsonPropertyName("confirmed_at")] DateTimeOffset ConfirmedAt);

/// <summary>Что человек меняет себе сам.</summary>
internal sealed record ProfileIn
{
    /// <summary>Отображаемое имя.</summary>
    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}

/// <summary>Кто я.</summary>
internal sealed record MeOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("session_id")] Guid? SessionId,
    [property: JsonPropertyName("must_enroll_two_factor")] bool MustEnrollTwoFactor = false);
/// <summary>
/// Всё своё: профиль, права, пароль, второй фактор, сессии.
/// </summary>
/// <remarks>
/// <b>Приставка <c>/api/me</c> — обещание, а не украшение</b> (решение владельца 2026-08-24).
/// Она означает: субъект уже известен и взят из токена, в пути его нет и быть не может.
/// Отсюда два следствия, которые стоят того, чтобы контроллер существовал отдельно.
///
/// Первое: ни один здешний путь не содержит <c>{userId}</c>. Появился — значит действие про
/// ЧУЖОГО человека, и место ему в <c>api/users</c>, где оно спросит право. Раньше эти две
/// породы жили вперемешку в одном контроллере <c>api/auth</c>, и отличить их можно было
/// только чтением тела метода.
///
/// Второе: здесь не спрашивают прав — спрашивают, вошёл ли человек. Действие над собой
/// подтверждается знанием своего пароля, а не строкой в роли; право распоряжаться собой
/// означало бы, что распоряжаться собой можно запретить.
///
/// ⚠ <b>Вход сюда не относится.</b> <c>login</c>, <c>refresh</c>, <c>logout</c> и вызов
/// второго фактора остались в <c>api/auth</c>: до успешного входа субъекта не существует, и
/// <c>/api/me/login</c> означал бы «мой вход» у того, кого ещё нет.
///
/// ⚠ <b>Список сессий — не здесь.</b> Сессии принадлежат <c>EmailSslSender.Modules.Tokens</c>,
/// и <c>/api/me/sessions</c> отвечает его контроллер. Приставка общая, потому что она про
/// субъекта, а не про модуль-владельца.
/// </remarks>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/me")]
internal sealed class MeController(
    UserService people,
    TwoFactorService factors,
    UserRepository users,
    StepUpService stepUp,
    SecondFactorPolicy methods,
    ICurrentUser actor) : ControllerBase
{
    /// <summary>Кто я.</summary>
    [HttpGet]
    [AllowWhilePasswordChangeRequired("экрану смены пароля надо знать, кто на нём стоит")]
    [AllowWhileTwoFactorEnrollmentRequired("экрану включения фактора — тоже")]
    public async Task<MeOut> Me(CancellationToken cancellationToken)
    {
        // ⚠ ЧТЕНИЕ, а не `FindAsync`: тот берёт сущность целиком и с трекингом — это путь
        // записи. Здесь ничего не меняется, и хешу пароля незачем покидать базу (27.7).
        var user = await users.ReadAsync(actor.Id!.Value, cancellationToken)
            ?? throw AppException.NotFound("Пользователь не найден");

        return new MeOut(
            user.Id, user.Email, user.DisplayName, user.MustChangePassword, actor.SessionId, actor.MustEnrollTwoFactor);
    }

    /// <summary>Изменить своё имя.</summary>
    /// <remarks>
    /// Своё — и только имя. Адрес не меняется вовсе: он логин, и на него ссылается журнал.
    /// Флаги тем более: право распоряжаться собственной активностью означало бы, что прав
    /// нет вообще.
    /// </remarks>
    [HttpPatch]
    public async Task<MeOut> UpdateMe(
        [FromBody] ProfileIn body, CancellationToken cancellationToken)
    {
        var user = await people.UpdateOwnProfileAsync(body.DisplayName, cancellationToken);

        return new MeOut(
            user.Id, user.Email, user.DisplayName, user.MustChangePassword, actor.SessionId, actor.MustEnrollTwoFactor);
    }

    /// <summary>Состояние второго фактора.</summary>
    [HttpGet("two-factor")]
    [AllowWhilePasswordChangeRequired("доступные способы нужны для подтверждения смены пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("экрану включения надо знать, с чего начинать")]
    public async Task<TwoFactorStatusOut> TwoFactorStatus(CancellationToken cancellationToken)
    {
        var status = await factors.StatusAsync(cancellationToken);
        var (выбран, действует) = await people.SecondFactorAsync(cancellationToken);

        return new TwoFactorStatusOut(
            status.Enrolled, status.Confirmed, status.Required, выбран, действует,
            await methods.AvailableAsync((await users.FindAsync(actor.Id!.Value, cancellationToken))!, cancellationToken));
    }

    /// <summary>
    /// Выбрать себе способ второго фактора.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b><c>null</c> означает «как в системе», и это третье состояние обязательно.</b>
    /// Без него смена умолчания установки не подействовала бы ни на кого, кто способ однажды
    /// выбрал, — то есть настройка перестала бы работать молча и навсегда.
    ///
    /// ⚠ <b>Выбрать «нет фактора» нельзя, если система его требует.</b> Настройка установки —
    /// потолок, а не предложение: иначе обязательный фактор снимался бы одним запросом от
    /// самого человека.
    ///
    /// ⚠ <b>Ответ — то же состояние</b>, что у <c>GET</c>: экран перерисовывается целиком, и
    /// второй запрос за тем же был бы дороже этих полей.
    /// </remarks>
    [HttpPut("two-factor")]
    [AllowWhileTwoFactorEnrollmentRequired("выбор способа — часть включения фактора")]
    public async Task<TwoFactorStatusOut> ChooseTwoFactor(
        [FromBody] TwoFactorMethodIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await people.ChooseSecondFactorAsync(body.Method, cancellationToken);

        return await TwoFactorStatus(cancellationToken);
    }

    /// <summary>
    /// Завести секрет и показать его.
    /// </summary>
    /// <remarks>
    /// ⚠ Единственный момент, когда секрет покидает сервер. Больше его не показывают
    /// никогда: потерял — заводи новый.
    /// </remarks>
    [HttpPost("two-factor/totp/setup")]
    [AllowWhileTwoFactorEnrollmentRequired("ради этого действия ограничение и заведено")]
    public async Task<TwoFactorSetupOut> SetupTwoFactor(CancellationToken cancellationToken)
    {
        var setup = await factors.SetupAsync(cancellationToken);

        return new TwoFactorSetupOut(setup.Secret, setup.Uri);
    }

    /// <summary>
    /// Подтвердить фактор кодом.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Пароля не спрашивает</b> (решение владельца 0061): сессия уже доказала, кто это,
    /// а код — что аутентификатор в руках. Прежняя редакция спрашивала, потому что подтверждение
    /// закрывало все прочие сессии; теперь оно их не закрывает, и оба изменения — одно решение,
    /// а не два. Разбор — у службы.
    ///
    /// ⚠ <b>Кодов восстановления не возвращает: их нет.</b> Потерявший аутентификатор берёт код
    /// письмом.
    /// </remarks>
    [HttpPost("two-factor/totp/confirm")]
    [AllowWhileTwoFactorEnrollmentRequired("ради этого действия ограничение и заведено")]
    public async Task<IActionResult> ConfirmTwoFactor(
        [FromBody] TwoFactorConfirmIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await factors.ConfirmAsync(body.Code, cancellationToken);

        return NoContent();
    }

    /// <summary>Отключить фактор. Требует пароля.</summary>
    [HttpPost("two-factor/totp/disable")]
    public async Task<IActionResult> DisableTwoFactor(
        [FromBody] PasswordIn body, CancellationToken cancellationToken)
    {
        var status = await TwoFactorStatus(cancellationToken);
        if (status.AvailableMethods?.Contains(SecondFactorMethods.Email) != true)
            throw new AppException(ErrorCodes.TwoFactorNotEnrolled,
                "Приложение — единственный доступный второй фактор. Сначала настройте отправку кодов по почте.",
                System.Net.HttpStatusCode.Conflict);
        await factors.DisableAsync(body.Password, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Подтвердить второй фактор на этой сессии.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Действия после входа кода не требуют</b> (решение владельца, 2026-10-20): второй
    /// фактор спрашивается только при входе. Этот адрес остался ради одного случая — завершить
    /// кодом сессию настройки; отметку фактора на сессии он ставит так же, как вход с кодом.
    /// </remarks>
    [HttpPost("step-up")]
    [AllowWhilePasswordChangeRequired("подтверждение фактора не мешает обязательной смене пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("код завершает обязательную настройку этой сессии")]
    public async Task<StepUpOut> StepUp(
        [FromBody] StepUpIn body, CancellationToken cancellationToken) =>
        new(await stepUp.ConfirmAsync(body.Code, cancellationToken));

    /// <summary>
    /// Сменить себе пароль.
    /// </summary>
    /// <remarks>
    /// ⚠ Единственное действие, доступное тому, кому система велела сменить пароль, — ради
    /// него ограничение и существует. До 2026-08-22 эндпоинта не было вовсе: флаг ставили,
    /// показывали в профиле, и снять его было нечем.
    ///
    /// Права не спрашиваются: это действие человека над собой, и подтверждает его знание
    /// текущего пароля, а не строка в роли.
    /// </remarks>
    [HttpPost("password")]
    [AllowWhilePasswordChangeRequired("ради этого действия ограничение и заведено")]
    [AllowWhileTwoFactorEnrollmentRequired("сменить пароль можно и не включив ещё фактор")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordIn body, CancellationToken cancellationToken)
    {
        await people.ChangePasswordAsync(body.CurrentPassword, body.NewPassword, cancellationToken);

        return NoContent();
    }
}
