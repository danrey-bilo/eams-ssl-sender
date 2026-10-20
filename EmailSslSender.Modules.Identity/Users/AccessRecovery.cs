using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Shared.Data;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>
/// Вернуть доступ к учётной записи из консоли: команда <c>reset-access</c>.
/// </summary>
/// <remarks>
/// <b>Для случая «потерян и телефон, и пароль, а войти больше некому».</b> По отдельности то же
/// делают <c>set-superadmin-password</c> и <c>reset-superadmin-2fa</c>; здесь — разом и одной
/// транзакцией: новый пароль, снятый второй фактор (следующий вход заново подключает
/// аутентификатор), снятая блокировка, закрытые сессии, погашенные ссылки, расписки и коды.
///
/// ⚠ ⚠ <b>Одна транзакция, а не две команды подряд.</b> Сбой между ними оставил бы учётную запись
/// с новым паролем и прежним аутентификатором — то есть по-прежнему запертой, но уже с паролем,
/// который знает тот, кто стоит у консоли. Обе службы уважают открытую транзакцию вызывающего.
///
/// ⚠ <b>Отключённую учётную запись команда НЕ включает.</b> Включение — решение администратора с
/// записью в журнале, а не побочный эффект восстановления; итог говорит об этом прямо.
///
/// Пропуском служит доступ к машине, как у <c>create-superadmin</c>: у того, кто попал в
/// контейнер, и так есть база целиком.
/// </remarks>
internal sealed class AccessRecovery(SenderDbContext db, UserService people, TwoFactorService factors)
{
    /// <summary>Сменить пароль, снять фактор и блокировку, закрыть сессии — разом.</summary>
    /// <exception cref="Shared.Errors.AppException">
    /// <c>NOT_FOUND</c> — живой учётной записи с таким адресом нет; <c>PASSWORD_POLICY_VIOLATION</c>
    /// — пароль не по правилам. В обоих случаях не меняется ничего.
    /// </exception>
    public Task<AccessResetResult> ResetAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                // ⚠ Пароль — первым: он же проверяет политику, и негодный пароль откатывает всё,
                // не тронув фактора.
                var (user, sessions) = await people.SetConsolePasswordAsync(email, password, token);
                var hadFactor = await factors.ResetSuperuserForConsoleAsync(user.Id, token);

                return new AccessResetResult(user.Id, user.Email, user.IsActive, hadFactor, sessions);
            },
            cancellationToken);
}
