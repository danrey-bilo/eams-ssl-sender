using System.Net;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Переподтвердить личность на текущем токене.
/// </summary>
/// <remarks>
/// <b>Зачем это отдельно от входа.</b> Есть действия, которых мало «человек когда-то вошёл»:
/// показ пароля из хранилища, помеченного требованием фактора. Вход мог быть неделю назад и
/// на чужом ноутбуке, оставленном разблокированным; переподтверждение спрашивает код <b>сейчас</b>.
///
/// ⚠ <b>Отметка ставится на предъявленный токен, а не на человека</b> (решение
/// <c>docs/decisions/0023-хранилища-ответы-до-первой-строки.md</c> §3). Ставит её сосед —
/// владелец токенов, — через <see cref="IStepUpMarker"/>; своей таблицы «когда этот человек
/// подтверждался» здесь нет и заводить её нельзя.
///
/// ⚠ ⚠ <b>Действия после входа свежей отметки не требуют</b> (решение владельца, 2026-10-20):
/// второй фактор спрашивается только при входе. Отметка нужна, чтобы отличать полноценную
/// сессию от сессии настройки; этим сервисом её ставят, когда сессию настройки завершают кодом.
///
/// ⚠ <b>Код расходуется в любом случае.</b> Принятый шаг записывается
/// <see cref="TwoFactorService.VerifyAsync"/> до всего прочего, и это верно: код одноразов
/// по своей природе, а «отметка не легла, попробуйте тем же кодом» означало бы, что
/// перехваченный код годен дважды.
/// </remarks>
internal sealed class StepUpService(
    TwoFactorService factors,
    LoginCodes codes,
    SecondFactorPolicy methods,
    IStepUpMarker marker,
    ICurrentUser current,
    IAuditWriter audit,
    SenderDbContext db,
    UserRepository users,
    LoginFailures failures,
    TimeProvider clock)
{
    /// <summary>
    /// Проверить код и отметить токен.
    /// </summary>
    /// <exception cref="AppException">
    /// <see cref="ErrorCodes.TwoFactorInvalid"/> — код не подошёл, фактор не включён либо
    /// токен отозвали между распознаванием и проверкой.
    /// </exception>
    /// <returns>Когда отметка поставлена — это же время читает владелец ресурса.</returns>
    /// <remarks>
    /// ⚠ ⚠ <b>Неверный код считается тем же счётчиком, что и вход, и упирается в ту же
    /// блокировку</b> (ревью 2026-09-15, S2, stepup-1). До этого переподтверждение было
    /// единственной дверью с кодом без счёта: держатель угнанной сессии перебирал шесть цифр
    /// (окно ±1 шаг — три годных кода на миллион) со скоростью сети и за минуты получал отметку,
    /// открывающую записи с <c>require_totp</c>. Теперь — <c>auth.lockout_threshold</c> попыток на
    /// <c>auth.lockout_minutes</c>. Цена названа у <see cref="LoginFailures"/>.
    ///
    /// ⚠ <b>Проверка кода и засчёт неудачи — ВНЕ транзакции.</b> Отказ, брошенный внутри неё,
    /// откатил бы и счётчик, и строку журнала (писатель журнала кладёт строку в транзакцию
    /// вызывающего). Образец — переспрос пароля в <see cref="TwoFactorService"/>.
    /// </remarks>
    public async Task<DateTimeOffset> ConfirmAsync(string code, CancellationToken cancellationToken = default)
    {
        var кто = current.Id
            ?? throw new AppException(
                ErrorCodes.Validation, "Действие требует входа", HttpStatusCode.Unauthorized);

        // ⚠ Токен обязан быть назван. Способ входа, не знающий строки выдачи, помечать нечего
        // — и молча считать такой запрос подтверждённым было бы дырой ровно того рода, ради
        // закрытия которой отметку и привязали к токену.
        var токен = current.SessionId
            ?? throw new AppException(
                ErrorCodes.TwoFactorInvalid,
                "Этим способом входа личность не переподтверждают.",
                HttpStatusCode.Forbidden);

        var сейчас = clock.GetUtcNow();

        var человек = await users.FindAsync(кто, cancellationToken)
            ?? throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена", HttpStatusCode.Unauthorized);

        // ⚠ Запертая учётная запись переподтверждения не проходит — и код при этом не
        // проверяется вовсе: иначе блокировка закрывала бы вход и оставляла открытым подбор кода
        // из уже захваченной сессии. Счётчик здесь не растёт (разбор вечной блокировки — у
        // SignInService.CountFailureAsync).
        if (человек.LockedUntil is { } until && until > сейчас)
        {
            await ОтказатьAsync(кто, токен, "locked", cancellationToken);

            throw Отказ();
        }

        // ⚠ Код расходуется и без транзакции: принятый шаг записывается условным обновлением,
        // которое фиксируется само.
        var принят = await factors.VerifyAsync(кто, code, cancellationToken)
            || await codes.VerifyAsync(кто, code, cancellationToken, токен.ToString("N"), Purposes.StepUpCode);

        if (!принят)
        {
            // мутация 2026-09-15: убрал засчёт неудачи (осталась только строка журнала) →
            // покраснел StepUpTests.Подбор_кода_упирается_в_блокировку.
            await failures.RecordAsync(
                человек,
                "step_up_bad_code",
                сейчас,
                cancellationToken,
                Строка(кто, токен, AuditResult.Deny, "invalid_code"));

            throw Отказ();
        }

        var помечен = await db.InTransactionAsync(
            async token =>
            {
                // ⚠ Отметка ставится ТОЛЬКО после принятого кода, и порядок здесь не
                // косметика: пометить сначала означало бы открыть окно, в котором неверный
                // код всё равно даёт свежую отметку.
                if (!await marker.MarkAsync(токен, сейчас, token))
                {
                    return false;
                }

                audit.Write(Строка(кто, токен, AuditResult.Allow, null));
                var fresh = await users.FindAsync(кто, token);
                if (fresh is null || !fresh.IsActive || fresh.LockedUntil > сейчас) throw Отказ();
                fresh.TwoFactorConfiguredAt ??= сейчас;

                await db.SaveChangesAsync(token);

                return true;
            },
            cancellationToken);

        if (!помечен)
        {
            // Гонка с отзывом: токен отозвали между распознаванием и отметкой.
            await ОтказатьAsync(кто, токен, "token_revoked", cancellationToken);

            throw Отказ();
        }

        return сейчас;
    }

    /// <summary>
    /// Записать отказ своим сохранением — до того, как вызывающий поднимет исключение.
    /// </summary>
    /// <remarks>
    /// ⚠ Прежняя редакция писала отказ внутри транзакции и бросала там же — строка уезжала в
    /// откат, и подбор кода на живой сессии следа в журнале не оставлял (в тестах это было не
    /// видно: подменный журнал запоминает вызов, а не зафиксированную строку).
    /// </remarks>
    /// <summary>Код действия действует только в предъявленной сессии и отдельно от входа.</summary>
    public async Task SendEmailAsync(CancellationToken cancellationToken = default)
    {
        var owner = current.Id ?? throw Отказ();
        var session = current.SessionId ?? throw Отказ();
        var user = await users.FindAsync(owner, cancellationToken) ?? throw Отказ();
        if (!user.IsActive || user.LockedUntil > clock.GetUtcNow()) throw Отказ();
        if (!(await methods.AvailableAsync(user, cancellationToken)).Contains(SecondFactorMethods.Email))
        {
            throw new AppException(ErrorCodes.MailNotConfigured, "Почта недоступна. Используйте приложение.", HttpStatusCode.ServiceUnavailable);
        }
        await db.InTransactionAsync(async token =>
        {
            SignInService.EnsureMail(await codes.IssueAsync(user, token, session.ToString("N"), Purposes.StepUpCode));
            await db.SaveChangesAsync(token);
        }, cancellationToken);
    }

    private async Task ОтказатьAsync(Guid кто, Guid токен, string причина, CancellationToken cancellationToken)
    {
        audit.Write(Строка(кто, токен, AuditResult.Deny, причина));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static AuditEntry Строка(Guid кто, Guid токен, AuditResult исход, string? причина) =>
        new(
            IdentityActions.StepUp,
            ActorId: кто,
            Result: исход,
            ResourceType: "session",
            ResourceId: токен,
            Reason: причина);

    private static AppException Отказ() =>
        new(ErrorCodes.TwoFactorInvalid, "Код не подошёл.", HttpStatusCode.Forbidden);
}
