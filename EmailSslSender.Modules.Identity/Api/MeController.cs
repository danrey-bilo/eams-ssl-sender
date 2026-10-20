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
/// <param name="EffectiveMethod">
/// Чем подтверждается следующий вход: <c>totp</c> у подтвердившего аутентификатор, иначе
/// <c>email</c> при настроенной почте, иначе <c>none</c>.
/// </param>
/// <param name="AvailableMethods">Какие способы подтверждения сейчас доступны на установке.</param>
internal sealed record TwoFactorStatusOut(
    [property: JsonPropertyName("enrolled")] bool Enrolled,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("effective_method")] string? EffectiveMethod = null,
    [property: JsonPropertyName("available_methods")] IReadOnlyList<string>? AvailableMethods = null);

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
    /// <summary>Код из приложения-аутентификатора. Код письмом не принимается.</summary>
    [Required]
    [StringLength(64, MinimumLength = 4)]
    public string Code { get; init; } = string.Empty;
}

/// <summary>Когда личность подтвердили и до какого момента это открывает опасные действия.</summary>
/// <param name="ConfirmedAt">Когда отметка легла на токен этой сессии.</param>
/// <param name="StepUpUntil">
/// До какого момента действия с <c>[RequireRecentSecondFactor]</c> проходят без нового кода.
/// </param>
/// <remarks>
/// ⚠ <b>Срок не вычисляется здесь, а берётся у <see cref="StepUpFreshness"/></b> — того же, кто
/// отказывает <c>SECOND_FACTOR_REQUIRED</c>. Второе место, где написано, сколько живёт
/// подтверждение, разошлось бы с первым молча: экран обещал бы минуты, которых у сервера нет.
/// </remarks>
internal sealed record StepUpOut(
    [property: JsonPropertyName("confirmed_at")] DateTimeOffset ConfirmedAt,
    [property: JsonPropertyName("step_up_until")] DateTimeOffset? StepUpUntil);

/// <summary>Что человек меняет себе сам.</summary>
internal sealed record ProfileIn
{
    /// <summary>Отображаемое имя.</summary>
    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}

/// <summary>Кто я.</summary>
/// <param name="Id">Идентификатор учётной записи.</param>
/// <param name="Email">Адрес, он же логин.</param>
/// <param name="DisplayName">Отображаемое имя.</param>
/// <param name="MustChangePassword">Система требует сменить пароль.</param>
/// <param name="SessionId">Сессия, которой пришли.</param>
/// <param name="MustEnrollTwoFactor">Сессия настройки: сначала подключить аутентификатор.</param>
/// <param name="StepUpUntil">
/// До какого момента код из приложения, подтверждённый на ЭТОЙ сессии, открывает опасные действия
/// (<c>[RequireRecentSecondFactor]</c>). <c>null</c> — подтверждения нет или окно уже прошло: такое
/// действие ответит <c>403 SECOND_FACTOR_REQUIRED</c>, и код спросит <c>POST /api/me/step-up</c>.
/// </param>
internal sealed record MeOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("session_id")] Guid? SessionId,
    [property: JsonPropertyName("must_enroll_two_factor")] bool MustEnrollTwoFactor = false,
    [property: JsonPropertyName("step_up_until")] DateTimeOffset? StepUpUntil = null);

/// <summary>
/// Всё своё: профиль, пароль, второй фактор, сессии.
/// </summary>
/// <remarks>
/// <b>Приставка <c>/api/me</c> — обещание, а не украшение</b> (решение владельца 2026-08-24).
/// Она означает: субъект уже известен и взят из токена, в пути его нет и быть не может.
/// Отсюда два следствия, которые стоят того, чтобы контроллер существовал отдельно.
///
/// Первое: ни один здешний путь не содержит <c>{userId}</c>. Появился — значит действие про
/// ЧУЖОГО человека, и место ему в <c>api/users</c>. Раньше эти две
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
    StepUpFreshness freshness,
    SecondFactorPolicy methods,
    ICurrentUser actor) : ControllerBase
{
    /// <summary>Кто я — и до какого момента на этой сессии открыты опасные действия.</summary>
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
            user.Id, user.Email, user.DisplayName, user.MustChangePassword, actor.SessionId, actor.MustEnrollTwoFactor,
            await freshness.UntilAsync(actor.StepUpAt, cancellationToken));
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
            user.Id, user.Email, user.DisplayName, user.MustChangePassword, actor.SessionId, actor.MustEnrollTwoFactor,
            await freshness.UntilAsync(actor.StepUpAt, cancellationToken));
    }

    /// <summary>Состояние второго фактора.</summary>
    [HttpGet("two-factor")]
    [AllowWhilePasswordChangeRequired("доступные способы нужны для подтверждения смены пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("экрану включения надо знать, с чего начинать")]
    public async Task<TwoFactorStatusOut> TwoFactorStatus(CancellationToken cancellationToken)
    {
        var status = await factors.StatusAsync(cancellationToken);

        return new TwoFactorStatusOut(
            status.Enrolled, status.Confirmed, status.Required, await people.SecondFactorAsync(cancellationToken),
            await methods.AvailableAsync((await users.FindAsync(actor.Id!.Value, cancellationToken))!, cancellationToken));
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
    /// Подтвердить второй фактор на этой сессии кодом из приложения.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Ответ на <c>403 SECOND_FACTOR_REQUIRED</c></b> (решение владельца 2026-10-20):
    /// опасные действия (<c>[RequireRecentSecondFactor]</c>, правка настроек защиты) требуют кода,
    /// подтверждённого в ЭТОЙ сессии за последние <c>auth.step_up_minutes</c> минут. Код ставит
    /// отметку на токен этой сессии — и только на него; <c>step_up_until</c> в ответе говорит, до
    /// какого момента действия пройдут без нового кода.
    ///
    /// ⚠ <b>Пароля не требует, а требует кода.</b> Пароль здесь бесполезен: тот, кто увёл живую
    /// вкладку, чаще всего знает и его — а телефона у него нет.
    ///
    /// ⚠ <b>Только код аутентификатора</b>: код письмом сессию не подтверждает (разбор у
    /// <see cref="StepUpService"/>). Этим же адресом завершают кодом сессию настройки.
    /// </remarks>
    [HttpPost("step-up")]
    [AllowWhilePasswordChangeRequired("подтверждение фактора не мешает обязательной смене пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("код завершает обязательную настройку этой сессии")]
    public async Task<StepUpOut> StepUp(
        [FromBody] StepUpIn body, CancellationToken cancellationToken)
    {
        var когда = await stepUp.ConfirmAsync(body.Code, cancellationToken);

        return new StepUpOut(когда, await freshness.UntilAsync(когда, cancellationToken));
    }

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
