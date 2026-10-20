using EmailSslSender.Modules.Tokens.Contracts;
using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Modules.Tokens.Naming;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Tokens.Tokens;

/// <summary>
/// Закрытие токенов — одно место на весь модуль.
/// </summary>
/// <remarks>
/// <b>Почему отдельно от службы токенов.</b> Закрывать нужно троим: самой службе (выход,
/// кража), учётным записям (сменили пароль, удалили) и второму фактору (включили — значит
/// прежним сессиям не место). Служба выдачи при этом сама зависит
/// от вызывающего; позови вызывающий её обратно — получится кольцо, и контейнер откажется
/// его собирать. Отзыв не знает ни о входе, ни о факторе; вынесенный, он доступен всем и не
/// тянет никого.
///
/// ⚠ <b>Гашение условным обновлением, а не присваиванием.</b> Обновление идёт с условием
/// «ещё не отозван», и потому не может затереть чужую причину. Это не мелочь: обнаружение
/// кражи закрывает строку причиной «кража», и если человек в ту же секунду нажал «выйти»,
/// прежняя редакция переписывала причину на «вышел» — единственный след кражи исчезал, а на
/// экране вместо «сообщите администратору» человек видел «вы вышли». Найдено разбором
/// 2026-08-22.
///
/// ⚠ <b>Требует открытой транзакции вызывающего</b>, и это проверяется. Гашение секретов
/// выполняется оператором немедленно, а строки журнала уезжают на сохранении: без общей
/// транзакции доступ пропал бы, а следа не осталось — прямое нарушение правила 15.
/// </remarks>
internal sealed class TokenRevoker(SenderDbContext db, IAuditWriter audit, TimeProvider clock) : ITokenRevoker
{
    /// <summary>
    /// Закрыть одну строку. <c>false</c> — её нет или она уже закрыта.
    /// </summary>
    /// <remarks>
    /// Строка перечитывается <b>внутри</b> вызова, а не приходит снаружи: обёртка транзакций
    /// очищает трекер перед каждой попыткой, и экземпляр, прочитанный до неё, правился бы
    /// вхолостую. Так уже случалось с активацией учётной записи.
    /// </remarks>
    public async Task<bool> RevokeAsync(
        Guid tokenId, string reason, CancellationToken cancellationToken = default)
    {
        RequireTransaction();

        var now = clock.GetUtcNow();

        var owner = await db.Set<AuthToken>()
            .Where(row => row.Id == tokenId && row.RevokedAt == null)
            .Select(row => (Guid?)row.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is not { } userId)
        {
            return false;
        }

        var affected = await db.Set<AuthToken>()
            .Where(row => row.Id == tokenId && row.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.RevokedAt, now)
                    .SetProperty(row => row.RevokedReason, reason),
                cancellationToken);

        if (affected == 0)
        {
            // Опередили: причина уже стоит, и она сильнее нашей.
            return false;
        }

        Record(tokenId, userId, reason);

        await ExpireSecretsAsync([tokenId], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public Task<int> RevokeAllAsync(
        Guid userId, string reason, CancellationToken cancellationToken = default) =>
        ManyAsync(userId, reason, keep: null, cancellationToken);

    /// <summary>
    /// Закрыть всё, кроме одной строки.
    /// </summary>
    /// <remarks>
    /// Нужно смене пароля и включению второго фактора: выгнать со всех устройств, кроме того,
    /// за которым человек сейчас сидит. Выгонять и его — значит наказывать за правильное
    /// поведение: действие заканчивалось бы экраном входа, и его перестали бы делать.
    /// </remarks>
    public Task<int> RevokeOthersAsync(
        Guid userId,
        Guid? keep,
        string reason,
        CancellationToken cancellationToken = default) =>
        ManyAsync(userId, reason, keep, cancellationToken);


    /// <summary>
    /// Удалить строки человека насовсем.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Единственный законный повод — удаление человека</b> (решение владельца
    /// 2026-08-25). Держать строки того, кого в системе больше нет, незачем: след действия
    /// живёт в журнале, а он append-only.
    ///
    /// ⚠ <b>Строки журнала здесь НЕ пишутся, и это не упущение.</b> Событие «удалён человек»
    /// пишет тот, кто его удаляет; наша строка «закрыто восемь токенов» рядом с ней ничего
    /// не добавляет, а в выдаче журнала выглядит как восемь отдельных происшествий.
    ///
    /// Секреты уезжают каскадом — внешний ключ здесь свой, обе таблицы наши.
    /// </remarks>
    public async Task<int> DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        RequireTransaction();

        return await db.Set<AuthToken>()
            .Where(row => row.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // --- внутреннее -----------------------------------------------------------------------

    /// <remarks>
    /// Одним проходом, а не по строке за раз: прежняя редакция звала отзыв в цикле, и каждый
    /// виток делал своё обновление и своё сохранение. У человека с десятком устройств
    /// отключение стоило двадцати обращений к базе вместо трёх.
    /// </remarks>
    private async Task<int> ManyAsync(
        Guid userId, string reason, Guid? keep, CancellationToken cancellationToken)
    {
        RequireTransaction();

        var now = clock.GetUtcNow();

        // ⚠ Только живые и не истёкшие. Прежняя редакция закрывала и те, у которых срок уже
        // вышел: счётчик «закрыто восемь» врал, а журнал наполнялся строками об отзыве
        // сессий, которыми и так нельзя было воспользоваться.
        var doomed = await db.Set<AuthToken>()
            .Where(row => row.UserId == userId
                && row.RevokedAt == null
                && row.ExpiresAt > now
                && (keep == null || row.Id != keep))
            .Select(row => row.Id)
            .ToListAsync(cancellationToken);

        if (doomed.Count == 0)
        {
            return 0;
        }

        var affected = await db.Set<AuthToken>()
            .Where(row => doomed.Contains(row.Id) && row.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.RevokedAt, now)
                    .SetProperty(row => row.RevokedReason, reason),
                cancellationToken);

        if (affected == 0)
        {
            return 0;
        }

        foreach (var tokenId in doomed)
        {
            Record(tokenId, userId, reason);
        }

        await ExpireSecretsAsync(doomed, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return affected;
    }


    /// <summary>Строка журнала об отзыве. Кража — отдельным событием и отказом.</summary>
    private void Record(Guid tokenId, Guid userId, string reason) =>
        audit.Write(new AuditEntry(
            reason == RevokeReasons.Stolen ? TokenActions.Reuse : TokenActions.Revoked,
            ActorId: userId,
            Result: reason == RevokeReasons.Stolen ? AuditResult.Deny : AuditResult.Allow,
            ResourceType: TokenResources.Session,
            ResourceId: tokenId,
            Reason: reason));

    /// <summary>Погасить секреты закрытых строк: срок «сейчас».</summary>
    private Task ExpireSecretsAsync(
        IReadOnlyCollection<Guid> tokenIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Set<AuthTokenSecret>()
            .Where(row => tokenIds.Contains(row.TokenId) && row.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAt, now), cancellationToken);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "Отзыв токена обязан идти внутри транзакции: секреты гасятся оператором немедленно, "
            + "а строка журнала уезжает на сохранении. Без общей транзакции доступ пропал бы, "
            + "а следа не осталось.");
    }
}
