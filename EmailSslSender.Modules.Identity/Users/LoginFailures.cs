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
/// Неудачная попытка доказать, что ты — это ты.
/// </summary>
/// <remarks>
/// <b>Почему отдельно от службы сессий.</b> Доказывают личность в трёх местах: на входе
/// паролем, на входе кодом второго фактора и <b>внутри уже открытой сессии</b> — когда
/// переспрашивают пароль перед снятием защиты.
///
/// ⚠ ⚠ <b>Дверей внутри сессии оказалось три, а не одна</b> (ревью 2026-09-15, S2): кроме
/// переспроса пароля при отключении фактора — <b>смена пароля</b> (текущий пароль) и
/// <b>переподтверждение личности</b> (код). Обе перебирались без счёта. Цена у всех трёх одна
/// и названа: держатель угнанной сессии может намеренно запереть владельца на
/// <c>auth.lockout_minutes</c> и повторять это, пока сессия жива. Навсегда не запирает —
/// запертая учётная запись счётчик не двигает, блокировка кончается сама, — а выход из этого
/// круга один: закрыть угнанную сессию (экран сессий или администратор) и снять блокировку. Первые два считались и писались в журнал,
/// третье не делало ни того, ни другого: угнанная вкладка перебирала пароль владельца через
/// отключение фактора бесшумно и без ограничений. Найдено разбором готовности 2026-08-24.
///
/// Служба сессий сюда не годится: её зовёт второй фактор, а её саму второй фактор зовёт при
/// входе — получилось бы кольцо. Ровно тот же довод, по которому вынесен отзыв сессий.
///
/// ⚠ <b>Счётчик двигается условным обновлением.</b> Параллельные попытки перебора — это
/// ровно тот случай, ради которого счётчик существует, и «прочитать-решить-записать»
/// перетирало бы их друг о друга, так и не дойдя до порога.
///
/// ⚠ <b>Строка журнала сохраняется своим вызовом.</b> Вызывающий сейчас поднимет отказ, и
/// запись уехала бы вместе с откатом — то есть следа перебора не осталось бы вовсе.
/// </remarks>
internal sealed class LoginFailures(
    SenderDbContext db,
    ISettingsReader settings,
    IAuditWriter audit)
{
    /// <summary>Засчитать неудачу, заблокировать при переборе и записать след.</summary>
    /// <param name="user">Чья неудача.</param>
    /// <param name="reason">Причина — в журнал и в уведомление о блокировке.</param>
    /// <param name="now">Время попытки.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <param name="след">
    /// Своя строка журнала вместо общей «вход отвергнут». Нужна переподтверждению личности: его
    /// отказ разбирают как <c>user.step_up</c> по токену, и вторая строка про то же событие
    /// удвоила бы журнал.
    /// </param>
    public async Task RecordAsync(
        User user,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        AuditEntry? след = null)
    {
        var threshold = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutThreshold, cancellationToken));
        var minutes = Math.Max(1, await settings.GetAsync(IdentitySettings.LockoutMinutes, cancellationToken));

        await db.Set<User>()
            .Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.FailedLoginCount, row => row.FailedLoginCount + 1)
                    .SetProperty(
                        row => row.LockedUntil,
                        row => row.FailedLoginCount + 1 >= threshold ? now.AddMinutes(minutes) : row.LockedUntil),
                cancellationToken);

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
