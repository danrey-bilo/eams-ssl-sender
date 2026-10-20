using System.Net;

namespace EmailSslSender.Shared.Contracts.Tokens;

/// <summary>Откуда пришёл запрос — для списка «где я вошёл».</summary>
/// <param name="Address">Адрес.</param>
/// <param name="UserAgent">Чем пришли.</param>
public sealed record RequestOrigin(IPAddress? Address, string? UserAgent);

/// <summary>
/// Выданная пара: короткий токен доступа и долгий токен обновления.
/// </summary>
/// <remarks>
/// ⚠ <b>Токены непрозрачные, а не JWT.</b> Подпись избавила бы от обращения к базе при
/// проверке — но обращаться всё равно приходится: человека могли отключить, токен отозвать,
/// секрет украсть. Раз обращение к базе всё равно есть, подпись не покупает ничего, а стоит
/// управления ключом и его ротацией.
/// </remarks>
/// <param name="Access">Короткий. Предъявляется на каждом запросе.</param>
/// <param name="AccessExpiresAt">Когда кончится короткий.</param>
/// <param name="Refresh">Долгий. Обменивается на новую пару и гасится при этом.</param>
/// <param name="RefreshExpiresAt">Когда кончится долгий.</param>
/// <param name="TokenId">
/// Строка, к которой пара относится, — то, что человек видит в списке «где я вошёл».
/// Прежде звалась идентификатором сессии.
/// </param>
public sealed record TokenPair(
    string Access,
    DateTimeOffset AccessExpiresAt,
    string Refresh,
    DateTimeOffset RefreshExpiresAt,
    Guid TokenId);

/// <summary>Что выдать и кому.</summary>
/// <param name="UserId">Кому.</param>
/// <param name="Kind">
/// Вид из <see cref="TokenKinds"/>. Строкой, а не перечислением: значение ложится в колонку
/// и в <c>CHECK</c>-ограничение, и перечисление означало бы два списка — в коде и в базе.
/// </param>
/// <param name="Name">Как сессия называется в списке «где я вошёл»: агент браузера.</param>
/// <param name="Address">Откуда пришли.</param>
/// <param name="StepUpAt">
/// Когда подтверждён второй фактор этого входа. Вход по коду ставит время кода — первые минуты
/// сессии опасные действия не переспрашивают; вход без кода оставляет <c>null</c>.
/// </param>
public sealed record TokenIssueRequest(
    Guid UserId,
    string Kind,
    string? Name = null,
    System.Net.IPAddress? Address = null,
    DateTimeOffset? StepUpAt = null);

/// <summary>
/// Выдать предъявляемый токен от имени человека.
/// </summary>
/// <remarks>
/// <b>Место ротации, место обнаружения кражи и список «где я вошёл» — одни на систему.</b>
///
/// ⚠ <b>Контракт НЕ проверяет, можно ли этому человеку войти.</b> Он выдаёт токены тому,
/// про кого вызывающий уже решил, что пускает. Пароль, блокировка после неудач, второй
/// — это учётные записи, и знать про них выдаче незачем: тогда пришлось бы затащить сюда
/// хеши паролей.
///
/// Разрыв проходит ровно здесь: <b>«проверил» — у соседа, «выдал» — у нас</b>.
/// </remarks>
public interface ITokenIssuer
{
    /// <summary>
    /// Выдать пару человеку, про которого вызывающий уже решил, что пускает.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Зовётся ВНУТРИ транзакции вызывающего</b> и ничего не сохраняет сам. Иначе вход
    /// состоялся бы отдельно от обнуления счётчика неудач и от строки журнала — то есть при
    /// сбое между ними человек получил бы токены, а система осталась бы при мнении, что он
    /// не входил.
    /// </remarks>
    /// <param name="request">Кому, чего и с какими приметами.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<TokenPair> IssueAsync(TokenIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Обменять токен обновления на новую пару.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Предъявленный повторно потраченный токен — это обнаружение кражи</b>, а не
    /// ошибка: значит, токен есть у двоих, и кто из них вор, неизвестно. Строка закрывается
    /// целиком, в журнал уходит отказ.
    /// </remarks>
    /// <exception cref="Errors.AppException">Токен не найден, просрочен, отозван или потрачен.</exception>
    Task<TokenPair> RotateAsync(string refresh, RequestOrigin origin, CancellationToken cancellationToken = default);
}
