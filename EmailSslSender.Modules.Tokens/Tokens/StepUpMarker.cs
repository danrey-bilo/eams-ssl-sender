using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Tokens.Tokens;

/// <summary>
/// Ставит отметку подтверждения на строку выдачи.
/// </summary>
/// <remarks>
/// ⚠ <b>Только на живой токен, и это проверяется условием обновления, а не чтением.</b>
/// Прочитать-решить-записать здесь означало бы окно, в котором отозванный токен успевает
/// получить свежую отметку, — то есть ровно та гонка, ради закрытия которой отзыв и
/// существует.
///
/// ⚠ <b>Срок токена НЕ проверяется, и это не пропуск.</b> Истёкший токен не проходит
/// распознавание вовсе (<see cref="TokenService"/>), то есть до сюда не доходит; а вот
/// отозвать его могли и после распознавания — потому проверяется именно отзыв.
/// </remarks>
internal sealed class StepUpMarker(SenderDbContext db) : IStepUpMarker
{
    public async Task<bool> MarkAsync(
        Guid tokenId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var помечено = await db.Set<AuthToken>()
            .Where(row => row.Id == tokenId && row.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.StepUpAt, at), cancellationToken);

        return помечено == 1;
    }
}
