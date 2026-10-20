using EmailSslSender.Modules.Identity.Settings;
using System.Net;
using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Naming;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>Чем кончился вход: пара токенов.</summary>
/// <param name="Pair">Выданная пара токенов.</param>
/// <remarks>
/// ⚠ Не <c>SignInResult</c>: так называется тип ASP.NET Core (<c>Microsoft.AspNetCore.Mvc</c>), и
/// в контроллере имя стало бы двусмысленным.
///
/// ⚠ «Доверенных машин» (вход без кода с запомненного браузера) здесь нет, хотя в EAMS они
/// есть: код спрашивается при каждом входе. Вернуть их — перенести <c>TrustedClients</c>.
/// </remarks>
internal sealed record SignInOutcome(TokenPair Pair);

/// <summary>
/// Вход: пароль, блокировка после неудач, второй фактор.
/// </summary>
/// <remarks>
/// <b>Прежде звалась <c>SessionService</c> и делала вдвое больше.</b> Всё, что про сам
/// предъявляемый секрет — выдача пары, обмен, проверка, отзыв, список «где я вошёл», —
/// уехало в модуль токенов (решение 0017, этап 10.5.4). Здесь осталось то, что про
/// <b>учётную запись</b>: пароль, счётчик неудач, блокировка, второй фактор.
///
/// ⚠ <b>Разрыв проходит по вопросу «кто решает, что пускать».</b> Решаем мы — пароль и
/// фактор наши. Выдаёт токены сосед: <see cref="ITokenIssuer"/>. Обратный раздел означал бы,
/// что модуль токенов читает хеши паролей и коды восстановления.
///
/// ⚠ <b>Порядок шагов входа — требование безопасности, а не оформление.</b> Каждая
/// перестановка даёт оракул: становится заметно, существует ли адрес, заблокирована ли
/// учётная запись, отключена ли она.
/// </remarks>
internal sealed class SignInService(
    Invitations invitations,
    TwoFactorService factors,

    // ⚠ Способ фактора и код письмом (этап 17.7). Оба — соседние службы ТОГО ЖЕ модуля:
    // решение о факторе принимается внутри входа, а не приезжает снаружи.
    SecondFactorPolicy methods,
    LoginCodes codes,
    SenderDbContext db,
    UserRepository users,
    IPasswordHasher passwords,
    ITokenIssuer tokens,
    ISettingsReader settings,
    IAuditWriter audit,
    SignInAttempts attempts,

    // ⚠ Попытка учётной записи занимается ДО проверки секрета (ревью 2026-10-20) — разбор у
    // LoginFailures. Попытка расписки — тоже до проверки кода, своим счётом (ChallengeAttempts).
    LoginFailures failures,
    ChallengeAttempts challengeAttempts,
    TimeProvider clock)
{
    /// <summary>
    /// Войти по паролю.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Порядок шагов — требование безопасности, а не оформление.</b> Каждая
    /// перестановка даёт оракул: становится заметно, существует ли адрес, заблокирована ли
    /// учётная запись, отключена ли она.
    ///
    /// Порядок такой: найти → <b>занять попытку</b> (она же проверка блокировки) →
    /// <b>проверить пароль</b> → проверить активность. Активность после пароля нарочно: до него
    /// отключённая учётная запись отвечала бы заметно быстрее живой с неверным паролем.
    ///
    /// А вот блокировка проверяется <b>до</b> пароля, и это осознанная уступка: считать
    /// argon2 для заблокированной учётной записи значит дать способ нагружать сервер
    /// перебором, который всё равно ничем не кончится. Разницу во времени здесь мы
    /// выравниваем холостой проверкой.
    ///
    /// ⚠ ⚠ <b>Блокировка и счёт — одним условным обновлением ДО пароля</b> (ревью 2026-10-20,
    /// <see cref="LoginFailures.TakeAsync"/>). Прежде блокировка читалась из загруженной строки, а
    /// неудача засчитывалась после argon2 отдельным запросом: сотня одновременных попыток
    /// проходила проверку блокировки вся и получала сотню догадок при пороге в десять.
    /// </remarks>
    public async Task<SignInOutcome> SignInAsync(
        string email,
        string password,
        RequestOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(origin);

        // ⚠ Предел на пару «адрес + почта» — ДО поиска и до Argon2 (решение 0096): отказ одинаков
        // для любого адреса, и отбитая попытка не стоит хеша.
        attempts.Require(origin.Address, email);

        var now = clock.GetUtcNow();
        var user = await users.FindByEmailAsync(email.Trim(), cancellationToken);

        if (user is null)
        {
            // ⚠ Тратим столько же, сколько потратила бы настоящая проверка. Без этого ответ
            // на несуществующий адрес возвращается на десятки миллисекунд быстрее, и
            // правило «вход не различает эти два случая» держится на тексте ошибки, а не на
            // поведении.
            await passwords.WasteVerificationTimeAsync(cancellationToken);
            await RecordFailureAsync(null, email, "unknown_email", cancellationToken);

            throw Invalid();
        }

        // ⚠ Попытка занимается и засчитывается неудачей ДО argon2; верный пароль её вернёт.
        // Не занялась — учётная запись заперта, и пароль не проверяется вовсе.
        if (!await failures.TakeAsync(user.Id, now, cancellationToken))
        {
            await passwords.WasteVerificationTimeAsync(cancellationToken);
            await RecordFailureAsync(user.Id, email, "locked", cancellationToken);

            throw Invalid();
        }

        if (!await passwords.VerifyAsync(user.PasswordHash, password, cancellationToken))
        {
            // Счёт уже занят — остаётся след в журнале.
            await RecordFailureAsync(user.Id, email, "bad_password", cancellationToken);

            throw Invalid();
        }

        // ⚠ Пароль верен — попытка неудачей не была, и возвращается ДО всего прочего: иначе
        // попытка, доведшая счёт до порога, заперла бы и того, кто ввёл пароль правильно
        // (`EnsureCurrent` ниже смотрит на блокировку), а каждый шаг входа со вторым фактором
        // копил бы счёт. Обнулит счёт только завершённый вход.
        await failures.ReturnAsync(user.Id, cancellationToken);

        if (!user.IsActive)
        {
            await RecordFailureAsync(user.Id, email, "inactive", cancellationToken);

            throw Invalid();
        }

        // ⚠ Второй фактор — ПОСЛЕ пароля и активности, но ДО выдачи токенов. Порядок не
        // переставляется: спроси мы код раньше пароля, форма входа отвечала бы «у этого
        // человека включён фактор» всякому, кто назвал чужой адрес.
        //
        // Здесь же выполняется условие завершения этапа: обойти фактор нельзя ни одним
        // путём, потому что путь выдачи токенов ровно один — этот.
        // ⚠ ⚠ Способ решается ПОЛИТИКОЙ (`SecondFactorPolicy`): подтверждённый аутентификатор —
        // только он; иначе письмо, если почта настроена; иначе фактора нет.
        var способ = await methods.ResolveAsync(user, cancellationToken);

        // ⚠ ⚠ ⚠ Администратор, ни разу не подтверждавший фактор (только что заведён командой,
        // активировал приглашение, фактор сняли), получает СЕССИЮ НАСТРОЙКИ — с ограничением
        // «сначала подключите приложение-аутентификатор» (`SessionIdentityResolver` — сессия без
        // отметки фактора). Но получает её по-разному:
        //
        // - почта настроена — ТОЛЬКО ПОСЛЕ КОДА ИЗ ПИСЬМА (ревью 2026-10-20). Прежде способ
        //   `email` у такой учётной записи молча подменялся отсутствием фактора, и сессия
        //   настройки выдавалась по одному паролю — а в ней чужой, узнавший пароль, подключал
        //   СВОЙ аутентификатор и становился полноправным администратором. Код письмом завершает
        //   вход сессией настройки, а не рабочей (`CompleteTwoFactorAsync`);
        // - почты нет (свежая установка) — по паролю, как прежде: настроить почту можно только
        //   войдя, и требовать письмо, которое некуда послать, — запертая дверь без ключа.
        //   Принятый риск второго послабления `SecondFactorPolicy`.
        //
        // В EAMS так устроен вход суперпользователя (решение 0082); здесь каждая учётная запись —
        // администратор.
        if (способ == SecondFactorMethods.None && user.TwoFactorConfiguredAt is not null)
        {
            throw new AppException(ErrorCodes.TwoFactorUnavailable,
                "Второй фактор сейчас недоступен. Обратитесь к администратору.", HttpStatusCode.ServiceUnavailable);
        }

        if (способ != SecondFactorMethods.None)
        {
            var minutes = Math.Max(
                1, await settings.GetAsync(IdentitySettings.TwoFactorChallengeMinutes, cancellationToken));

            var challenge = await db.InTransactionAsync(
                async (token) =>
                {
                    var fresh = await users.FindAsync(user.Id, token) ?? throw Invalid();
                    EnsureCurrent(fresh, user.PasswordHash, now);
                    if (passwords.NeedsRehash(fresh.PasswordHash)) fresh.PasswordHash = await passwords.HashAsync(password, token);
                    var issued = await invitations.IssueAsync(
                        user.Id, Purposes.TwoFactor, TimeSpan.FromMinutes(minutes),
                        cancellationToken: token, binding: Invitations.CodeBinding(fresh.PasswordHash));

                    // ⚠ ⚠ Код письмом заводится и ставится в очередь В ТОЙ ЖЕ транзакции,
                    // что и расписка. Иначе расписка выдана, а кода нет — человек смотрит на
                    // поле ввода, которого нечем заполнить.
                    //
                    // ⚠ Неудача отправки НЕ меняет ответ: сказать «слишком часто» значило бы
                    // подтвердить существование учётной записи тому, кто это и выясняет.
                    if (способ == SecondFactorMethods.Email)
                    {
                        EnsureMail(await codes.IssueAsync(user, token, Invitations.CodeBinding(issued.Token)));
                    }

                    await db.SaveChangesAsync(token);

                    return issued;
                },
                cancellationToken);

            throw new AppException(
                ErrorCodes.TwoFactorRequired,
                "Введите код второго фактора",
                HttpStatusCode.Unauthorized,
                new Dictionary<string, object?>
                {
                    ["challenge"] = challenge.Token,
                    ["expires_at"] = challenge.ExpiresAt,

                    // ⚠ Способ уезжает клиенту: экран должен сказать, откуда брать код —
                    // из приложения или из почты. Молчание здесь означает форму, которая
                    // просит «код» и не говорит какой.
                    ["method"] = способ,

                    // ⚠ ⚠ Чем вход будет принят — ровно это, и ничего сверх. У владельца
                    // аутентификатора письма здесь нет (ревью 2026-10-20): запасной путь «код на
                    // почту» делал ящик единственным ключом от учётной записи. Потерявшему
                    // телефон фактор снимает другой администратор или консоль. Список отдаётся
                    // ПОСЛЕ пароля — до него он был бы сведением о чужой учётной записи.
                    ["available"] = await methods.AvailableAsync(user, cancellationToken),
                });
        }

        return await db.InTransactionAsync(
            async (token) =>
            {
                // ⚠ Перечитываем внутри попытки. Проверки выше делались на экземпляре,
                // загруженном ДО транзакции, а трекер перед каждой попыткой очищается —
                // отсоединённая сущность приняла бы правки и потеряла их молча. Разбор — в
                // InTransactionAsync.
                var fresh = await users.FindAsync(user.Id, token) ?? throw Invalid();
                EnsureCurrent(fresh, user.PasswordHash, now);

                // Успех обнуляет счётчик неудач: OWASP и NIST сходятся в том, что
                // предыдущие попытки к вошедшему больше не относятся.
                fresh.FailedLoginCount = 0;
                fresh.LockedUntil = null;
                fresh.UpdatedAt = now;

                // ⚠ Пересчёт хеша, если параметры argon2 усилили. Другого момента, когда
                // открытый пароль есть на руках, не будет. Отметка «когда сменили пароль»
                // при этом НЕ двигается: пересчёт — не смена.
                if (passwords.NeedsRehash(fresh.PasswordHash))
                {
                    fresh.PasswordHash = await passwords.HashAsync(password, token);
                }

                // ⚠ Выдаёт СОСЕД, и внутри нашей транзакции. Иначе вход состоялся бы отдельно от
                // обнуления счётчика неудач и от строки журнала: при сбое между ними
                // человек получил бы токены, а система осталась бы при мнении, что он
                // не входил.
                //
                // ⚠ Отметки фактора у этой сессии НЕТ (`StepUpAt` пуст): сюда доходит только
                // сессия настройки, и распознаватель личности ограничит её до подключения
                // аутентификатора.
                var pair = await tokens.IssueAsync(
                    new TokenIssueRequest(fresh.Id, TokenKinds.Session, origin.UserAgent, origin.Address), token);

                audit.Write(new AuditEntry(
                    IdentityActions.LoggedIn,
                    ActorId: fresh.Id,
                    ResourceType: IdentityResources.Session,
                    ResourceId: pair.TokenId,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["second_factor"] = "enrollment",
                    }));

                await db.SaveChangesAsync(token);

                return new SignInOutcome(pair);
            },
            cancellationToken);
    }

    /// <summary>
    /// Завершить вход: расписка плюс код.
    /// </summary>
    /// <remarks>
    /// <b>Расписка вместо повторного пароля.</b> Без неё клиенту пришлось бы держать пароль в
    /// памяти между двумя запросами и присылать его снова — то есть второй фактор увеличил бы
    /// число мест, где пароль бывает, ради того чтобы уменьшить цену его утечки.
    ///
    /// ⚠ ⚠ <b>Опечатка расписку не гасит, но попыток у расписки <see cref="ChallengeAttempts.Limit"/>,
    /// не больше</b> (ревью 2026-10-20). Прежде неверный код не гасил ни расписку, ни код письмом
    /// (тот гасится только совпавшим значением), и одна расписка перебиралась до самой
    /// блокировки учётной записи. Теперь исчерпанная расписка гасится в базе, и вход начинается с
    /// пароля.
    ///
    /// ⚠ <b>Неверный код — такая же неудачная попытка, как неверный пароль.</b> До
    /// 2026-08-22 он писался в журнал и <b>не считался</b>: счётчик двигал только разбор
    /// пароля. То есть блокировки на подбор кода не было вовсе — а расписка берётся заново
    /// одним запросом входа, если пароль известен. Ровно тот случай, ради которого второй
    /// фактор и заводят: пароль утёк, и всё, что стоит между чужим и учётной записью, — шесть
    /// цифр. Без счётчика их перебирали бы столько, сколько выдержит ограничитель частоты.
    ///
    /// ⚠ ⚠ <b>Обе попытки — расписки и учётной записи — занимаются ДО проверки кода</b>, а не
    /// засчитываются после (ревью 2026-10-20): прежде блокировка читалась из загруженной строки,
    /// и одновременные запросы получали каждый свою догадку, сколько бы их ни было.
    /// </remarks>
    public async Task<SignInOutcome> CompleteTwoFactorAsync(
        string challenge,
        string code,
        RequestOrigin origin,
        CancellationToken cancellationToken = default)
    {
        var receipt = (
            Owner: await invitations.PeekAsync(challenge, Purposes.TwoFactor, cancellationToken),
            Binding: await invitations.PeekBindingAsync(challenge, Purposes.TwoFactor, cancellationToken));

        if (receipt.Owner is not { } userId)
        {
            throw Expired(ErrorCodes.SessionExpired, "Вход не завершён вовремя. Начните заново.");
        }

        var user = await users.FindAsync(userId, cancellationToken);

        if (user is not { IsActive: true })
        {
            await RecordFailureAsync(userId, user?.Email ?? string.Empty, "inactive", cancellationToken);

            throw Invalid();
        }

        var now = clock.GetUtcNow();

        if (receipt.Binding != Invitations.CodeBinding(user.PasswordHash))
        {
            throw Expired(ErrorCodes.SessionExpired, "Пароль изменён. Начните вход заново.");
        }

        // ⚠ Попытка расписки — первой: исчерпанная расписка не стоит и попытки учётной записи.
        if (!challengeAttempts.Take(challenge))
        {
            await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, cancellationToken);

            throw Expired(ErrorCodes.SessionExpired, "Слишком много неверных кодов. Начните вход заново.");
        }

        // ⚠ Попытка учётной записи — до проверки кода, одним условным обновлением. Не заняли —
        // учётная запись заперта: расписка могла быть взята за секунду до того, как порог
        // сработал, и блокировка обязана закрывать все двери, а не ту, через которую вошли в
        // прошлый раз. Код при этом не проверяется вовсе.
        if (!await failures.TakeAsync(userId, now, cancellationToken))
        {
            await RecordFailureAsync(userId, user.Email, "locked", cancellationToken);

            throw Invalid();
        }

        // ⚠ ⚠ ⚠ Проверяется ровно ОДИН способ — тот, которым вход и спрашивали (ревью
        // 2026-10-20). Подтверждённый аутентификатор — только его код; код письмом у такой
        // учётной записи не принимается, даже если бы нашёлся: письмо не заменяет аутентификатор
        // (разбор — у SecondFactorPolicy). Нет аутентификатора — только код письмом, привязанный
        // к ЭТОЙ расписке.
        var завёл = await factors.ConfirmedAsync(userId, cancellationToken);

        var подошёл = завёл
            ? await factors.VerifyAsync(userId, code, cancellationToken)
            : await codes.VerifyAsync(userId, code, cancellationToken, Invitations.CodeBinding(challenge));

        if (!подошёл)
        {
            // Счёт уже занят; записываем: журнал — чтобы разбирающий происшествие увидел, что
            // подбирали именно код.
            await RecordFailureAsync(userId, user.Email, "bad_code", cancellationToken);

            // Последняя попытка расписки потрачена — гасим её сразу: следующий запрос по ней
            // услышит «начните заново», а не очередное «код не подошёл».
            var исчерпана = challengeAttempts.Exhausted(challenge);

            if (исчерпана)
            {
                await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, cancellationToken);
            }

            var retry = !исчерпана && await db.Set<User>().AsNoTracking()
                .AnyAsync(row => row.Id == userId && row.IsActive && (row.LockedUntil == null || row.LockedUntil <= now), cancellationToken);

            throw new AppException(
                ErrorCodes.TwoFactorInvalid, "Код не подошёл", HttpStatusCode.Unauthorized,
                new Dictionary<string, object?> { ["can_retry"] = retry });
        }

        // ⚠ Код верен — попытка неудачей не была. Возвращается ДО транзакции: иначе попытка,
        // доведшая счёт до порога, заперла бы того, кто ввёл верный код (`EnsureCurrent`).
        await failures.ReturnAsync(userId, cancellationToken);

        return await db.InTransactionAsync(
            async (token) =>
            {
                var fresh = await users.FindAsync(userId, token) ?? throw Invalid();
                EnsureCurrent(fresh, user.PasswordHash, now);
                // Успех гасит расписку условно, в той же транзакции: два завершения не выдадут две сессии.
                if (await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, token) != userId)
                    throw Expired(ErrorCodes.SessionExpired, "Вход уже завершён. Начните заново.");

                // ⚠ ⚠ ⚠ Код письмом у того, кто фактор ещё НИ РАЗУ не подтверждал, — пропуск в
                // СЕССИЮ НАСТРОЙКИ, а не в работу (ревью 2026-10-20). Письмо доказало ящик, но
                // аутентификатор обязателен всем: сессия остаётся без отметки фактора, и
                // распознаватель личности откроет в ней только подключение аутентификатора.
                // Отметка «фактор подтверждён» тоже не ставится — подтверждать было нечего.
                // Прежде такая учётная запись получала сессию настройки по одному паролю.
                //
                // ⚠ ⚠ То же — у того, кто аутентификатор подтверждал, а потом отключил сам: отметка
                // «фактор подтверждён» у него осталась (она держит вход без почты закрытым), но
                // полной сессии по коду из ящика быть не должно — пароль сбрасывается тем же ящиком,
                // и ящик один давал бы весь вход. Без аутентификатора — только сессия настройки.
                var настройка = !завёл;

                if (!настройка)
                {
                    fresh.TwoFactorConfiguredAt ??= now;
                }

                fresh.FailedLoginCount = 0;
                fresh.LockedUntil = null;
                fresh.UpdatedAt = now;

                // ⚠ Выдаёт СОСЕД, и внутри нашей транзакции. Иначе вход состоялся бы отдельно от
                // обнуления счётчика неудач и от строки журнала: при сбое между ними
                // человек получил бы токены, а система осталась бы при мнении, что он
                // не входил.
                // ⚠ Отметка фактора ставится ВРЕМЕНЕМ КОДА: она отличает рабочую сессию от сессии
                // настройки. У сессии настройки её нет.
                var pair = await tokens.IssueAsync(new TokenIssueRequest(fresh.Id, TokenKinds.Session,
                    origin.UserAgent, origin.Address, StepUpAt: настройка ? null : now), token);

                audit.Write(new AuditEntry(
                    IdentityActions.LoggedIn,
                    ActorId: fresh.Id,
                    ResourceType: IdentityResources.Session,
                    ResourceId: pair.TokenId,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["two_factor"] = true,
                        ["method"] = завёл ? SecondFactorMethods.Totp : SecondFactorMethods.Email,
                        ["second_factor"] = настройка ? "enrollment" : "confirmed",
                    }));

                await db.SaveChangesAsync(token);

                return new SignInOutcome(pair);
            },
            cancellationToken);
    }

    /// <summary>
    /// Выслать код письмом по живой расписке — первый или повторный.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>Тому, кто подтвердил аутентификатор, письмо НЕ шлётся</b> (ревью безопасности
    /// 2026-10-20). Прежде это был запасной путь «нет доступа к коду?» — и вместе с публичным
    /// «забыл пароль» он отдавал учётную запись под аутентификатором тому, у кого есть один лишь
    /// ящик. Потерявшему телефон фактор снимает другой администратор или консольная
    /// <c>reset-superadmin-2fa</c>. Здесь — только повторная отправка кода тем, у кого письмо и
    /// есть способ входа.
    ///
    /// ⚠ ⚠ <b>Расписка ПРОСМАТРИВАЕТСЯ, а не гасится.</b> Гашение здесь означало бы, что
    /// нажавший «пришлите ещё раз» теряет вход целиком: расписки больше нет, а код ещё не
    /// пришёл. Гасит её только успешное завершение входа — или исчерпанные попытки кода.
    ///
    /// ⚠ <b>Проверка тем же путём, что и завершение входа:</b> расписка живая, учётная запись
    /// активна, блокировка не действует. Иначе появилась бы вторая дверь с более слабой
    /// проверкой — ровно то, чего требует не делать порядок шагов входа.
    ///
    /// ⚠ <b>Отправка не бросает</b> (см. <see cref="LoginCodes"/>): ответ один и тот же, послали
    /// письмо или упёрлись в ограничение частоты. Иначе адрес отвечал бы, существует ли он.
    ///
    /// ⚠ Прежний код письмом гасится выдачей нового — двух живых кодов не бывает.
    /// </remarks>
    public async Task SendEmailCodeAsync(string challenge, CancellationToken cancellationToken = default)
    {
        var userId = await invitations.PeekAsync(challenge, Purposes.TwoFactor, cancellationToken)
            ?? throw Invalid();

        var user = await users.FindAsync(userId, cancellationToken);

        if (user is not { IsActive: true })
        {
            throw Invalid();
        }

        if (user.LockedUntil is { } until && until > clock.GetUtcNow())
        {
            throw Invalid();
        }

        // ⚠ ⚠ Владельцу аутентификатора — отказ, и прямой: просящий уже прошёл пароль, способ
        // ему назван в ответе на него, а «ждите письма, которого не будет» было бы хуже отказа.
        // Список способов (`AvailableAsync`) письма ему тоже не даёт; проверка здесь — ради
        // внятного текста, а не единственная стена.
        if (await factors.ConfirmedAsync(user.Id, cancellationToken))
        {
            throw new AppException(
                ErrorCodes.TwoFactorInvalid,
                "Вход подтверждается приложением-аутентификатором; код письмом его не заменяет. "
                + "Нет доступа к приложению — попросите другого администратора сбросить второй фактор.",
                HttpStatusCode.Conflict);
        }

        // ⚠ Почты нет — говорим прямо, а не молчим: экран предложил способ, которого на этой
        // установке не существует, и человеку надо понять, что ждать письма не за чем.
        if (!(await methods.AvailableAsync(user, cancellationToken)).Contains(SecondFactorMethods.Email))
        {
            throw new AppException(
                ErrorCodes.TwoFactorInvalid,
                "Код письмом на этой установке недоступен",
                HttpStatusCode.Conflict);
        }

        await db.InTransactionAsync(
            async (token) =>
            {
                EnsureMail(await codes.IssueAsync(user, token, Invitations.CodeBinding(challenge)));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    // --- внутреннее -----------------------------------------------------------------------

    private static void EnsureCurrent(User user, string passwordHash, DateTimeOffset now)
    {
        if (!user.IsActive || user.DeletedAt is not null || user.PasswordHash != passwordHash
            || user.LockedUntil > now)
        {
            throw Invalid();
        }
    }

    internal static void EnsureMail(MailQueueResult result)
    {
        if (result != MailQueueResult.Queued)
        {
            throw new AppException(result == MailQueueResult.RateLimited ? ErrorCodes.RateLimited : ErrorCodes.MailNotConfigured,
                result == MailQueueResult.RateLimited ? "Код уже запрошен. Подождите перед повторной отправкой."
                    : "Почта недоступна. Выберите приложение или обратитесь к администратору.",
                result == MailQueueResult.RateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable);
        }
    }

    private async Task RecordFailureAsync(
        Guid? userId, string email, string reason, CancellationToken cancellationToken)
    {
        // ⚠ В журнал — настоящая причина, наружу — одна на всех. Журнал закрыт правом, и без
        // причины запись бесполезна тому, кто разбирает происшествие: «перебор пароля» и
        // «стучатся в несуществующие адреса» — разные события с разной реакцией.
        audit.Write(new AuditEntry(
            IdentityActions.LoggedIn,
            ActorId: userId,
            Result: AuditResult.Deny,
            ResourceType: IdentityResources.Session,
            Reason: reason,
            Changes: new Dictionary<string, object?> { ["email"] = email }));

        // Своим сохранением: вызывающий сейчас поднимет отказ, и запись уехала бы с откатом.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Один и тот же отказ на все причины — иначе форма входа становится оракулом.</summary>
    private static AppException Invalid() => new(
        ErrorCodes.InvalidCredentials, "Неверный адрес или пароль", HttpStatusCode.Unauthorized);

    private static AppException Expired(string code, string message) =>
        new(code, message, HttpStatusCode.Unauthorized);

}
