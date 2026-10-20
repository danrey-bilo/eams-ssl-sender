using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Secrets;

/// <summary>
/// Ключ API Key Manager Plus: запечатать, открыть, снять.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Испорченный блоб даёт ОТКАЗ, а не «ключа нет»</b> — довод тот же, что у
/// <c>SystemSecrets</c> почты: молчаливое «не задан» увело бы разбор в «Key Manager Plus не
/// принимает ключ», а это авария хранилища или подмена.
///
/// ⚠ Не зовёт <c>SaveChangesAsync</c>: фиксацией распоряжается вызывающий — вместе со строкой
/// журнала (правило 15).
/// </remarks>
internal sealed class ApiKeyStore(
    SenderDbContext db, ISecretEnvelope envelope, ICurrentUser current, TimeProvider clock)
{
    /// <summary>
    /// Контекст AEAD ключа API.
    /// </summary>
    /// <remarks>
    /// ⚠ Входит в шифротекст: после первой установки его менять нельзя, иначе прежний ключ
    /// перестанет открываться.
    /// </remarks>
    internal const string Контекст = "sender:keymanager:api-key";

    /// <summary>Открыть ключ API или <c>null</c>, если он не задан.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Ключ есть, но не открывается.
    /// </exception>
    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<KeyManagerState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, cancellationToken);

        return строка is { ApiKey: { } запечатано, DataKey: { } ключ }
            ? envelope.OpenText(new WrappedDataKey(ключ), new SealedSecret(запечатано), Контекст)
            : null;
    }

    /// <summary>Задан ли ключ — без его открытия.</summary>
    public Task<bool> HasAsync(CancellationToken cancellationToken = default) =>
        db.Set<KeyManagerState>()
            .AnyAsync(с => с.Id == KeyManagerState.SingletonId && с.ApiKey != null, cancellationToken);

    /// <summary>Задать ключ; пустая строка снимает его.</summary>
    public async Task PutAsync(string? value, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<KeyManagerState>()
            .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, cancellationToken);

        if (строка is null)
        {
            строка = new KeyManagerState();
            db.Set<KeyManagerState>().Add(строка);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            строка.ApiKey = null;
            строка.DataKey = null;
        }
        else
        {
            var ключ = envelope.CreateDataKey();
            строка.ApiKey = envelope.SealText(ключ, value.Trim(), Контекст).Bytes;
            строка.DataKey = ключ.Bytes;
        }

        строка.UpdatedAt = clock.GetUtcNow();
        строка.UpdatedBy = current.Id;
    }
}
