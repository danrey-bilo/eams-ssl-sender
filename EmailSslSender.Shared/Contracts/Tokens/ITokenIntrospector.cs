using EmailSslSender.Shared.Contracts.Audit;

namespace EmailSslSender.Shared.Contracts.Tokens;

/// <summary>
/// Что известно о предъявленном секрете, если он жив.
/// </summary>
/// <param name="UserId">Чей.</param>
/// <param name="TokenId">
/// Строка выдачи — то, что человек видит в списке «где я вошёл». Ею же помечается сессия
/// в личности запроса.
/// </param>
/// <param name="ActorType">Человек или система.</param>
/// <param name="StepUpAt">
/// Когда личность за этим токеном подтверждали вторым фактором. <c>null</c> — не подтверждали.
/// Свойство <b>токена</b>, а не человека; ставит отметку <see cref="IStepUpMarker"/>.
/// </param>
public sealed record TokenIdentity(
    Guid UserId,
    Guid TokenId,
    AuditActorType ActorType,
    DateTimeOffset? StepUpAt = null);

/// <summary>
/// Проверить предъявленный секрет: жив ли он и чей.
/// </summary>
/// <remarks>
/// <b>Граница проходит по вопросу, а не по файлу:</b>
///
/// <list type="table">
/// <item><term>«секрет жив, и чей он»</term><description>отвечает модуль токенов</description></item>
/// <item><term>«а этому человеку можно войти»</term><description>отвечает модуль учётных записей</description></item>
/// <item><term>собрать личность из двух ответов</term><description>модуль учётных записей: ограничения личности его</description></item>
/// </list>
///
/// ⚠ <b>Почему не наоборот.</b> Распознавание одним соединением таблиц секретов и людей
/// читало бы чужие таблицы — у одного из двух модулей, всё равно у какого.
///
/// ⚠ <b>Цена названа честно: одно лишнее обращение к базе на каждом запросе с токеном.</b>
/// Это самый горячий путь системы. Дешевле не выходит, и делать вид, что переезд бесплатен,
/// было бы неправдой.
///
/// <b>Два разных «нет».</b> <c>null</c> означает «это не наш формат»: пусть попробует
/// следующий распознаватель. <b>Исключение</b> означает «наш, но негодный» — просрочен,
/// отозван, предъявлен повторно. Разница принципиальная: во втором случае человеку надо
/// сказать, <b>почему</b> его сессия кончилась: «вы вышли» и «обнаружена кража токена» —
/// это разница между «ладно» и «звоните в безопасность».
/// </remarks>
public interface ITokenIntrospector
{
    /// <summary>Наш ли это формат вообще. Дешёвая проверка без обращения к базе.</summary>
    /// <remarks>
    /// ⚠ Нужна затем, чтобы цепочка распознавателей шла дальше, а не обрывалась исключением
    /// на чужом заголовке: заголовок <c>Bearer</c> общий для всякого способа входа.
    /// </remarks>
    bool Recognises(string presented);

    /// <summary>
    /// Проверить секрет и сказать, чей он.
    /// </summary>
    /// <returns><c>null</c> — не наш формат.</returns>
    /// <exception cref="Errors.AppException">Наш формат, но секрет негоден.</exception>
    Task<TokenIdentity?> IntrospectAsync(string presented, CancellationToken cancellationToken = default);
}
