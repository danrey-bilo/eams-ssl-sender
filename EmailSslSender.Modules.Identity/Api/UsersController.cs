using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Data;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Учётные записи.
/// </summary>
/// <remarks>
/// Контроллер <c>internal</c>, как и всё в модуле, кроме <c>Contracts/</c> и точки сборки:
/// публичным он потянул бы за собой публичность службы и хранилища. Находить внутренние
/// контроллеры MVC учит провайдер, который добавляет сама точка сборки.
///
/// Проверок прав здесь нет — их делает конвейер по атрибутам. Контроллер остаётся тонким
/// швом HTTP: разобрать запрос, позвать службу, отдать ответ.
/// </remarks>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/users")]
internal sealed class UsersController(
    UserService users,
    UserRepository repository,
    TwoFactorService factors,
    SenderDbContext db) : ControllerBase
{
    /// <summary>Список пользователей.</summary>
    /// <remarks>
    /// ⚠ Отдаёт состояние учётных записей — флаг суперпользователя, блокировку, активацию.
    /// Это карта администраторов системы, поэтому право отдельное и рядовому человеку не
    /// выдаётся. Для «кому написать» появится отдельная выдача, отдающая только имя.
    /// </remarks>
    [HttpGet]
    public async Task<UsersPageOut> List(
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "after")] string? after,
        [FromQuery(Name = "limit")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Запрос собирает репозиторий: условие «живой» пишется в одном месте, и разъехаться
        // ему негде. Курсор — keyset по адресу, разбор в UserCursor.
        var (items, next) = await repository.PageAsync(
            query, UserCursor.Decode(after), limit, cancellationToken);

        var ids = items.Select(user => user.Id).ToArray();
        var confirmed = await db.Set<UserTwoFactor>().Where(row => ids.Contains(row.UserId) && row.ConfirmedAt != null)
            .Select(row => row.UserId).ToListAsync(cancellationToken);
        return new UsersPageOut([.. items.Select(user => UserOut.From(user) with
        {
            AuthenticatorEnabled = confirmed.Contains(user.Id),
        })], next);
    }

    /// <summary>Профиль одного.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserOut>> Get(Guid id, CancellationToken cancellationToken)
    {
        var user = await repository.FindAsync(id, cancellationToken);

        return user is null ? NotFound() : UserOut.From(user);
    }

    /// <summary>Завести учётную запись.</summary>
    [HttpPost]
    public async Task<ActionResult<UserCreatedOut>> Create(
        [FromBody] UserIn body, CancellationToken cancellationToken)
    {
        // ⚠ Только приглашением: пароль человек задаёт сам по ссылке из письма. Пароль,
        // придуманный администратором, знают двое, и второй знает его всегда.
        var (user, invitation) = await users.CreateAsync(
            new UserDraft(body.Email, body.DisplayName, MustChangePassword: false), cancellationToken);

        return Created(
            $"/api/users/{user.Id}",
            new UserCreatedOut(UserOut.From(user), invitation?.Token, invitation?.ExpiresAt));
    }

    /// <summary>Изменить профиль и флаги.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<UserOut> Update(
        Guid id, [FromBody] UserPatchIn body, CancellationToken cancellationToken)
    {
        var user = await users.UpdateAsync(
            id,
            new UserPatch(body.DisplayName, body.MustChangePassword),
            cancellationToken);

        return UserOut.From(user);
    }

    /// <summary>Отключить.</summary>
    /// <remarks>
    /// Отдельным эндпоинтом: отключение человека — не «изменение профиля», и в журнале оно
    /// должно искаться своим именем. Правкой <c>is_active</c>
    /// то же самое больше <b>не</b> делается — разбор у парного действия ниже.
    /// </remarks>
    [HttpPost("{id:guid}/disable")]
    public async Task<UserOut> Disable(Guid id, CancellationToken cancellationToken) =>
        UserOut.From(await users.DisableAsync(id, cancellationToken));

    /// <summary>Включить обратно.</summary>
    /// <remarks>
    /// ⚠ Парное к отключению и тоже отдельным эндпоинтом: возврат доступа не прячется в правке
    /// профиля и ищется в журнале своим именем.
    /// </remarks>
    [HttpPost("{id:guid}/enable")]
    public async Task<UserOut> Enable(Guid id, CancellationToken cancellationToken) =>
        UserOut.From(await users.EnableAsync(id, cancellationToken));

    /// <summary>Удалить.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await users.DeleteAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary>Снять блокировку входа.</summary>
    /// <remarks>
    /// Блокировка снимается и сама по истечении срока; это для того случая, когда ждать
    /// пятнадцать минут незачем.
    /// </remarks>
    [HttpPost("{id:guid}/unlock")]
    public async Task<UserOut> Unlock(Guid id, CancellationToken cancellationToken) =>
        UserOut.From(await users.UnlockAsync(id, cancellationToken));

    /// <summary>
    /// Снять человеку второй фактор.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Единственный законный путь для того, кто потерял и приложение, и коды
    /// восстановления.</b> Если такой человек — единственная учётная запись, его спасает
    /// только консольная команда <c>reset-superadmin-2fa</c>.
    ///
    /// ⚠ <b>Действие опасное:</b> оно <b>снимает защиту</b>, а знай снимающий ещё и чужой
    /// пароль — вход открыт.
    ///
    /// Сессии снимаемого закрываются: фактор снимают, когда телефон потерян или украден.
    /// </remarks>
    [HttpDelete("{id:guid}/two-factor")]
    public async Task<IActionResult> ResetTwoFactor(Guid id, CancellationToken cancellationToken)
    {
        await factors.ResetForAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Закрыть все сессии человека, не отключая его.</summary>
    /// <remarks>
    /// Для случая «угнали ноутбук»: доступ остаётся, украденные токены — нет. До 2026-08-22
    /// единственным способом выгнать человека было отключить его целиком.
    /// </remarks>
    [HttpDelete("{id:guid}/sessions")]
    public async Task<RevokedOut> RevokeSessions(Guid id, CancellationToken cancellationToken) =>
        new(await users.RevokeSessionsAsync(id, cancellationToken));

    /// <summary>
    /// Выдать ссылку сброса пароля.
    /// </summary>
    /// <remarks>
    /// Почта настроена — ссылка уходит письмом, и в ответе <c>reset_token</c> пуст. Почты нет —
    /// ссылка возвращается в ответе один раз, и выдавший её передаёт её человеку сам.
    ///
    /// ⚠ Публичное «забыл пароль» (<c>POST /api/password-reset/request</c>) работает только с
    /// почтой: без неё оно отдало бы ссылку неаутентифицированному, то есть по одному лишь
    /// адресу — чужую учётную запись.
    /// </remarks>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<ResetIssuedOut> ResetPassword(Guid id, CancellationToken cancellationToken)
    {
        var reset = await users.IssuePasswordResetAsync(id, cancellationToken);

        // ⚠ Пустая строка от службы означает «письмо ушло»: показывать ссылку второй раз
        // незачем, а `null` говорит это клиенту прямо (решение 0031).
        return new ResetIssuedOut(
            string.IsNullOrEmpty(reset.Token) ? null : reset.Token, reset.ExpiresAt);
    }

    /// <summary>Выслать приглашение заново.</summary>
    /// <remarks>
    /// Как и сброс пароля: почта настроена — ссылка уходит письмом, нет — возвращается в
    /// ответе один раз. Прежнее приглашение при этом гасится.
    /// </remarks>
    [HttpPost("{id:guid}/invite")]
    public async Task<UserCreatedOut> Invite(Guid id, CancellationToken cancellationToken)
    {
        var invitation = await users.ResendInvitationAsync(id, cancellationToken);
        var user = await repository.FindAsync(id, cancellationToken);

        return new UserCreatedOut(UserOut.From(user!),
            string.IsNullOrEmpty(invitation.Token) ? null : invitation.Token, invitation.ExpiresAt);
    }
}
