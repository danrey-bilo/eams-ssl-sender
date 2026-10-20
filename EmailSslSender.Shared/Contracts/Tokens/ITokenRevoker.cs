namespace EmailSslSender.Shared.Contracts.Tokens;

/// <summary>
/// Погасить сессии человека.
/// </summary>
/// <remarks>
/// <b>Отдельно от <see cref="ITokenIssuer"/>, потому что зовут их разные и по разным
/// поводам.</b> Выдаёт тот, кто впускает; гасит тот, у кого что-то случилось с человеком:
/// сменили пароль, отключили, удалили учётную запись. Один контракт на оба означал бы, что
/// модуль учётных записей, которому нужно только погасить, получает заодно право выписывать.
///
/// ⚠ <b>Все методы зовутся ВНУТРИ транзакции вызывающего и ничего не сохраняют сами.</b>
/// Иначе смена пароля состоялась бы отдельно от гашения сессий: сбой между ними оставил бы
/// прежние токены живыми при новом пароле — то есть ровно то, ради предотвращения чего
/// гашение и делается.
/// </remarks>
public interface ITokenRevoker
{
    /// <summary>Закрыть одну сессию — «выйти отсюда».</summary>
    /// <returns><c>false</c>, если строки нет или она уже закрыта.</returns>
    Task<bool> RevokeAsync(
        Guid tokenId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Закрыть все сессии человека.</summary>
    /// <returns>Сколько закрыто.</returns>
    Task<int> RevokeAllAsync(
        Guid userId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Закрыть всё, кроме одной сессии, — «выгнать остальных».
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Отдельный метод, а не «все, а потом выдать новую».</b> Человек, сменивший
    /// пароль, не должен быть выброшен из системы вместе со всеми: он только что доказал,
    /// что это он.
    /// </remarks>
    Task<int> RevokeOthersAsync(
        Guid userId,
        Guid? keep,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Удалить строки человека насовсем — единственный случай, когда именно удалить.
    /// </summary>
    /// <remarks>
    /// При удалении человека его сессии удаляются, а не гасятся: держать строки того, кого в
    /// системе больше нет, незачем. Не путать с отключением: там сессии гасятся, а строки
    /// остаются до уборки по сроку.
    /// </remarks>
    /// <returns>Сколько удалено.</returns>
    Task<int> DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default);
}
