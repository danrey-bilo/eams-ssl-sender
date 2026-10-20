using EmailSslSender.Shared.Contracts.Tokens;
using System.Net;
using System.Security.Cryptography;
using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Naming;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>Что показать человеку, который включает фактор.</summary>
/// <param name="Secret">Секрет в base32 — ввести руками, если камера не работает.</param>
/// <param name="Uri">Ссылка <c>otpauth://</c> для QR-кода.</param>
internal sealed record TwoFactorSetup(string Secret, string Uri);

/// <summary>Состояние второго фактора у человека.</summary>
/// <param name="Enrolled">Секрет заведён (может быть ещё не подтверждён).</param>
/// <param name="Confirmed">Фактор действует.</param>
/// <param name="Required">Система требует его от этого человека.</param>
/// <remarks>
/// ⚠ Кодов восстановления в системе нет (решение 0061), и письмо подтверждённый аутентификатор
/// не заменяет (ревью 2026-10-20): потерявшему телефон фактор снимает другой администратор или
/// консоль. Поля «осталось кодов» здесь не бывает, и экрана, который бы его показывал, — тоже.
/// </remarks>
internal sealed record TwoFactorStatus(bool Enrolled, bool Confirmed, bool Required);

/// <summary>
/// Второй фактор: включение, подтверждение, проверка, отключение.
/// </summary>
/// <remarks>
/// <b>Секрет хранится запечатанным (этап 7.5) и в открытом виде не лежит нигде.</b> Он
/// первый по-настоящему обратимый секрет в системе — пароли и токены только хешируются, а
/// seed надо уметь развернуть, иначе нечем считать код. Ключ данных свой на каждого
/// человека: удаление строки уносит ключ с собой, и секрет становится нечитаемым даже из
/// резервной копии.
///
/// <b>Два состояния.</b> Заведённый секрет не действует, пока человек не подтвердил его
/// кодом. Иначе нажавший «включить» и не сумевший отсканировать код запирал бы себя снаружи
/// собственной учётной записи.
///
/// ⚠ <b>Отключение требует пароля.</b> Иначе угнанная вкладка снимает второй фактор — то
/// есть он защищает ровно до первой угнанной вкладки, а именно от неё и заводится.
/// </remarks>
internal sealed class TwoFactorService(
    SenderDbContext db,
    UserRepository users,
    Invitations invitations,
    ITokenRevoker revoker,
    IStepUpMarker marker,
    LoginFailures failures,
    ISecretEnvelope envelope,
    IPasswordHasher passwords,
    IAuditWriter audit,
    ICurrentUser actor,
    TimeProvider clock)
{
    /// <summary>Привязка секрета к месту. Перенесённая в чужую строку запись не откроется.</summary>
    private static string Context(Guid userId) => $"user:{userId}:totp_seed";

    /// <summary>Как установка называется в приложении-аутентификаторе.</summary>
    /// <remarks>
    /// ⚠ Меняется только вместе с перевыпуском всех секретов: у тех, кто уже добавил запись,
    /// старое имя остаётся в приложении, и две записи с разными подписями читаются как две
    /// разные системы.
    /// </remarks>
    private const string Issuer = "Email SSL Sender";

    /// <summary>
    /// Завести секрет и показать его человеку.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Единственный момент, когда секрет покидает сервер.</b> Больше его не показывают
    /// никогда: потерял — заводи новый.
    ///
    /// Повторный вызов до подтверждения заменяет секрет. Это нужно тому, кто начал заводить
    /// фактор, закрыл вкладку и начал заново: старый секрет он всё равно уже не введёт.
    /// Подтверждённый фактор так не перезаводится — сначала отключить, и с паролем.
    /// </remarks>
    public async Task<TwoFactorSetup> SetupAsync(CancellationToken cancellationToken = default)
    {
        var user = await RequireActorAsync(cancellationToken);
        var existing = await FindAsync(user.Id, cancellationToken);

        if (existing is { ConfirmedAt: not null })
        {
            throw new AppException(
                ErrorCodes.TwoFactorAlreadyEnabled,
                "Второй фактор уже включён. Чтобы завести новый секрет, сначала отключите его.",
                HttpStatusCode.Conflict);
        }

        var secret = Totp.NewSecret();

        try
        {
            var key = envelope.CreateDataKey();
            var sealedSecret = envelope.Seal(key, secret, Context(user.Id));

            if (existing is null)
            {
                db.Add(new UserTwoFactor
                {
                    UserId = user.Id,
                    DataKey = key.Bytes,
                    Secret = sealedSecret.Bytes,
                    CreatedAt = clock.GetUtcNow(),
                });
            }
            else
            {
                existing.DataKey = key.Bytes;
                existing.Secret = sealedSecret.Bytes;
                existing.CreatedAt = clock.GetUtcNow();
                existing.LastStep = -1;
            }

            await db.SaveChangesAsync(cancellationToken);

            return new TwoFactorSetup(Totp.ToBase32(secret), Totp.Uri(Issuer, user.Email, secret));
        }
        finally
        {
            // Открытый seed не должен пережить эту строку в памяти дольше необходимого.
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Подтвердить фактор кодом.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Пароля здесь нет — решение владельца 0061</b>
    /// (<c>docs/decisions/0061-второй-фактор-без-кодов-восстановления.md</c>). Сессия уже
    /// доказала, кто это; код доказывает, что аутентификатор в руках. Третьего доказательства
    /// действие, которое защиту <b>усиливает</b>, не требует.
    ///
    /// ⚠ ⚠ <b>Прежние сессии здесь БОЛЬШЕ НЕ ГАСЯТСЯ, и это прямое следствие снятого пароля.</b>
    /// Пока пароль спрашивался, гашение закрывало кражу: включивший фактор выбрасывал того, кто
    /// увёл его токен обновления (путь обновления про фактор не знает и молол бы новые пары до
    /// конца абсолютного срока сессии). Без пароля та же строка становится оружием: севший за
    /// незапертый компьютер включает фактор со <b>своего</b> аутентификатора и одним действием
    /// выбрасывает хозяина отовсюду. Выгнать все сессии по-прежнему можно — осознанно и своей
    /// кнопкой на экране сессий, а не побочным действием включения.
    ///
    /// ⚠ ⚠ <b>Кодов восстановления нет, и письмо их больше не заменяет</b> (ревью безопасности
    /// 2026-10-20). Подтверждённый аутентификатор — единственный второй фактор своего владельца
    /// (<see cref="SecondFactorPolicy"/>): запасной путь «код письмом» вместе с публичным сбросом
    /// пароля отдавал учётную запись тому, у кого есть один лишь ящик. Потерял аутентификатор —
    /// фактор снимает другой администратор (<see cref="ResetForAsync"/>) или консольная
    /// <c>reset-superadmin-2fa</c>.
    /// </remarks>
    public async Task ConfirmAsync(string code, CancellationToken cancellationToken = default)
    {
        await db.InTransactionAsync(
            async (token) =>
            {
                var user = await RequireActorAsync(token);

                var factor = await FindAsync(user.Id, token)
                    ?? throw new AppException(
                        ErrorCodes.TwoFactorNotEnrolled,
                        "Секрет не заведён: начните с включения",
                        HttpStatusCode.Conflict);

                if (factor.ConfirmedAt is not null)
                {
                    throw new AppException(
                        ErrorCodes.TwoFactorAlreadyEnabled, "Второй фактор уже включён", HttpStatusCode.Conflict);
                }

                var step = VerifyCode(factor, code)
                    ?? throw new AppException(
                        ErrorCodes.TwoFactorInvalid, "Код не подошёл", HttpStatusCode.BadRequest);

                factor.ConfirmedAt = clock.GetUtcNow();
                factor.LastStep = step;
                user.TwoFactorConfiguredAt ??= factor.ConfirmedAt;
                if (actor.SessionId is { } session && !await marker.MarkAsync(session, factor.ConfirmedAt.Value, token))
                {
                    throw new AppException(ErrorCodes.SessionRevoked, "Сессия закрыта. Войдите заново.", HttpStatusCode.Unauthorized);
                }


                audit.Write(new AuditEntry(
                    IdentityActions.TwoFactorEnabled,
                    ActorId: user.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>
    /// Отключить фактор. Требует пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ Одной транзакцией. Коды восстановления удаляются оператором и исчезают немедленно, а
    /// строка журнала и удаление самого фактора уезжают на сохранении: сбой между ними
    /// оставлял бы состояние, из которого человека не вытащить, — фактор включён, кодов нет,
    /// в журнале ничего. Потерял телефон — заперт снаружи собственной учётной записи.
    /// Найдено разбором 2026-08-22.
    /// </remarks>
    public async Task DisableAsync(string password, CancellationToken cancellationToken = default)
    {
        // Переспрос до транзакции — разбор у ConfirmAsync и у EnsurePasswordAsync.
        await EnsurePasswordAsync(await RequireActorAsync(cancellationToken), password, cancellationToken);

        await db.InTransactionAsync(
            async (token) =>
            {
                var user = await RequireActorAsync(token);

                var factor = await FindAsync(user.Id, token);

                if (factor is null)
                {
                    return;
                }

                db.Remove(factor);
                await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, clock.GetUtcNow(), token);

                audit.Write(new AuditEntry(
                    IdentityActions.TwoFactorDisabled,
                    ActorId: user.Id,
                    ResourceType: IdentityResources.User,
                    ResourceId: user.Id));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    /// <summary>
    /// Снять фактор <b>другому человеку</b>. Пароля снимаемого не требует — его никто не знает.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Без этого человек, потерявший и приложение, и коды восстановления, заперт</b>
    /// снаружи собственной учётной записи навсегда. Если он единственный — заперта вся
    /// система; на этот случай есть консольная <c>reset-superadmin-2fa</c>.
    ///
    /// ⚠ <b>Действие опасное:</b> снимающий снимает именно защиту, а знай он вдобавок чужой
    /// пароль — вход открыт. Эндпоинт поэтому требует свежего второго фактора; служба не знает,
    /// кто её позвал и зачем, и свежесть не проверяет.
    ///
    /// ⚠ <b>Сессии снимаемого закрываются.</b> Фактор снимают, когда телефон потерян или
    /// украден; живая сессия на нём — это ровно то, ради чего снятие и происходит.
    /// </remarks>
    /// <returns><c>false</c> — фактора и не было.</returns>
    public Task<bool> ResetForAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async (token) =>
            {
                var user = await users.FindAsync(userId, token)
                    ?? throw AppException.NotFound("Пользователь не найден");

                return await ResetCoreAsync(user, fromConsole: false, token);
            },
            cancellationToken);

    /// <summary>Сбросить фактор суперпользователя из консоли с обязательным отзывом старого доступа (0084).</summary>
    internal Task<bool> ResetSuperuserForConsoleAsync(Guid userId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async token =>
        {
            var user = await users.FindAsync(userId, token)
                ?? throw AppException.NotFound("Учётная запись не найдена");
            return await ResetCoreAsync(user, fromConsole: true, token);
        }, cancellationToken);

    private async Task<bool> ResetCoreAsync(User user, bool fromConsole, CancellationToken cancellationToken)
    {
        var factor = await FindAsync(user.Id, cancellationToken);
        var hadFactor = factor is not null || user.TwoFactorConfiguredAt is not null;
        if (!hadFactor && !fromConsole) return false;

        if (factor is not null) db.Remove(factor);
        var now = clock.GetUtcNow();
        user.TwoFactorConfiguredAt = null;
        user.UpdatedAt = now;
        if (fromConsole)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
        }
        await invitations.QuenchAsync(user.Id, Purposes.TwoFactor, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.LoginCode, now, cancellationToken);
        await invitations.QuenchAsync(user.Id, Purposes.StepUpCode, now, cancellationToken);
        audit.Write(new AuditEntry(
            IdentityActions.TwoFactorReset,
            ActorId: fromConsole ? null : actor.Id,
            ActorType: fromConsole ? AuditActorType.System : null,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Changes: fromConsole ? new Dictionary<string, object?> { ["via_cli"] = true } : null));
        await db.SaveChangesAsync(cancellationToken);
        await revoker.RevokeAllAsync(user.Id, RevokeReasons.TwoFactorReset, cancellationToken);
        return hadFactor;
    }

    /// <summary>Что показать на экране настроек.</summary>
    public async Task<TwoFactorStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var user = await RequireActorAsync(cancellationToken);
        var factor = await FindAsync(user.Id, cancellationToken);

        return new TwoFactorStatus(
            factor is not null,
            factor is { ConfirmedAt: not null },
            true);
    }

    /// <summary>
    /// Проверить код аутентификатора при входе.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Кода восстановления здесь больше нет</b> (решение 0061). Код письмом проверяет
    /// <see cref="LoginCodes"/>, и путь входа спрашивает ровно одну из двух служб: владельца
    /// аутентификатора — эту, прочих — ту (ревью 2026-10-20: письмо аутентификатор не заменяет).
    /// </remarks>
    public async Task<bool> VerifyAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var factor = await FindAsync(userId, cancellationToken);

        if (factor is not { ConfirmedAt: not null })
        {
            return false;
        }

        if (VerifyCode(factor, code) is { } step)
        {
            // ⚠ Условным обновлением, а не присваиванием. Защита от повторного использования
            // кода держится на том, что принятый шаг записан; прочитать-решить-записать здесь
            // означало бы, что два одновременных запроса с ОДНИМ кодом оба увидят прежний шаг
            // и оба пройдут. А перехваченный код — это ровно тот случай, ради которого защита
            // и стоит: у вора и у хозяина код один и тот же. Найдено разбором 2026-08-22.
            var accepted = await db.Set<UserTwoFactor>()
                .Where(row => row.UserId == userId && row.LastStep < step)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastStep, step), cancellationToken);

            return accepted == 1;
        }

        return false;
    }

    /// <summary>Есть ли у человека действующий фактор.</summary>
    public Task<bool> ConfirmedAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Set<UserTwoFactor>().AnyAsync(row => row.UserId == userId && row.ConfirmedAt != null, cancellationToken);



    // --- внутреннее -----------------------------------------------------------------------

    /// <summary>Развернуть секрет и проверить код. Возвращает принятый шаг.</summary>
    private long? VerifyCode(UserTwoFactor factor, string code)
    {
        var secret = envelope.Open(
            new WrappedDataKey(factor.DataKey),
            new SealedSecret(factor.Secret),
            Context(factor.UserId));

        try
        {
            return Totp.Verify(secret, code, clock.GetUtcNow(), factor.LastStep);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private Task<UserTwoFactor?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Set<UserTwoFactor>().FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);

    private async Task<User> RequireActorAsync(CancellationToken cancellationToken)
    {
        if (actor.Id is not { } id)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", HttpStatusCode.Unauthorized);
        }

        return await users.FindAsync(id, cancellationToken)
            ?? throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Unauthorized);
    }

    /// <summary>Переспросить пароль. Без него угнанная вкладка снимает второй фактор.</summary>
    /// <summary>
    /// Переспросить пароль — с теми же последствиями, что и на входе.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Неудача считается и пишется в журнал.</b> До 2026-08-24 здесь стояла голая
    /// проверка: ни счётчика, ни строки, ни блокировки. То есть угнанная вкладка перебирала
    /// пароль владельца через отключение фактора — бесшумно и без ограничений, — а именно
    /// этот сценарий переспросом и закрывают. Вход давно считает такие попытки; здесь забыли.
    ///
    /// ⚠ <b>Запертая учётная запись переспрос не проходит.</b> Иначе блокировка закрывала бы
    /// одну дверь из двух: подбор через вход упирается в порог, а подбор через отключение
    /// фактора продолжался бы как ни в чём не бывало.
    ///
    /// Причина в журнале своя (<c>reauth_bad_password</c>), а не общая с входом: разбирающему
    /// происшествие важно, что подбирали не на форме входа, а внутри уже открытой сессии — это
    /// разные истории с разной реакцией.
    ///
    /// ⚠ Попытка занимается ДО argon2 одним условным обновлением и возвращается верным паролем
    /// (ревью 2026-10-20, разбор у <see cref="LoginFailures"/>): порог не зависит от числа
    /// одновременных запросов.
    /// </remarks>
    private async Task EnsurePasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (!await failures.TakeAsync(user.Id, now, cancellationToken))
        {
            throw new AppException(
                ErrorCodes.InvalidCredentials, "Пароль неверен", HttpStatusCode.BadRequest);
        }

        if (await passwords.VerifyAsync(user.PasswordHash, password, cancellationToken))
        {
            await failures.ReturnAsync(user.Id, cancellationToken);

            return;
        }

        await failures.DenyAsync(user, "reauth_bad_password", cancellationToken);

        throw new AppException(
            ErrorCodes.InvalidCredentials, "Пароль неверен", HttpStatusCode.BadRequest);
    }
}
