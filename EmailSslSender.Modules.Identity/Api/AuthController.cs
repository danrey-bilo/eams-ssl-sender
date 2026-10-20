using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Contracts.Tokens;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Что присылают, входя.</summary>
internal sealed record LoginIn
{
    [Required]
    [StringLength(320, MinimumLength = 3)]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Класть ли токен обновления в куку.
    /// </summary>
    /// <remarks>
    /// Веб — да и только да: кука <c>HttpOnly</c> недоступна ни одному сценарию на странице,
    /// и одна найденная уязвимость с внедрением кода не уносит с собой сессию. Десктоп — нет:
    /// кук у него нет, токен он хранит сам в зашифрованном виде.
    ///
    /// ⚠ Когда кука ставится, токен обновления в теле ответа <b>не возвращается</b>. Иначе
    /// смысл <c>HttpOnly</c> теряется полностью: то же значение оказалось бы доступно любому
    /// сценарию на странице.
    /// </remarks>
    [JsonPropertyName("use_cookie")]
    public bool UseCookie { get; init; } = true;
}

/// <summary>Что присылают, обновляя токен.</summary>
internal sealed record RefreshIn
{
    /// <summary>Токен обновления. Пусто — берём из куки.</summary>
    [StringLength(200)]
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("use_cookie")]
    public bool UseCookie { get; init; } = true;
}

/// <summary>
/// Выданная пара.
/// </summary>
/// <remarks>
/// ⚠ Половина обновления едет ОДНОЙ дорогой из двух, и выбирает её признак <c>use_cookie</c>:
/// браузер получает её кукой <c>HttpOnly</c> (и тогда поле пусто), скрипт или клиент без кук —
/// полем. Дублировать значение в обе дороги нельзя: это отменило бы <c>HttpOnly</c> целиком.
/// </remarks>
/// <param name="AccessToken">Токен доступа.</param>
/// <param name="AccessExpiresAt">Докуда живёт токен доступа.</param>
/// <param name="RefreshToken">Половина обновления; <c>null</c> — уехала кукой.</param>
/// <param name="RefreshExpiresAt">Докуда живёт половина обновления.</param>
/// <param name="SessionId">Идентификатор выданной сессии.</param>
internal sealed record TokensOut(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("access_expires_at")] DateTimeOffset AccessExpiresAt,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_expires_at")] DateTimeOffset RefreshExpiresAt,
    [property: JsonPropertyName("session_id")] Guid SessionId);

/// <summary>Завершение входа кодом второго фактора.</summary>
internal sealed record TwoFactorIn
{
    /// <summary>Расписка, выданная на первом шаге.</summary>
    [Required]
    [JsonPropertyName("challenge")]
    public string Challenge { get; init; } = string.Empty;

    /// <summary>Код из приложения или код из письма.</summary>
    /// <remarks>
    /// ⚠ Поле одно на оба способа, и оба кода одной длины намеренно: разная длина означала бы
    /// разное поле, разный экран и разную проверку на клиенте. Кода восстановления среди
    /// принимаемых больше нет — их в системе не бывает (решение 0061).
    /// </remarks>
    [Required]
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Класть ли токен обновления в куку. Браузер — да, скрипт или клиент без кук — нет.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Второй шаг обязан слушать этот признак так же, как первый, и до 2026-09-04 не
    /// слушал.</b> Здесь стояло зашитое <c>useCookie: true</c>, а поле отсутствовало вовсе — то
    /// есть у входа с включённым фактором способ «отдай половину телом» пропадал. Настольный
    /// клиент кук не имеет: он получал пару БЕЗ половины обновления, сохранять ему было нечего, и
    /// каждый запуск начинался с логина, пароля и кода. Нашлось живой проверкой владельца.
    ///
    /// ⚠ Умолчание <c>true</c> — то же, что у входа: веб ничего у себя не меняет, а поле
    /// добавляется, а не отбирается (правило 11, изменение не ломающее).
    /// </remarks>
    [JsonPropertyName("use_cookie")]
    public bool UseCookie { get; init; } = true;
}

/// <summary>Просьба выслать код письмом по уже выданной расписке.</summary>
/// <remarks>
/// ⚠ Кода здесь нет и быть не может: человек как раз и говорит, что взять его негде.
/// Пропуск — та же расписка, что и у завершения входа.
/// </remarks>
internal sealed record TwoFactorEmailIn
{
    /// <summary>Расписка, выданная на первом шаге.</summary>
    [Required]
    [JsonPropertyName("challenge")]
    public string Challenge { get; init; } = string.Empty;
}

/// <summary>
/// Вход, обновление, выход и вызов второго фактора.
/// </summary>
/// <remarks>
/// Три вида доступа встречаются здесь все сразу, и это не случайность: вход открыт по
/// существу, обновление тоже (пропуск — сам токен), а всё остальное требует входа, но не
/// требует права — это действия человека над собой.
/// </remarks>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth")]
internal sealed class AuthController(
    SignInService signIn,
    ITokenIssuer tokens,
    ITokenRevoker revoker,
    SenderDbContext db,
    Users.PasswordPolicy policy,
    ICurrentUser actor) : ControllerBase
{
    /// <summary>
    /// Имя куки с токеном обновления.
    /// </summary>
    /// <remarks>
    /// ⚠ Префикс <c>__Host-</c> обязывает браузер принимать куку, только если стоит
    /// <c>Secure</c>, задан <c>Path=/</c> и <b>не задан</b> <c>Domain</c>. Тогда её нельзя
    /// подставить с поддомена и нельзя понизить до HTTP. ASP.NET Core этих правил <b>не
    /// проверяет</b> — соблюдаем сами, иначе браузер молча отбросит куку.
    /// </remarks>
    public const string RefreshCookie = "__Host-sender-refresh";

    /// <summary>Войти по адресу и паролю.</summary>
    [HttpPost("login")]
    [PublicEndpoint("вход: до него человек по определению никто")]
    public async Task<TokensOut> Login([FromBody] LoginIn body, CancellationToken cancellationToken)
    {
        var исход = await signIn.SignInAsync(body.Email, body.Password, Origin(), cancellationToken);

        return Respond(исход, body.UseCookie);
    }

    /// <summary>Обменять токен обновления на новую пару.</summary>
    /// <remarks>
    /// Открыт по той же причине, что и вход: пропуском служит сам токен. Токен доступа к
    /// этому моменту уже истёк — требовать его значило бы, что обновить нельзя никогда.
    /// </remarks>
    [HttpPost("refresh")]
    [PublicEndpoint("обновление: токен доступа к этому моменту уже истёк, пропуск — сам refresh")]
    public async Task<TokensOut> Refresh([FromBody] RefreshIn body, CancellationToken cancellationToken)
    {
        var token = body.RefreshToken;

        if (string.IsNullOrEmpty(token) && Request.Cookies.TryGetValue(RefreshCookie, out var fromCookie))
        {
            token = fromCookie;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new AppException(
                ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.", System.Net.HttpStatusCode.Unauthorized);
        }

        // ⚠ Оболочка остаётся здесь, работа уехала (открытый вопрос 2 плана 10.5). Довод —
        // кука: её имя и признаки ASP.NET Core не проверяет, и две копии этих правил в двух
        // модулях разошлись бы молча.
        var pair = await tokens.RotateAsync(token, Origin(), cancellationToken);

        return Respond(new SignInOutcome(pair), body.UseCookie);
    }

    /// <summary>Выйти: закрыть текущую сессию.</summary>
    [HttpPost("logout")]
    [AllowWhilePasswordChangeRequired("уйти можно всегда: запирать человека внутри — не защита")]
    [AllowWhileTwoFactorEnrollmentRequired("уйти можно всегда")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (actor.SessionId is { } id)
        {
            // Выход из несуществующей или уже закрытой строки — не ошибка: человек нажал
            // «выйти» дважды или его уже отозвали. Отвечать отказом значило бы пугать на
            // ровном месте. Состояние проверяет сам отзыв, условным обновлением.
            await db.InTransactionAsync(
                (token) => revoker.RevokeAsync(id, RevokeReasons.SignedOut, token),
                cancellationToken);
        }

        // Куку снимаем теми же Path и признаками, что при выдаче: иначе браузер оставит
        // старую живой — Delete лишь выставляет просроченную замену, а не удаляет по имени.
        Response.Cookies.Delete(RefreshCookie, CookieOptions());

        return NoContent();
    }

    /// <summary>
    /// Завершить вход кодом второго фактора.
    /// </summary>
    /// <remarks>
    /// Открыт по существу: пришедший сюда ещё не вошёл, а пропуск — расписка, выданная в
    /// ответе на пароль. Принимается и код из приложения, и код восстановления.
    /// </remarks>
    [HttpPost("two-factor")]
    [PublicEndpoint("вход не завершён: пропуск — расписка, выданная в обмен на пароль")]
    public async Task<TokensOut> CompleteTwoFactor(
        [FromBody] TwoFactorIn body, CancellationToken cancellationToken)
    {
        var исход = await signIn.CompleteTwoFactorAsync(
            body.Challenge, body.Code, Origin(), cancellationToken);

        // ⚠ ⚠ Признак берётся ИЗ ЗАПРОСА, а не зашивается. Зашитое `true` отдавало половину
        // обновления только кукой — и настольный клиент, у которого кук нет, оставался без неё.
        return Respond(исход, body.UseCookie);
    }

    /// <summary>
    /// Действующие требования к паролю.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>ЗАВЕДЁН ЭТАПОМ 37.1, И ДО НЕГО ТРЕБОВАНИЯ УЗНАВАЛИСЬ ТОЛЬКО ОТКАЗОМ.</b> Веб-экран
    /// смены пароля обещал чек-лист комментарием в разметке — «правила показаны до попытки и
    /// отмечаются по мере набора», — а показать их было нечем: этого адреса не существовало.
    /// Человек придумывал пароль, получал список требований отказом и придумывал заново.
    ///
    /// ⚠ ⚠ <b>Требует входа, и это не формальность.</b> Наименьшая длина и набор правил —
    /// подсказка перебирающему: она сужает пространство. Тому, кто уже вошёл, она известна и так
    /// — он этим паролем и вошёл. Экран активации политику получает иначе, вместе с проверкой
    /// самой ссылки (<c>GET /api/activation</c>): держатель действующего приглашения не
    /// неизвестный, а человек, подтвердивший доступ к ящику.
    /// </remarks>
    [HttpGet("password-policy")]
    [AllowWhilePasswordChangeRequired("политика нужна до ввода нового пароля")]
    [AllowWhileTwoFactorEnrollmentRequired("смена пароля разрешена до настройки фактора")]
    public async Task<PasswordPolicyOut> PasswordPolicy(CancellationToken cancellationToken)
    {
        var правила = await policy.CurrentAsync(cancellationToken);

        return new PasswordPolicyOut(
            правила.MinLength,
            правила.MaxLengthBytes,
            правила.RequireUppercase,
            правила.RequireLowercase,
            правила.RequireDigit,
            правила.RequireSymbol);
    }

    /// <summary>
    /// Выслать код письмом — «нет доступа к коду?».
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Ради этого адреса и сняты коды восстановления</b> (решение владельца 0061):
    /// запасной путь у потерявшего аутентификатор один — письмо, и нажимается он с того же
    /// экрана, на котором человек стоит.
    ///
    /// ⚠ <b>Расписка не гасится.</b> Нажавший «пришлите письмом» не должен терять вход: он
    /// вводит код в то же поле той же формы. Гасит расписку только завершение входа.
    ///
    /// ⚠ Открыт по существу — по той же причине, что и завершение: пришедший сюда ещё не вошёл,
    /// а пропуск у него тот же самый.
    /// </remarks>
    [HttpPost("two-factor/email")]
    [PublicEndpoint("вход не завершён: пропуск — расписка, выданная в обмен на пароль")]
    public async Task<IActionResult> SendTwoFactorEmail(
        [FromBody] TwoFactorEmailIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await signIn.SendEmailCodeAsync(body.Challenge, cancellationToken);

        return NoContent();
    }

    // --- обвязка ---------------------------------------------------------------------------

    private RequestOrigin Origin() => new(
        HttpContext.Connection.RemoteIpAddress,
        Request.Headers.UserAgent.FirstOrDefault() is { Length: > 0 } agent
            ? agent[..Math.Min(agent.Length, 512)]
            : null);

    /// <summary>
    /// Собрать ответ и, если просили, положить токен обновления в куку.
    /// </summary>
    /// <remarks>
    /// Половина обновления передаётся одной дорогой: браузеру — кукой HttpOnly, клиенту без
    /// кук — полем ответа. Секрет куки никогда не дублируется телом.
    /// </remarks>
    private TokensOut Respond(SignInOutcome исход, bool useCookie)
    {
        var pair = исход.Pair;

        if (!useCookie)
        {
            // Клиент без кук (скрипт, настольное приложение): оба секрета уезжают телом, и
            // хранить их — его забота.
            return new TokensOut(
                pair.Access, pair.AccessExpiresAt, pair.Refresh, pair.RefreshExpiresAt, pair.TokenId);
        }

        var options = CookieOptions();
        options.Expires = pair.RefreshExpiresAt;

        Response.Cookies.Append(RefreshCookie, pair.Refresh, options);

        // В теле их нет: смысл HttpOnly в том, что сценарий на странице до них не
        // добирается, и дублировать значения рядом означало бы отменить всю затею.
        return new TokensOut(
            pair.Access, pair.AccessExpiresAt, null, pair.RefreshExpiresAt, pair.TokenId);
    }

    /// <summary>
    /// Признаки куки.
    /// </summary>
    /// <remarks>
    /// Новый экземпляр на каждый вызов: документация прямо предупреждает не переиспользовать
    /// один объект между куками.
    ///
    /// <c>SameSite=Strict</c>, потому что обновление токена не должно происходить по
    /// переходу с чужого сайта ни при каких обстоятельствах. <c>Secure</c> и <c>Path=/</c> —
    /// требования префикса <c>__Host-</c>; <c>Domain</c> не задаётся по тому же требованию.
    /// </remarks>
    private static Microsoft.AspNetCore.Http.CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
