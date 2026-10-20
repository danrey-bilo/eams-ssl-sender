using System.ComponentModel.DataAnnotations;
using EmailSslSender.Shared.Contracts.Tokens;
using System.Net;
using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Mail;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Naming;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>Что задают при заведении учётной записи.</summary>
/// <param name="Email">Адрес, он же логин.</param>
/// <param name="DisplayName">Как показывать.</param>
/// <param name="Password">
/// Пароль. <c>null</c> — обычный путь: человек придумает его сам, перейдя по ссылке.
/// </param>
/// <param name="MustChangePassword">Требовать смены при первом входе.</param>
internal sealed record UserDraft(
    string Email,
    string? DisplayName = null,
    string? Password = null,
    bool MustChangePassword = true);

/// <summary>Что можно изменить у заведённой учётной записи.</summary>
internal sealed record UserPatch(
    string? DisplayName = null,
    bool? MustChangePassword = null);

/// <summary>
/// Жизненный цикл учётной записи.
/// </summary>
/// <remarks>
/// ⚠ <b>Каждое действие пишет в журнал</b>: кто кого завёл, отключил, удалил, кому сбросили
/// пароль. Это ровно тот класс событий, ради которого журнал заводят.
///
/// ⚠ ⚠ <b>Ролей и прав нет: каждая учётная запись — администратор.</b> Поэтому проверок «кто
/// кем вправе распоряжаться» здесь нет вовсе; остаётся одна защита — в системе всегда есть
/// хотя бы одна активная учётная запись (<see cref="EnsureNotLastActiveAsync"/>).
/// </remarks>
internal sealed class UserService(
    SenderDbContext db,
    UserRepository users,
    Invitations invitations,

    // Сессии — соседняя служба ТОГО ЖЕ модуля, а не чужого: отключение человека обязано
    // закрывать его сессии, и делать это надо одной транзакцией с самим отключением.
    ITokenRevoker tokens,
    PasswordPolicy policy,
    IPasswordHasher passwords,
    IAuditWriter audit,

    // ⚠ Письма ставятся В ТЕХ ЖЕ транзакциях, что и сами действия (этап 17.6): письмо
    // «пароль сменён» не должно существовать, если сама смена откатилась.
    MailNotices mail,

    // ⚠ Разрешение способа второго фактора — соседняя служба того же модуля. Проверки
    // «обязателен ли фактор» здесь НЕТ намеренно: отказаться от него нельзя вовсе, и живёт
    // отказ в списке допустимого (SecondFactorMethods.Choosable), а не в проверке после нажатия.
    SecondFactorPolicy methods,

    // ⚠ Счётчик неудач внутри открытой сессии — тот же, что у входа (ревью 2026-09-15, S2):
    // текущий пароль при смене перебирался без счёта.
    LoginFailures failures,
    ICurrentUser actor,
    TimeProvider clock)
{
    /// <summary>Тип ресурса в журнале.</summary>

    /// <summary>
    /// Выполнить изменение одной транзакцией.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Через стратегию выполнения, а не просто <c>BeginTransaction</c>.</b> У хоста
    /// включён повтор при обрыве соединения, и он прямо запрещает транзакции, начатые
    /// вручную: повторять можно только целиком, а не с середины. Обычный
    /// <c>BeginTransactionAsync</c> при этом даёт не предупреждение, а исключение — то есть
    /// пятисотку на каждом заведении пользователя.
    ///
    /// Найдено интеграционным тестом. Тесты модуля этого не показывали: их обвязка
    /// подключала базу без повторов, то есть проверяла не ту конфигурацию, что работает в
    /// бою. Обвязка выровнена вместе с этой правкой.
    ///
    /// Транзакция нужна не сама по себе: без неё блокировка строк при проверке последнего
    /// суперпользователя снимается немедленно, и защита превращается в украшение.
    /// </remarks>
    /// <summary>
    /// Выполнить одной транзакцией, переживая обрыв соединения.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Трекер чистится перед каждой попыткой, и это не перестраховка.</b> Стратегия
    /// выполнения при обрыве зовёт делегат ЗАНОВО, а всё, что первая попытка успела
    /// добавить и изменить, остаётся отслеженным: вторая попытка вставила бы те же строки
    /// второй раз, а изменения посчитала бы уже сделанными. Транзакция при этом честно
    /// откатилась — в базе пусто, в памяти нет. Найдено разбором 2026-08-22.
    ///
    /// Отсюда требование к телу: <b>всё, что оно трогает, оно и загружает</b> — внутри
    /// делегата, а не до него. Сущность, полученная снаружи, после очистки окажется
    /// отсоединённой, и её правки уедут в никуда молча.
    /// </remarks>

    /// <summary>Завести учётную запись.</summary>
    /// <returns>Заведённый человек и ссылка приглашения, если пароль не задавали.</returns>
    /// <remarks>
    /// ⚠ Своей транзакцией, как и все прочие изменения. До 2026-08-22 это был <b>единственный</b>
    /// путь без неё: гашение прежних приглашений (отдельным оператором) успевало состояться, а
    /// сама учётная запись — нет, и обрыв посередине оставлял бы человека без действующей ссылки.
    /// </remarks>
    public Task<(User User, Invitation? Invitation)> CreateAsync(
        UserDraft draft, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(token => CreateCoreAsync(draft, token), cancellationToken);

    private async Task<(User User, Invitation? Invitation)> CreateCoreAsync(
        UserDraft draft, CancellationToken cancellationToken)
    {
        var email = NormalizeForCreation(draft.Email);

        if (draft.Password is { } password)
        {
            await policy.EnsureAsync(password, cancellationToken);
        }

        if (await users.EmailTakenAsync(email, cancellationToken))
        {
            throw new AppException(
                ErrorCodes.UserAlreadyExists, "Такой адрес уже занят", HttpStatusCode.Conflict);
        }

        var now = clock.GetUtcNow();

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            DisplayName = draft.DisplayName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Invitation? invitation = null;

        if (draft.Password is { } given)
        {
            user.PasswordHash = await passwords.HashAsync(given, cancellationToken);
            user.PasswordChangedAt = now;
            user.MustChangePassword = draft.MustChangePassword;

            // Человека допустили без подтверждения ящика — значит он активен с этой
            // секунды. Пропуск отметки не ломает вход, а врёт про него: в админке такая
            // учётная запись показывалась бы «Приглашён», хотя работает.
            user.ActivatedAt = now;
        }
        else
        {
            // Пароль, которого не знает никто. Не пустая строка и не «нет пароля»: значение
            // должно быть заведомо непроверяемым, а не отсутствующим.
            user.PasswordHash = Unusable;

            // Принудительно: пароль человек придумает сам, и требовать сменить только что
            // придуманный — бессмысленно.
            user.MustChangePassword = false;
        }

        users.Add(user);

        if (draft.Password is null)
        {
            invitation = await invitations.IssueAsync(user.Id, Purposes.Activation, cancellationToken: cancellationToken);
        }

        audit.Write(new AuditEntry(
            IdentityActions.UserCreated,
            ActorId: actor.Id,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: new Dictionary<string, object?>
            {
                ["email"] = email,
                ["display_name"] = draft.DisplayName,
                ["by_invitation"] = draft.Password is null,
            }));

        if (invitation is not null)
        {
            audit.Write(new AuditEntry(
                IdentityActions.InvitationSent,
                ActorId: actor.Id,
                ResourceType: IdentityResources.User,
                ResourceId: user.Id,
                Changes: new Dictionary<string, object?> { ["expires_at"] = invitation.ExpiresAt }));
        }

        // ⚠ Постановка письма ДО сохранения и в той же единице работы: строка очереди
        // уезжает в базу вместе с самим человеком. Откат заведения уносит и письмо — иначе
        // приглашение существовало бы для учётной записи, которой нет.
        var приглашениеОтправлено = false;

        if (invitation is not null)
        {
            приглашениеОтправлено = await mail.InvitationAsync(
                user, invitation.Token, invitation.ExpiresAt, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsEmailConflict(error))
        {
            // Гонка: проверка занятости прошла, а между ней и вставкой тот же адрес завёл
            // кто-то другой. Частичный уникальный индекс ловит это в базе — и без перевода
            // наружу уезжала бы пятисотка вместо внятного «адрес занят».
            throw new AppException(
                ErrorCodes.UserAlreadyExists, "Такой адрес уже занят", HttpStatusCode.Conflict);
        }

        // ⚠ ⚠ Токен возвращается ТОЛЬКО если письмо не ушло. Ушло — секрет уехал по своему
        // каналу, и второй раз показывать его в ответе API незачем: он попадёт в лог прокси,
        // в историю вкладки и в отчёт об ошибке. Разбор — решение 0031.
        return (user, приглашениеОтправлено ? null : invitation);
    }

    /// <summary>
    /// Это нарушение уникальности адреса?
    /// </summary>
    /// <remarks>
    /// По имени <b>своего</b> индекса, а не по коду состояния провайдера. Код <c>23505</c>
    /// потребовал бы ссылки на Npgsql, то есть знания модуля о том, какая под ним база; ровно
    /// за это из выдачи пользователей уже убирали <c>ILike</c>. Имя индекса — наша константа,
    /// она объявлена рядом со схемой и меняется только вместе с ней.
    /// </remarks>
    private static bool IsEmailConflict(DbUpdateException error) =>
        error.InnerException?.Message.Contains(IdentityEntities.EmailIndex, StringComparison.Ordinal) == true;

    /// <summary>Изменить профиль и флаги.</summary>
    public async Task<User> UpdateAsync(
        Guid id, UserPatch patch, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(async token => await UpdateCoreAsync(id, patch, token), cancellationToken);
    }

    private async Task<User> UpdateCoreAsync(Guid id, UserPatch patch, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(id, cancellationToken);
        var changes = new Dictionary<string, object?>();

        if (patch.DisplayName is { } name && name != user.DisplayName)
        {
            user.DisplayName = name;
            changes["display_name"] = name;
        }

        if (patch.MustChangePassword is { } must && must != user.MustChangePassword)
        {
            user.MustChangePassword = must;
            changes["must_change_password"] = must;
        }

        if (changes.Count == 0)
        {
            // Пустая правка не пишет в журнал: строка «изменил, но ничего не изменилось» —
            // это шум, а журнал безопасности от шума уже один раз чистили. Транзакция при
            // этом закрывается пустой — записывать было нечего.
            return user;
        }

        user.UpdatedAt = clock.GetUtcNow();

        audit.Write(new AuditEntry(
            IdentityActions.UserUpdated,
            ActorId: actor.Id,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: changes));

        await db.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>
    /// Сменить себе пароль.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Этого не было вовсе до 2026-08-22</b>, и вместе с этим не работал флаг
    /// <c>must_change_password</c>: система велела сменить пароль, а сменить его было нечем.
    /// Человек с временным паролем от администратора ходил по всему API и не мог снять с
    /// себя ограничение, потому что ограничения тоже не было.
    ///
    /// <b>Старый пароль спрашивается обязательно.</b> Угнанная вкладка иначе меняет пароль и
    /// запирает хозяина снаружи.
    ///
    /// <b>Прочие сессии закрываются, текущая остаётся.</b> Смысл смены — выгнать того, кто
    /// мог подсмотреть прежний пароль. Выгонять заодно и того, кто меняет, значит наказывать
    /// за правильное поведение: смена заканчивалась бы экраном входа, и её перестали бы
    /// делать.
    /// </remarks>
    /// <remarks>
    /// ⚠ ⚠ <b>Неверный текущий пароль считается счётчиком входа и упирается в блокировку</b>
    /// (ревью 2026-09-15, S2, password-1). До этого держатель угнанной сессии перебирал пароль
    /// владельца здесь без счёта, без строки журнала и без письма — а угадав, ставил свой, и
    /// <c>RevokeOthersAsync</c> выгонял всех, кроме него. Цена названа у <see cref="LoginFailures"/>.
    ///
    /// ⚠ <b>Проверка и засчёт — ДО транзакции.</b> Отказ, брошенный внутри неё, откатил бы и
    /// счётчик, и строку журнала. Внутри транзакции сверяется, что пароль не сменился между
    /// проверкой и записью: иначе одновременная смена прошла бы по устаревшему «текущему».
    /// </remarks>
    public async Task ChangePasswordAsync(
        string current, string next, CancellationToken cancellationToken = default)
    {
        if (actor.Id is not { } id)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", HttpStatusCode.Unauthorized);
        }

        var user = await RequireAsync(id, cancellationToken);
        var now = clock.GetUtcNow();

        // ⚠ Попытка занимается ДО argon2 одним условным обновлением (ревью 2026-10-20, разбор у
        // LoginFailures). Не заняли — учётная запись заперта: смену она не проходит, и счётчик не
        // двигается — иначе блокировка закрывала бы вход и оставляла открытым подбор через смену
        // пароля.
        if (!await failures.TakeAsync(user.Id, now, cancellationToken))
        {
            throw WrongCurrentPassword();
        }

        // Сначала подтверждаем, кто просит, и только потом рассказываем о требованиях к
        // паролю: иначе форма смены становится справочником политики для угнавшего вкладку.
        if (!await passwords.VerifyAsync(user.PasswordHash, current, cancellationToken))
        {
            // Счёт уже занят — остаётся след в журнале.
            await failures.DenyAsync(user, "change_password_bad_password", cancellationToken);

            throw WrongCurrentPassword();
        }

        // Текущий пароль верен — попытка неудачей не была.
        await failures.ReturnAsync(user.Id, cancellationToken);

        var проверенный = user.PasswordHash;

        await db.InTransactionAsync(
            token => ChangePasswordCoreAsync(id, проверенный, current, next, token), cancellationToken);
    }

    private static AppException WrongCurrentPassword() =>
        new(ErrorCodes.InvalidCredentials, "Текущий пароль неверен", HttpStatusCode.BadRequest);

    private async Task<bool> ChangePasswordCoreAsync(
        Guid id, string проверенный, string current, string next, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(id, cancellationToken);

        // Пароль сменили между проверкой и транзакцией — «текущий» уже не текущий.
        if (!string.Equals(user.PasswordHash, проверенный, StringComparison.Ordinal))
        {
            throw WrongCurrentPassword();
        }

        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            throw new AppException(
                ErrorCodes.PasswordPolicyViolation,
                "Новый пароль совпадает с текущим",
                HttpStatusCode.UnprocessableEntity);
        }

        await policy.EnsureAsync(next, cancellationToken);

        await ApplyNewPasswordAsync(user, next, byReset: false, cancellationToken);

        // Текущая сессия остаётся, если она известна; без неё закрываются все.
        if (actor.SessionId is { } session)
        {
            await tokens.RevokeOthersAsync(
                user.Id, session, RevokeReasons.PasswordChanged, cancellationToken);
        }
        else
        {
            await tokens.RevokeAllAsync(
                user.Id, RevokeReasons.PasswordChanged, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Выдать ссылку сброса пароля.
    /// </summary>
    /// <remarks>
    /// Административное действие: человек забыл пароль, и вернуть ему доступ может другой
    /// администратор. Пока почты нет, ссылка возвращается в ответе, а значит выдавший её
    /// задаёт чужой пароль, — поэтому эндпоинт требует свежего второго фактора.
    ///
    /// Публичное «забыл пароль» — <see cref="RequestPasswordResetAsync"/>: оно шлёт ссылку
    /// только письмом и никогда не возвращает её в ответе.
    /// </remarks>
    public async Task<Invitation> IssuePasswordResetAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(IssueCoreAsync, cancellationToken);

        async Task<Invitation> IssueCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            if (!user.IsActive)
            {
                throw new AppException(
                    ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Conflict);
            }

            var reset = await invitations.IssueAsync(user.Id, Purposes.Reset, cancellationToken: token);

            audit.Write(new AuditEntry(
                IdentityActions.PasswordResetIssued,
                ActorId: actor.Id,
                ResourceType: IdentityResources.User,
                ResourceId: user.Id,
                Changes: new Dictionary<string, object?> { ["expires_at"] = reset.ExpiresAt }));

            var отправлено = await mail.PasswordResetAsync(
                user, reset.Token, reset.ExpiresAt, token);

            await db.SaveChangesAsync(token);

            // ⚠ Ссылка возвращается вызывающему ТОЛЬКО если письмо не ушло: иначе секрет
            // уходит двумя каналами вместо одного (решение 0031).
            return отправлено ? reset with { Token = string.Empty } : reset;
        }
    }

    /// <summary>Что человек выбрал себе и что будет действовать.</summary>
    /// <remarks>
    /// ⚠ Два значения, а не одно: выбранное — то, что он нажал; действующее — то, что
    /// получится с учётом послаблений. Показав одно, экран соврал бы либо про выбор, либо
    /// про то, что произойдёт.
    /// </remarks>
    public async Task<(string? Chosen, string Effective)> SecondFactorAsync(
        CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(actor.Id!.Value, cancellationToken);

        return (user.SecondFactorMethod, await methods.ResolveAsync(user, cancellationToken));
    }

    /// <summary>
    /// Выбрать себе способ второго фактора.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>«Нет фактора» не выбирается вовсе.</b> Фактор обязателен всем, выбирается
    /// только способ; отказ живёт в самом списке допустимого (<c>SecondFactorMethods.Choosable</c>),
    /// а не в проверке после нажатия. <c>none</c> остаётся возможным исходом разрешения, когда нет
    /// ни почты, ни аутентификатора, — но это следствие настроек установки, а не решение человека.
    /// </remarks>
    public async Task ChooseSecondFactorAsync(
        string? method, CancellationToken cancellationToken = default)
    {
        if (!SecondFactorPolicy.Выбираем(method))
        {
            throw new AppException(
                ErrorCodes.Validation,
                "Такого способа второго фактора выбрать нельзя.",
                HttpStatusCode.UnprocessableContent,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allowed"] = SecondFactorMethods.Choosable,
                });
        }

        await db.InTransactionAsync(
            async token =>
            {
                var user = await RequireAsync(actor.Id!.Value, token);

                user.SecondFactorMethod = method;
                user.UpdatedAt = clock.GetUtcNow();

                audit.Write(new AuditEntry(
                    IdentityActions.TwoFactorMethodChanged,
                    ActorId: actor.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id,
                    Changes: new Dictionary<string, object?> { ["method"] = method }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>
    /// Публичная просьба о ссылке на смену пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Ничего не возвращает и ничем не отличает исходы.</b> Адреса нет, учётная
    /// запись отключена, почта не настроена — снаружи всё одинаково. Различие сделало бы форму
    /// определителем существующих учётных записей.
    ///
    /// ⚠ <b>Молчание внутри тоже полное:</b> в лог не уходит ни адрес, ни то, нашёлся ли он.
    /// Строка «просили сброс для такого-то» — сведение о человеке, а лог общий (правило 14).
    ///
    /// ⚠ <b>Строка журнала пишется ТОЛЬКО когда ссылка действительно выдана.</b> Иначе журнал
    /// наполнялся бы перебором чужих адресов, и в нём утонули бы настоящие сбросы.
    /// </remarks>
    public async Task RequestPasswordResetAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var адрес = Normalize(email);

        var найден = await users.FindByEmailAsync(адрес, cancellationToken);

        // ⚠ ⚠ Холостая работа — на КАЖДОМ пути, и на существующем адресе тоже (ревью 2026-10-20).
        // Найдено ревью 23.8: без неё ветка «нет адреса» возвращалась мгновенно, а существующий
        // адрес платил выдачу токена и постановку письма. Первая починка тратила Argon2 только на
        // ветке «нет адреса» — и перевернула разницу: теперь сотни миллисекунд хеша платил
        // незнакомый адрес, а существующий отвечал за десяток миллисекунд транзакции, то есть
        // заметно быстрее. Перечисление адресов усреднением осталось, только с обратным знаком.
        // Одинаковая работа на обоих путях оставляет разницу в одну транзакцию — величину того
        // же порядка, что шум сети. Тот же приём, что на входе (`SignInService` →
        // `WasteVerificationTimeAsync`).
        //
        // ⚠ ⚠ ВНЕ транзакции (проработка API 2026-10-19, решение 0095). Внутри неё каждый
        // запрос держал соединение пула, пока стоял в очереди к Argon2, и поток таких запросов
        // выедал пул — вставало всё API. Как в `ActivateAsync`: хеш считается до транзакции.
        await passwords.WasteVerificationTimeAsync(cancellationToken);

        // ⚠ Отключённому не шлём: ссылка ему всё равно не поможет — сброс пароля отказывает
        // отключённой учётной записи. Ответ снаружи при этом тот же.
        if (найден is not { IsActive: true })
        {
            return;
        }

        try
        {
            await db.InTransactionAsync(
                async token =>
                {
                    // Перечитываем внутри попытки — см. InTransactionAsync. Пропал или отключён
                    // за эти миллисекунды — молчим так же, как о незнакомом адресе.
                    var user = await users.FindByEmailAsync(адрес, token);

                    if (user is null || !user.IsActive)
                    {
                        return;
                    }

                    var reset = await invitations.IssueAsync(
                        user.Id, Purposes.Reset, cancellationToken: token);

                    if (!await mail.PasswordResetAsync(user, reset.Token, reset.ExpiresAt, token))
                    {
                        // Письмо не встало в очередь — выданную ссылку получать некому.
                        // Транзакция откатывается: ссылки, которую никто не увидит, быть не
                        // должно, а показать её здесь нельзя — просящий не вошёл.
                        throw new НеОтправлено();
                    }

                    audit.Write(new AuditEntry(
                        IdentityActions.PasswordResetIssued,
                        ResourceType: IdentityResources.User,
                        ResourceId: user.Id,
                        Changes: new Dictionary<string, object?> { ["by_request"] = true }));

                    await db.SaveChangesAsync(token);
                },
                cancellationToken);
        }
        catch (НеОтправлено)
        {
            // Наружу — тот же 202. Внутри не осталось ничего.
        }
    }

    /// <summary>
    /// Письмо не встало в очередь: откатываем выданную ссылку и молчим наружу.
    /// </summary>
    /// <remarks>
    /// ⚠ Своё исключение, а не <c>AppException</c>: оно ловится здесь же и наружу не выходит.
    /// Общее однажды доехало бы до клиента и различило исходы — то самое, чего эндпоинт обещает
    /// не делать.
    /// </remarks>
    private sealed class НеОтправлено : Exception;

    /// <summary>
    /// Задать пароль по ссылке сброса.
    /// </summary>
    /// <remarks>
    /// Отказ один на все случаи — нет ссылки, просрочена, погашена, учётная запись
    /// отключена: различать их снаружи означало бы отвечать перебирающему ссылки на вопрос
    /// «а есть ли такой человек».
    ///
    /// Здесь закрываются <b>все</b> сессии, включая ту, из которой пришли: входящий по
    /// ссылке сброса по определению не вошёл, а старые сессии — как раз то, что могло
    /// достаться тому, из-за кого сброс и понадобился.
    /// </remarks>
    public async Task<User> ResetPasswordAsync(
        string token, string password, CancellationToken cancellationToken = default)
    {
        var owner = await invitations.PeekAsync(token, Purposes.Reset, cancellationToken);
        var user = owner is { } id ? await users.FindAsync(id, cancellationToken) : null;

        if (user is not { IsActive: true })
        {
            throw await RejectActivationAsync(cancellationToken);
        }

        await policy.EnsureAsync(password, cancellationToken);

        var subject = user.Id;

        return await db.InTransactionAsync(ResetCoreAsync, cancellationToken);

        async Task<User> ResetCoreAsync(CancellationToken inner)
        {
            var consumed = await invitations.ConsumeAsync(token, Purposes.Reset, inner);

            if (consumed != subject)
            {
                throw await RejectActivationAsync(inner);
            }

            // Перечитываем внутри попытки — см. InTransactionAsync.
            var fresh = await RequireAsync(subject, inner);
            if (!fresh.IsActive) throw await RejectActivationAsync(inner);

            await ApplyNewPasswordAsync(fresh, password, byReset: true, inner);
            await tokens.RevokeAllAsync(
                fresh.Id, RevokeReasons.PasswordChanged, inner);


            await db.SaveChangesAsync(inner);

            return fresh;
        }
    }

    /// <summary>Общая часть: новый хеш, отметки, строка журнала.</summary>
    private async Task ApplyNewPasswordAsync(
        User user, string password, bool byReset, CancellationToken cancellationToken, bool fromConsole = false)
    {
        var now = clock.GetUtcNow();

        user.PasswordHash = await passwords.HashAsync(password, cancellationToken);
        user.PasswordChangedAt = now;
        user.UpdatedAt = now;

        // Ограничение снимается сменой пароля — ради этого оно и ставилось.
        user.MustChangePassword = false;

        // Счётчик неудач обнуляется: он про подбор прежнего пароля, которого больше нет.
        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        // ⚠ Выданная расписка второго фактора гаснет вместе с паролем. Расписку выдают ПОСЛЕ
        // проверки пароля и до кода: тот, кто знал прежний пароль, мог держать её на руках и
        // завершить вход уже после смены — то есть смена пароля не закрывала бы дверь, которую
        // сама же и приоткрыла. Сессии здесь закрываются вызывающим, а расписка — не сессия, и
        // её пропустили. Найдено разбором 2026-08-22.
        await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, now, cancellationToken);

        // ⚠ Коды письмом гаснут здесь же: выданный под прежним паролем код не должен пережить
        // его смену. Место выбрано так, чтобы третий путь смены пароля, если он появится,
        // получил это даром: `ApplyNewPasswordAsync` — единственная точка, где пароль меняется.
        await invitations.QuenchAsync(user.Id, Purposes.LoginCode, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.StepUpCode, now, cancellationToken);

        var changes = new Dictionary<string, object?> { ["by_reset"] = byReset };
        if (fromConsole) changes["via_cli"] = true;

        audit.Write(new AuditEntry(
            IdentityActions.PasswordChanged,
            ActorId: fromConsole ? null : user.Id,
            ActorType: fromConsole ? AuditActorType.System : null,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: changes));
    }

    /// <summary>
    /// Изменить своё имя.
    /// </summary>
    /// <remarks>
    /// ⚠ Своё — и только имя. Адрес не меняется вовсе: он логин, на него ссылается журнал, и
    /// смена адреса это отдельное решение владельца, а не строчка в профиле. Флаги не
    /// меняются тем более: право распоряжаться собственной активностью и собственным
    /// суперпользовательством означало бы, что прав нет вообще.
    ///
    /// До 2026-08-22 своё имя человек изменить не мог никак: профиль только читался.
    /// </remarks>
    public Task<User> UpdateOwnProfileAsync(
        string? displayName, CancellationToken cancellationToken = default)
    {
        if (actor.Id is not { } id)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", HttpStatusCode.Unauthorized);
        }

        return UpdateAsync(id, new UserPatch(DisplayName: displayName), cancellationToken);
    }

    /// <summary>
    /// Снять блокировку входа.
    /// </summary>
    /// <remarks>
    /// Блокировка снимается сама по истечении срока, но ждать пятнадцать минут человеку,
    /// который трижды промахнулся мимо раскладки, незачем — а до 2026-08-22 снять её было
    /// нечем вовсе.
    ///
    /// </remarks>
    public async Task<User> UnlockAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(UnlockCoreAsync, cancellationToken);

        async Task<User> UnlockCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            if (user.LockedUntil is null && user.FailedLoginCount == 0)
            {
                return user;
            }

            user.LockedUntil = null;
            user.FailedLoginCount = 0;
            user.UpdatedAt = clock.GetUtcNow();

            audit.Write(new AuditEntry(
                IdentityActions.UserUnlocked,
                ActorId: actor.Id,
                ResourceType: IdentityResources.User,
                ResourceId: user.Id));

            await db.SaveChangesAsync(token);

            return user;
        }
    }

    /// <summary>
    /// Закрыть все сессии человека, не отключая его самого.
    /// </summary>
    /// <remarks>
    /// Нужно тому, у кого угнали ноутбук: доступ остаётся, украденные токены — нет. До
    /// 2026-08-22 единственным способом выгнать человека было отключить его целиком.
    /// </remarks>
    public Task<int> RevokeSessionsAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                var user = await RequireAsync(id, token);

                // «У человека угнали ноутбук» — это про всё сразу: сессии, расписки и коды.
                await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, clock.GetUtcNow(), token);
                await invitations.QuenchAsync(user.Id, Purposes.LoginCode, clock.GetUtcNow(), token);
                await invitations.QuenchAsync(user.Id, Purposes.StepUpCode, clock.GetUtcNow(), token);
                return await tokens.RevokeAllAsync(
                    user.Id, RevokeReasons.Revoked, token);
            },
            cancellationToken);

    /// <summary>
    /// Включить обратно.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Отдельным действием, а не правкой профиля</b>: включение и отключение — одно
    /// действие с двух сторон, у обоих свой журнал, и оба требуют свежего второго фактора.
    ///
    /// Сессии обратно <b>не</b> воскресают: отозванная сессия отозвана навсегда, и человек
    /// входит заново. Иначе возвращение доступа тихо возвращало бы и украденный токен.
    /// </remarks>
    public async Task<User> EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(EnableCoreAsync, cancellationToken);

        async Task<User> EnableCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            if (user.IsActive)
            {
                return user;
            }

            user.IsActive = true;
            user.UpdatedAt = clock.GetUtcNow();

            audit.Write(new AuditEntry(
                IdentityActions.UserEnabled, ActorId: actor.Id, ResourceType: IdentityResources.User, ResourceId: user.Id));

            await db.SaveChangesAsync(token);

            return user;
        }
    }

    /// <summary>
    /// Отключить учётную запись.
    /// </summary>
    /// <remarks>
    /// Идемпотентно: отключение уже отключённого — не ошибка, а подтверждение состояния.
    ///
    /// <b>Сессии обрываются здесь же.</b> Отключение, оставляющее человека работать до конца
    /// срока токена, — это не отключение, а обещание отключить через пятнадцать минут.
    ///
    /// </remarks>
    public async Task<User> DisableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.InTransactionAsync(DisableCoreAsync, cancellationToken);

        async Task<User> DisableCoreAsync(CancellationToken token)
        {
            var user = await RequireAsync(id, token);

            if (!user.IsActive)
            {
                return user;
            }

            await EnsureNotLastActiveAsync(user, token);

            user.IsActive = false;
            user.UpdatedAt = clock.GetUtcNow();

            await tokens.RevokeAllAsync(user.Id, RevokeReasons.UserInactive, token);
            await db.Set<IdentityToken>().Where(row => row.UserId == user.Id && row.UsedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.UsedAt, user.UpdatedAt), token);

            // ⚠ Сессии закрываются той же транзакцией, а непогашенные ссылки и коды гаснут: доступ
            // обрывается немедленно, а не через пятнадцать минут срока токена доступа.
            // Распознаватель личности к тому же читает активность из базы на КАЖДОМ запросе
            // (SessionIdentityResolver) — две защиты, и ни одна не полагается на другую.

            audit.Write(new AuditEntry(
                IdentityActions.UserDisabled, ActorId: actor.Id, ResourceType: IdentityResources.User, ResourceId: user.Id));

            // ⚠ В ТОЙ ЖЕ транзакции, что и само отключение. Письмо «доступ приостановлен» —
            // единственный способ узнать причину: форма входа отвечает одинаково на все
            // случаи, чтобы не подсказывать посторонним, какие адреса существуют.
            await mail.AccountSuspendedAsync(user, token);

            await db.SaveChangesAsync(token);

            return user;
        }
    }

    /// <summary>Удалить учётную запись мягко: адрес освобождается, журнал о ней остаётся.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.InTransactionAsync(
            async token =>
            {
                var user = await RequireAsync(id, token);

                await EnsureNotLastActiveAsync(user, token);

                var now = clock.GetUtcNow();

                user.DeletedAt = now;
                user.IsActive = false;
                user.UpdatedAt = now;

                // ⚠ Сессии УДАЛЯЕМ, а не гасим: держать строки того, кого в системе больше нет,
                // незачем, а след действия живёт в журнале.
                await tokens.DeleteAllAsync(user.Id, token);

                var swept = await SweepAsync(user.Id, now, token);

                audit.Write(new AuditEntry(
                    IdentityActions.UserDeleted,
                    ActorId: actor.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id,
                    Changes: new Dictionary<string, object?>
                    {
                        ["email"] = user.Email,
                        ["two_factor"] = swept.TwoFactor,
                        ["links"] = swept.Links,
                    }));

                await db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);
    }

    /// <summary>Выслать приглашение заново.</summary>
    public Task<Invitation> ResendInvitationAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(token => ResendCoreAsync(id, token), cancellationToken);

    /// <remarks>
    /// ⚠ Своей транзакцией. Выпуск новой ссылки сначала гасит прежние — оператором, который
    /// состоится немедленно, — а сама новая ссылка и строка журнала уезжают на сохранении.
    /// Сбой между ними оставлял бы приглашённого без единой действующей ссылки, причём в
    /// журнале не было бы даже следа того, что приглашение пытались выслать. Ровно тот дефект,
    /// который уже нашли и починили для заведения; путь повторной высылки тогда пропустили.
    /// Найдено разбором 2026-08-22.
    /// </remarks>
    private async Task<Invitation> ResendCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await RequireAsync(id, cancellationToken);

        if (user.ActivatedAt is not null)
        {
            throw new AppException(
                ErrorCodes.UserAlreadyActivated, "Учётная запись уже активирована", HttpStatusCode.Conflict);
        }

        if (!user.IsActive)
        {
            throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Conflict);
        }

        var invitation = await invitations.IssueAsync(user.Id, Purposes.Activation, cancellationToken: cancellationToken);

        audit.Write(new AuditEntry(
            IdentityActions.InvitationSent,
            ActorId: actor.Id,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: new Dictionary<string, object?>
            {
                ["expires_at"] = invitation.ExpiresAt,
                ["resent"] = true,
            }));

        var sent = await mail.InvitationAsync(user, invitation.Token, invitation.ExpiresAt, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return sent ? invitation with { Token = string.Empty } : invitation;
    }

    // --- активация ---------------------------------------------------------------------

    /// <summary>Проверить действующую ссылку сброса до показа формы; ссылку не погашает.</summary>
    public async Task<bool> CanResetPasswordAsync(string token, CancellationToken cancellationToken = default)
    {
        var owner = await invitations.PeekAsync(token, Purposes.Reset, cancellationToken);
        return owner is { } id && await users.FindAsync(id, cancellationToken) is { IsActive: true, DeletedAt: null };
    }

    /// <summary>Кого приглашает эта ссылка. Ссылку не гасит.</summary>
    public async Task<User?> PeekActivationAsync(string token, CancellationToken cancellationToken = default)
    {
        var owner = await invitations.PeekAsync(token, Purposes.Activation, cancellationToken);

        if (owner is not { } id)
        {
            return null;
        }

        var user = await users.FindAsync(id, cancellationToken);

        return user is { IsActive: true, ActivatedAt: null } ? user : null;
    }

    /// <summary>
    /// Активировать учётную запись по ссылке.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Порядок шагов — требование безопасности, а не оформление.</b> Всё, что может
    /// отказать по вине человека — непригодный пароль, — проверяется <b>до</b> гашения
    /// ссылки: иначе одна опечатка сжигала бы приглашение, и человеку пришлось бы просить
    /// новое у администратора.
    ///
    /// А вот сама ссылка гасится <b>до</b> записи пароля и одной с ней транзакцией: два
    /// одновременных запроса иначе задали бы два разных пароля, и второй затёр бы первый
    /// молча.
    /// </remarks>
    public async Task<User> ActivateAsync(
        string token, string password, CancellationToken cancellationToken = default)
    {
        // Отказ один на все случаи: нет ссылки, просрочена, уже использована, учётная
        // запись отключена. Различать их снаружи означало бы отвечать на вопрос «а есть ли
        // такой человек» тому, кто просто перебирает ссылки.
        var user = await PeekActivationAsync(token, cancellationToken)
            ?? throw await RejectActivationAsync(cancellationToken);

        await policy.EnsureAsync(password, cancellationToken);

        var hash = await passwords.HashAsync(password, cancellationToken);

        return await db.InTransactionAsync(ActivateCoreAsync, cancellationToken);

        async Task<User> ActivateCoreAsync(CancellationToken inner)
        {
            var owner = await invitations.ConsumeAsync(token, Purposes.Activation, inner);

            if (owner != user.Id)
            {
                // Исключение само откатит транзакцию: до фиксации дело не дойдёт.
                throw await RejectActivationAsync(inner);
            }

            var now = clock.GetUtcNow();

            // ⚠ Перечитываем внутри попытки. Проверка ссылки шла на экземпляре, загруженном
            // ДО транзакции, а трекер перед каждой попыткой очищается — отсоединённая
            // сущность приняла бы все правки ниже и потеряла их молча. Поймано тестом
            // «активированного повторно не приглашают»: активация проходила, отметки не
            // появлялось, и повторное приглашение считало учётную запись неактивированной.
            var fresh = await RequireAsync(user.Id, inner);
            if (!fresh.IsActive || fresh.ActivatedAt is not null) throw await RejectActivationAsync(inner);

            fresh.PasswordHash = hash;
            fresh.PasswordChangedAt = now;
            fresh.ActivatedAt = now;
            fresh.MustChangePassword = false;
            fresh.FailedLoginCount = 0;
            fresh.LockedUntil = null;
            fresh.UpdatedAt = now;

            // От его имени, хотя он ещё не вошёл: действие совершил он, а не система.
            audit.Write(new AuditEntry(
                IdentityActions.UserActivated,
                ActorId: fresh.Id,
                ResourceType: IdentityResources.User,
                ResourceId: fresh.Id));

            await db.SaveChangesAsync(inner);

            return fresh;
        }
    }

    /// <summary>
    /// Завести первого администратора командой <c>create-superadmin</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Отдельный путь, а не обычное заведение.</b> Обычное требует вошедшего
    /// администратора; здесь его нет ни у кого, потому что учётных записей ещё нет вовсе.
    ///
    /// Пропуском служит доступ к машине: команда выполняется внутри контейнера, а у того,
    /// кто туда попал, и так есть база целиком.
    ///
    /// Событие своё (<c>user.bootstrap</c>), актор — система. Найти этот момент в журнале надо
    /// одним фильтром, а не выуживая его из общих заведений.
    ///
    /// ⚠ Второй фактор у заведённого не подключён: первый вход по паролю выдаёт сессию, где
    /// открыта только настройка приложения-аутентификатора (<c>SignInService</c>).
    /// </remarks>
    public async Task<User> BootstrapSuperuserAsync(
        string email, string? displayName, string password, CancellationToken cancellationToken = default)
    {
        var address = NormalizeForCreation(email);

        // Та же политика, что для всех остальных: первая учётная запись не должна оказаться
        // единственной, заведённой мимо требований.
        await policy.EnsureAsync(password, cancellationToken);

        if (await users.EmailTakenAsync(address, cancellationToken))
        {
            throw new AppException(
                ErrorCodes.UserAlreadyExists, $"Пользователь {address} уже существует", HttpStatusCode.Conflict);
        }

        var now = clock.GetUtcNow();

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = address,
            DisplayName = displayName,
            PasswordHash = await passwords.HashAsync(password, cancellationToken),
            PasswordChangedAt = now,
            IsActive = true,
            MustChangePassword = false,
            ActivatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        users.Add(user);

        audit.Write(new AuditEntry(
            IdentityActions.SuperuserBootstrapped,
            ActorId: user.Id,
            ActorType: AuditActorType.System,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: new Dictionary<string, object?> { ["email"] = address }));

        await db.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>Сменить пароль существующей учётной записи из консоли установки.</summary>
    /// <remarks>
    /// Путь для того, кто забыл пароль, а почты нет или она сломана. Пропуском служит доступ к
    /// машине, как у <see cref="BootstrapSuperuserAsync"/>. Все сессии закрываются.
    /// </remarks>
    public async Task<User> SetSuperuserPasswordAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        (await SetConsolePasswordAsync(email, password, cancellationToken)).User;

    /// <summary>
    /// То же — с числом закрытых сессий: его называет итог консольной <c>reset-access</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Своей транзакцией — или транзакцией вызывающего, если она открыта (<c>InTransactionAsync</c>
    /// её уважает): <see cref="AccessRecovery"/> зовёт этот путь и снятие фактора одной транзакцией.
    /// </remarks>
    internal Task<(User User, int Sessions)> SetConsolePasswordAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async token =>
        {
            var user = await RequireSuperuserForRecoveryAsync(email, token);
            await policy.EnsureAsync(password, token);
            await ApplyNewPasswordAsync(user, password, byReset: true, token, fromConsole: true);
            var now = clock.GetUtcNow();
            await invitations.QuenchAsync(user.Id, Purposes.Reset, now, token);
            await invitations.QuenchAsync(user.Id, Purposes.Activation, now, token);
            var sessions = await tokens.RevokeAllAsync(user.Id, RevokeReasons.PasswordChanged, token);
            await db.SaveChangesAsync(token);
            return (user, sessions);
        }, cancellationToken);

    /// <summary>Найти цель консольного восстановления — без включения отключённой учётной записи.</summary>
    internal async Task<User> RequireSuperuserForRecoveryAsync(string email, CancellationToken cancellationToken) =>
        await users.FindByEmailAsync(NormalizeForCreation(email), cancellationToken)
            ?? throw AppException.NotFound("Учётная запись не найдена");

    // --- инварианты --------------------------------------------------------------------

    /// <summary>Заведомо непроверяемый хеш: «пароль есть, но его не знает никто».</summary>
    /// <remarks>
    /// Не <c>NULL</c> и не пустая строка. <c>NULL</c> однажды породил бы ветку «проверять
    /// нечего, пускаем», а пустая строка — попытку сравнить с пустым паролем.
    /// </remarks>
    private const string Unusable = "!";

    private async Task<User> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await users.FindAsync(id, cancellationToken)
        ?? throw AppException.NotFound("Пользователь не найден");

    /// <summary>
    /// В системе обязана оставаться хотя бы одна активная учётная запись.
    /// </summary>
    /// <remarks>
    /// ⚠ Проверка идёт <b>в той же транзакции</b>, что и изменение, и с блокировкой строк —
    /// разбор в <see cref="UserRepository.LockActiveUsersAsync"/>. Останься система без
    /// единого администратора, вернуть его будет нечем, кроме консольной команды на машине.
    ///
    /// ⚠ <b>Явная транзакция у вызывающего обязательна.</b> Первая редакция её не открывала:
    /// <c>SaveChangesAsync</c> заводит свою транзакцию только на время сохранения, а
    /// <c>SELECT … FOR UPDATE</c> выполнялся до неё, в режиме автофиксации, — то есть
    /// блокировка снималась немедленно, и обе параллельные попытки проходили. Найдено
    /// тестом на настоящей гонке из восьми потоков, а не рассуждением.
    ///
    /// Отключить и удалить <b>себя</b> при этом можно, если ты не последний: администратор,
    /// уходящий из компании, закрывает свою учётную запись сам, и это правильный порядок.
    /// </remarks>
    private async Task EnsureNotLastActiveAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsActive)
        {
            return;
        }

        var active = await users.LockActiveUsersAsync(cancellationToken);

        // ⚠ Считаются только те, кто может войти (UserRepository.LockActiveUsersAsync).
        if (active.Count == 1 && active.Contains(user.Id))
        {
            throw new AppException(
                ErrorCodes.LastSuperadmin,
                "Нельзя отключить или удалить последнюю активную учётную запись",
                HttpStatusCode.Conflict);
        }
    }

    /// <summary>
    /// Отказ в активации — одинаковый снаружи и записанный внутри.
    /// </summary>
    /// <remarks>
    /// Строка в журнал пишется <b>своей транзакцией</b> и до исключения: обработчик ошибки
    /// сейчас откатит запрос, и запись уехала бы вместе с ним. Это единственный след
    /// перебора ссылок — снаружи все отказы выглядят одинаково намеренно, а внутри их надо
    /// уметь сосчитать.
    /// </remarks>
    private async Task<AppException> RejectActivationAsync(CancellationToken cancellationToken)
    {
        audit.Write(new AuditEntry(
            IdentityActions.ActivationRejected,
            ActorType: AuditActorType.System,
            Result: AuditResult.Deny,
            ResourceType: IdentityResources.User,
            Reason: "ссылка недействительна, просрочена или уже использована"));

        await db.SaveChangesAsync(cancellationToken);

        return new AppException(
            ErrorCodes.ActivationTokenInvalid,
            "Ссылка недействительна или уже использована. Попросите администратора прислать новую.",
            HttpStatusCode.BadRequest);
    }

    /// <summary>Что унесли за удалённым человеком.</summary>
    private readonly record struct Swept(int TwoFactor, int Links);

    /// <summary>
    /// Унести за удалённым человеком всё, что продолжало бы что-то значить.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Удаление у нас мягкое, и поэтому каскады базы не срабатывают никогда.</b> Строка
    /// в <c>users</c> остаётся — значит остаются и запечатанный секрет второго фактора вместе
    /// со своим ключом данных, и непогашенные ссылки. Это не мусор, а <b>хранение чужого
    /// секрета после увольнения</b>: TOTP-секрет уволенного лежал бы в базе бессрочно.
    ///
    /// ⚠ Ссылки <b>гасятся</b>, а не удаляются: погашенная ссылка — это след того, что её
    /// выпускали, и уборщик унесёт её сам, когда выйдет срок хранения.
    /// </remarks>
    private async Task<Swept> SweepAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var factor = await db.Set<UserTwoFactor>()
            .Where(row => row.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        var links = await db.Set<IdentityToken>()
            .Where(row => row.UserId == userId && row.UsedAt == null && row.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.UsedAt, now), cancellationToken);

        return new Swept(factor, links);
    }

    /// <summary>
    /// Привести адрес к виду, в котором он хранится.
    /// </summary>
    /// <remarks>
    /// Обрезаются только края. Регистр НЕ приводится: колонка <c>citext</c>, сравнение и так
    /// нечувствительно к нему, а человек видит свой адрес таким, каким его написал.
    /// </remarks>
    private static string Normalize(string email) => email.Trim();

    /// <summary>
    /// Привести адрес и убедиться, что это адрес. Для путей ЗАВЕДЕНИЯ.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заведено вскрытием 43 по живой находке владельца:</b> команда
    /// <c>create-superadmin</c> приняла строку <c>windowsdanigame.com</c> — без <c>@</c> — и
    /// отчиталась успехом. Получилась учётная запись, в которую <b>нельзя войти через веб</b>
    /// (у поля формы стоит <c>type="email"</c>, и строку без <c>@</c> она не отправит), но
    /// можно через API; и ни приглашение, ни сброс, ни код второго фактора до неё не доедут
    /// никогда.
    ///
    /// ⚠ <b>Причина, по которой этого не видели: проверку держал БРАУЗЕР.</b> На сервере
    /// <c>UserIn</c> объявлял только длину, а команда идёт мимо DTO вовсе. Ровно тот случай,
    /// о котором свод говорит «UI-проверка — это UX, а не защита».
    ///
    /// ⚠ <b>Средство то же самое, что на DTO</b> — <see cref="EmailAddressAttribute"/>. Это
    /// не лень: своя регулярка здесь означала бы, что API и команда расходятся в том, что
    /// считают адресом, и расхождение всплыло бы ровно так же — на живом человеке.
    ///
    /// ⚠ <b>Путь СБРОСА пароля этим не затронут намеренно.</b>
    /// <see cref="RequestPasswordResetAsync"/> отвечает одинаково на существующий и
    /// несуществующий адрес — это защита от определения учётных записей формой, и ветка
    /// «здесь отвечаем иначе» была бы первым шагом к её отмене.
    /// </remarks>
    private static string NormalizeForCreation(string email)
    {
        var address = Normalize(email);

        return ЭтоАдрес.IsValid(address)
            ? address
            : throw new AppException(
                ErrorCodes.Validation,
                "Адрес учётной записи не похож на адрес почты",
                System.Net.HttpStatusCode.BadRequest);
    }

    /// <summary>Один экземпляр проверки на процесс: атрибут не хранит состояния.</summary>
    private static readonly EmailAddressAttribute ЭтоАдрес = new();
}
