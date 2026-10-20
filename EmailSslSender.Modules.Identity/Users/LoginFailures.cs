using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Naming;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>
/// Попытки доказать, что ты — это ты: занять, вернуть, записать отказ.
/// </summary>
/// <remarks>
/// <b>Почему отдельно от службы сессий.</b> Доказывают личность в нескольких местах: на входе
/// паролем, на входе кодом второго фактора и <b>внутри уже открытой сессии</b> — когда
/// переспрашивают пароль перед снятием защиты.
///
/// ⚠ ⚠ <b>Дверей внутри сессии оказалось три, а не одна</b> (ревью 2026-09-15, S2): кроме
/// переспроса пароля при отключении фактора — <b>смена пароля</b> (текущий пароль) и
/// <b>переподтверждение личности</b> (код). Обе перебирались без счёта. Цена у всех трёх одна
/// и названа: держатель угнанной сессии может намеренно запереть владельца на
/// <c>auth.lockout_minutes</c> и повторять это, пока сессия жива. Навсегда не запирает —
/// запертая учётная запись счётчик не двигает, блокировка кончается сама и обнуляет счёт, — а
/// выход из этого круга один: закрыть угнанную сессию (экран сессий или администратор) и снять
/// блокировку.
///
/// Служба сессий сюда не годится: её зовёт второй фактор, а её саму второй фактор зовёт при
/// входе — получилось бы кольцо. Ровно тот же довод, по которому вынесен отзыв сессий.
///
/// ⚠ ⚠ ⚠ <b>Попытка ЗАНИМАЕТСЯ до проверки, а не засчитывается после неё</b> (ревью
/// 2026-10-20). Прежде блокировка читалась из загруженной строки, потом шла проверка, потом —
/// отдельным обновлением — засчёт неудачи. Между чтением и засчётом помещалось сколько угодно
/// параллельных запросов, и каждый получал свою догадку: порог в десять попыток при сотне
/// одновременных запросов давал сотню догадок. Теперь <see cref="TakeAsync"/> одним условным
/// обновлением и проверяет блокировку, и засчитывает попытку как неудачу; проверять секрет
/// можно только получив <c>true</c>. Удача возвращает попытку (<see cref="ReturnAsync"/>) или
/// обнуляет счёт целиком (успешный вход). Порог перестаёт зависеть от параллельности: догадок
/// ровно столько, сколько разрешено.
///
/// ⚠ ⚠ <b>Счёт живёт до конца блокировки, а не вечно</b> (ревью 2026-10-20). Прежде счётчик
/// после блокировки оставался на пороге, и истёкшую блокировку ставила заново ПЕРВАЯ же
/// неудача: одной неверной попытки раз в <c>auth.lockout_minutes</c> хватало, чтобы владелец не
/// вошёл никогда. Теперь попытка после истёкшей блокировки начинает счёт с единицы — чтобы
/// запереть снова, нужен снова полный порог.
///
/// ⚠ <b>Строка журнала сохраняется своим вызовом.</b> Вызывающий сейчас поднимет отказ, и
/// запись уехала бы вместе с откатом — то есть следа перебора не осталось бы вовсе.
/// </remarks>
internal sealed class LoginFailures(
    SenderDbContext db,
    ISettingsReader settings,
    IAuditWriter audit)
{
    /// <summary>
    /// Занять попытку: если учётная запись не заперта — засчитать попытку неудачей заранее.
    /// </summary>
    /// <returns><c>false</c> — учётная запись заперта; проверять секрет нельзя.</returns>
    /// <remarks>
    /// ⚠ ⚠ <b>Одно условное обновление, и в нём всё:</b> условие «не заперта» в <c>WHERE</c>,
    /// счёт и блокировка в <c>SET</c>. Выигрывают ровно столько одновременных запросов, сколько
    /// осталось до порога; запрос, который довёл счёт до порога, ставит блокировку сам, и
    /// следующие не находят строки.
    ///
    /// ⚠ Заранее засчитанная попытка — неудача, пока не доказано обратное: удача обязана её
    /// вернуть (<see cref="ReturnAsync"/>), иначе каждый успешный шаг входа копил бы счёт.
    ///
    /// ⚠ Вызывается ВНЕ транзакции: занятая попытка обязана пережить отказ, который вызывающий
    /// сейчас бросит. Внутри транзакции откат возвращал бы догадку перебирающему.
    ///
    /// ⚠ По запертой учётной записи счёт не растёт и блокировка не продлевается: иначе
    /// непрерывный перебор держал бы её запертой вечно — оружие против того, кого защищает.
    /// </remarks>
    public async Task<bool> TakeAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var threshold = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutThreshold, cancellationToken));
        var minutes = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutMinutes, cancellationToken));
        DateTimeOffset? until = now.AddMinutes(minutes);

        // ⚠ `locked_until IS NOT NULL` в SET означает «блокировка была и истекла»: действующую
        // отсекает WHERE. Значит счёт начинается заново — это и есть окно, в котором считаются
        // неудачи (разбор вечной блокировки — в примечании к классу).
        var занято = await db.Set<User>()
            .Where(row => row.Id == userId && (row.LockedUntil == null || row.LockedUntil <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        row => row.FailedLoginCount,
                        row => row.LockedUntil == null ? row.FailedLoginCount + 1 : 1)
                    .SetProperty(
                        row => row.LockedUntil,
                        row => (row.LockedUntil == null ? row.FailedLoginCount + 1 : 1) >= threshold
                            ? until
                            : null),
                cancellationToken);

        return занято == 1;
    }

    /// <summary>Вернуть занятую попытку: секрет подошёл, неудачи не было.</summary>
    /// <remarks>
    /// ⚠ Блокировка снимается, если без этой попытки счёт ниже порога. Её могла поставить сама
    /// эта попытка (дошла до порога, а секрет оказался верным) — или соседняя неудачная, но тогда
    /// неудач всё равно меньше порога, и блокировки быть не должно. Правило одно: заперто ровно
    /// тогда, когда неудач набралось на порог.
    /// </remarks>
    public async Task ReturnAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var threshold = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutThreshold, cancellationToken));

        await db.Set<User>()
            .Where(row => row.Id == userId && row.FailedLoginCount > 0)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.FailedLoginCount, row => row.FailedLoginCount - 1)
                    .SetProperty(
                        row => row.LockedUntil,
                        row => row.FailedLoginCount - 1 >= threshold ? row.LockedUntil : null),
                cancellationToken);
    }

    /// <summary>Записать отказ своим сохранением. Счёт уже занят <see cref="TakeAsync"/>.</summary>
    /// <param name="user">Чья неудача.</param>
    /// <param name="reason">Причина — в журнал.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <param name="след">
    /// Своя строка журнала вместо общей «вход отвергнут». Нужна переподтверждению личности: его
    /// отказ разбирают как <c>user.step_up</c> по токену, и вторая строка про то же событие
    /// удвоила бы журнал.
    /// </param>
    public async Task DenyAsync(
        User user,
        string reason,
        CancellationToken cancellationToken = default,
        AuditEntry? след = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        audit.Write(след ?? new AuditEntry(
            IdentityActions.LoggedIn,
            ActorId: user.Id,
            Result: AuditResult.Deny,
            ResourceType: IdentityResources.User,
            ResourceId: user.Id,
            Reason: reason));

        await db.SaveChangesAsync(cancellationToken);
    }
}
