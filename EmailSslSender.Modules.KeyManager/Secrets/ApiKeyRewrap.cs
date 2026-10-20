using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Secrets;

/// <summary>
/// Перекладывает обёртку ключа данных ключа API под действующую версию мастер-ключа.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Написан в том же изменении, что и запечатывание.</b> Цель ротации, добавленная позже,
/// означала бы, что <c>rewrap-keys</c> отчитывается об успехе, не тронув ключ API, — и старую
/// версию мастер-ключа нельзя вывести из обращения, а никто об этом не знает.
///
/// ⚠ Трогается только обёртка; ключ API в память не открывается.
///
/// ⚠ <b>Условное обновление по прежней обёртке</b> — довод тот же, что у <c>SystemSecretsRewrap</c>:
/// ключ, перезаданный между чтением и записью, иначе получил бы обёртку от старого ключа данных
/// и перестал бы открываться.
/// </remarks>
internal sealed class ApiKeyRewrap(SenderDbContext db, ISecretEnvelope envelope) : IRewrapTarget
{
    public string Name => "keymanager_state";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                // Строка одна, и курсор у неё один: второй заход начинается после неё.
                if (after is not null)
                {
                    return new RewrapBatch(0, 0, null);
                }

                var строка = await db.Set<KeyManagerState>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, token);

                if (строка is not { DataKey: { } было })
                {
                    return new RewrapBatch(строка is null ? 0 : 1, 0, null);
                }

                var ключ = new WrappedDataKey(было);

                if (!envelope.NeedsRewrap(ключ))
                {
                    return new RewrapBatch(1, 0, null);
                }

                var стало = envelope.Rewrap(ключ).Bytes;

                var затронуто = await db.Set<KeyManagerState>()
                    .Where(с => с.Id == KeyManagerState.SingletonId && с.DataKey == было)
                    .ExecuteUpdateAsync(set => set.SetProperty(с => с.DataKey, стало), token);

                return new RewrapBatch(1, затронуто, null);
            },
            cancellationToken);
}
