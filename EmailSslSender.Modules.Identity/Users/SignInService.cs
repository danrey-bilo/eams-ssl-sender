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
    /// Порядок такой: найти → проверить блокировку → <b>проверить пароль</b> → проверить
    /// активность. Активность после пароля нарочно: до него отключённая учётная запись
    /// отвечала бы заметно быстрее живой с неверным паролем.
    ///
    /// А вот блокировка проверяется <b>до</b> пароля, и это осознанная уступка: считать
    /// argon2 для заблокированной учётной записи значит дать способ нагружать сервер
    /// перебором, который всё равно ничем не кончится. Разницу во времени здесь мы
    /// выравниваем холостой проверкой.
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

        if (user.LockedUntil is { } until && until > now)
        {
            await passwords.WasteVerificationTimeAsync(cancellationToken);
            await RecordFailureAsync(user.Id, email, "locked", cancellationToken);

            throw Invalid();
        }

        if (!await passwords.VerifyAsync(user.PasswordHash, password, cancellationToken))
        {
            await CountFailureAsync(user, "bad_password", now, cancellationToken);
            await RecordFailureAsync(user.Id, email, "bad_password", cancellationToken);

            throw Invalid();
        }

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
        // ⚠ ⚠ Способ решается ПОЛИТИКОЙ, а не наличием аутентификатора. Заведённый и
        // подтверждённый аутентификатор перевешивает всё; иначе — выбор человека или умолчание
        // системы, с двумя послаблениями вниз (`SecondFactorPolicy`).
        var способ = await methods.ResolveAsync(user, cancellationToken);

        // ⚠ ⚠ Администратор, ни разу не подтверждавший фактор (только что заведён командой,
        // активировал приглашение, фактор сняли), получает СЕССИЮ НАСТРОЙКИ: без кода, но с
        // ограничением «сначала подключите приложение-аутентификатор»
        // (`SessionIdentityResolver` — сессия без отметки фактора). Иначе первый вход упирался
        // бы в почту, которая на свежей установке ещё не настроена, а настроить её можно только
        // войдя. В EAMS так устроен вход суперпользователя (решение 0082); здесь каждая учётная
        // запись — администратор.
        if (user.TwoFactorConfiguredAt is null && способ == SecondFactorMethods.Email)
        {
            способ = SecondFactorMethods.None;
        }

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

                    // ⚠ ⚠ Остальные доступные способы — ради «нет доступа к коду?» (решение
                    // 0061). Кодов восстановления в системе нет; потерявший телефон обязан
                    // видеть на этом же экране, чем ещё он может подтвердить вход. Список
                    // отдаётся ПОСЛЕ пароля — до него он был бы сведением о чужой учётной
                    // записи.
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
    /// ⚠ <b>Расписка гасится в любом случае — и при неверном коде тоже.</b> Иначе одна
    /// расписка давала бы неограниченное число попыток подобрать шестизначный код: миллион
    /// вариантов при трёх попытках в секунду — это четыре дня, а при неограниченных попытках
    /// — минуты.
    ///
    /// ⚠ <b>Неверный код — такая же неудачная попытка, как неверный пароль.</b> До
    /// 2026-08-22 он писался в журнал и <b>не считался</b>: счётчик двигал только разбор
    /// пароля. То есть блокировки на подбор кода не было вовсе — а расписка берётся заново
    /// одним запросом входа, если пароль известен. Ровно тот случай, ради которого второй
    /// фактор и заводят: пароль утёк, и всё, что стоит между чужим и учётной записью, — шесть
    /// цифр. Без счётчика их перебирали бы столько, сколько выдержит ограничитель частоты.
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

        if (user.LockedUntil is { } until && until > now)
        {
            // Расписка могла быть взята за секунду до того, как порог сработал. Проверка
            // здесь стоит по той же причине, что и на входе: блокировка обязана закрывать все
            // двери, а не ту, через которую вошли в прошлый раз.
            await RecordFailureAsync(userId, user.Email, "locked", cancellationToken);

            throw Invalid();
        }

        if (receipt.Binding != Invitations.CodeBinding(user.PasswordHash))
        {
            throw Expired(ErrorCodes.SessionExpired, "Пароль изменён. Начните вход заново.");
        }

        // ⚠ ⚠ Проверяются ОБА способа, и порядок здесь не важен: у человека живёт ровно
        // один — либо подтверждённый аутентификатор, либо выданный код письмом. Спрашивать
        // «какой способ был» значило бы хранить это в расписке, то есть завести четвёртое
        // состояние там, где хватает двух проверок.
        //
        // ⚠ Код письмом гасится ЛЮБОЙ проверкой, как и расписка: иначе одна выдача давала бы
        // неограниченное число попыток подобрать шесть цифр.
        var подошёл = await factors.VerifyAsync(userId, code, cancellationToken)
            || await codes.VerifyAsync(userId, code, cancellationToken, Invitations.CodeBinding(challenge));

        if (!подошёл)
        {
            // Считаем и записываем: счётчик — чтобы подбор упёрся в блокировку, журнал —
            // чтобы разбирающий происшествие увидел, что подбирали именно код.
            await CountFailureAsync(user, "bad_code", now, cancellationToken);
            await RecordFailureAsync(userId, user.Email, "bad_code", cancellationToken);

            var retry = await db.Set<User>().AsNoTracking()
                .AnyAsync(row => row.Id == userId && row.IsActive && (row.LockedUntil == null || row.LockedUntil <= now), cancellationToken);

            throw new AppException(
                ErrorCodes.TwoFactorInvalid, "Код не подошёл", HttpStatusCode.Unauthorized,
                new Dictionary<string, object?> { ["can_retry"] = retry });
        }

        return await db.InTransactionAsync(
            async (token) =>
            {
                var fresh = await users.FindAsync(userId, token) ?? throw Invalid();
                EnsureCurrent(fresh, user.PasswordHash, now);
                // Успех гасит расписку условно, в той же транзакции: два завершения не выдадут две сессии.
                if (await invitations.ConsumeAsync(challenge, Purposes.TwoFactor, token) != userId)
                    throw Expired(ErrorCodes.SessionExpired, "Вход уже завершён. Начните заново.");
                fresh.TwoFactorConfiguredAt ??= now;

                fresh.FailedLoginCount = 0;
                fresh.LockedUntil = null;
                fresh.UpdatedAt = now;

                // ⚠ Выдаёт СОСЕД, и внутри нашей транзакции. Иначе вход состоялся бы отдельно от
                // обнуления счётчика неудач и от строки журнала: при сбое между ними
                // человек получил бы токены, а система осталась бы при мнении, что он
                // не входил.
                // ⚠ Отметка фактора ставится ВРЕМЕНЕМ КОДА: первые минуты сессии опасные
                // действия не переспрашивают — человек только что ввёл код.
                var pair = await tokens.IssueAsync(new TokenIssueRequest(fresh.Id, TokenKinds.Session,
                    origin.UserAgent, origin.Address, StepUpAt: now), token);

                audit.Write(new AuditEntry(
                    IdentityActions.LoggedIn,
                    ActorId: fresh.Id,
                    ResourceType: IdentityResources.Session,
                    ResourceId: pair.TokenId,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["two_factor"] = true,
                    }));

                await db.SaveChangesAsync(token);

                return new SignInOutcome(pair);
            },
            cancellationToken);
    }

    /// <summary>
    /// Выслать код письмом по живой расписке — «нет доступа к коду?».
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Расписка ПРОСМАТРИВАЕТСЯ, а не гасится.</b> Гашение здесь означало бы, что
    /// нажавший «пришлите письмом» теряет вход целиком: расписки больше нет, а код ещё не
    /// пришёл. Гасит её только успешное завершение входа.
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

    /// <summary>
    /// Засчитать неудачу и заблокировать, если их набралось достаточно.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>По заблокированной учётной записи счётчик не растёт</b> — сюда мы просто не
    /// доходим, проверка блокировки стоит раньше. Иначе непрерывный перебор продлевал бы
    /// блокировку бесконечно, и настоящий владелец не вошёл бы никогда: перебор превратился
    /// бы в вечный отказ в обслуживании, то есть в оружие против того, кого защищает.
    ///
    /// Счётчик привязан к учётной записи, а не к адресу: OWASP прямо требует этого, иначе
    /// перебор с тысячи адресов не упирается ни во что.
    /// </remarks>
    private async Task CountFailureAsync(
        User user, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var threshold = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutThreshold, cancellationToken));
        var minutes = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutMinutes, cancellationToken));

        // Одним обновлением, а не «прочитать-решить-записать»: параллельные попытки перебора
        // — это ровно тот случай, ради которого счётчик существует, и они перетирали бы друг
        // друга, так и не дойдя до порога.
        await db.Set<User>()
            .Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.FailedLoginCount, row => row.FailedLoginCount + 1)
                    .SetProperty(
                        row => row.LockedUntil,
                        row => row.FailedLoginCount + 1 >= threshold ? now.AddMinutes(minutes) : row.LockedUntil),
                cancellationToken);
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
