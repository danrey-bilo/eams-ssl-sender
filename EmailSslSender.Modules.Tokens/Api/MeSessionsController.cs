using System.Text.Json.Serialization;
using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Modules.Tokens.Tokens;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Tokens.Api;

/// <summary>Сессия в списке «где я вошёл».</summary>
/// <remarks>
/// ⚠ <b>Поле по-прежнему зовётся <c>user_agent</c>, хотя в базе колонка теперь <c>name</c>.</b>
/// Имя поля в ответе — часть контракта наружу, а версий API нет (решение 0004): переименование
/// сломало бы веб и настольный клиент одновременно. Приводить их в согласие можно только на
/// этапе 11, где контракт замерзает, и одним движением с обоими клиентами.
/// </remarks>
internal sealed record SessionOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("current")] bool Current);

/// <summary>
/// Свои входы: «где я вошёл» и «закрыть отсюда».
/// </summary>
/// <remarks>
/// ⚠ <b>Пути те же, что были у соседа, — владелец сменился, адрес нет.</b> Версий API нет
/// (решение 0004), и всякая правка пути ломает оба клиента разом. Переезд владельца при этом
/// обязателен: строки теперь наши, и оболочка в чужом модуле означала бы, что он читает нашу
/// таблицу.
///
/// ⚠ <b>Приставка <c>/api/me</c> — решение
/// [0015](../../docs/decisions/0015-всё-своё-под-приставкой-me.md):</b> всё, что человек
/// делает над собой, живёт под ней, и чужого идентификатора в таких путях не бывает вовсе.
/// Держит это сторож <c>EndpointGuards.Под_приставкой_me_нет_чужого_идентификатора</c>.
/// </remarks>
[ApiController]
[Route("api/me")]
internal sealed class MeSessionsController(
    TokenService tokens,
    ITokenRevoker revoker,
    SenderDbContext db,
    ICurrentUser actor) : ControllerBase
{
    /// <summary>Где я вошёл.</summary>
    [HttpGet("sessions")]
    public async Task<IReadOnlyList<SessionOut>> Sessions(CancellationToken cancellationToken)
    {
        var live = await tokens.ListAsync(actor.Id!.Value, TokenKinds.Session, cancellationToken);

        return [.. live.Select(row => new SessionOut(
            row.Id,
            row.Name,
            row.Address?.ToString(),
            row.CreatedAt,
            row.LastSeenAt,
            row.ExpiresAt,
            row.Id == actor.SessionId))];
    }

    /// <summary>Закрыть один из своих входов.</summary>
    /// <remarks>
    /// ⚠ <b>Только свой: владелец берётся из личности, а не из запроса.</b> Иначе это был бы
    /// способ выгонять кого угодно, зная лишь номер его сессии.
    ///
    /// Владелец проверяется отдельным чтением: сам отзыв про владельца не знает — он про
    /// строку.
    /// </remarks>
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var mine = await db.Set<AuthToken>()
            .AnyAsync(row => row.Id == id && row.UserId == actor.Id!.Value, cancellationToken);

        if (!mine)
        {
            // ⚠ «Не найдено», а не «не ваше». Отличать одно от другого нельзя: разница сама
            // по себе отвечает на вопрос «а существует ли сессия с таким номером».
            throw AppException.NotFound("Сессия не найдена");
        }

        await db.InTransactionAsync(
            (token) => revoker.RevokeAsync(id, RevokeReasons.Revoked, token),
            cancellationToken);

        return NoContent();
    }
}
