using System.Globalization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Authz;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Secrets;

/// <summary>Ключ API Key Manager Plus, привязанный к адресу (<see cref="BoundSecret"/>).</summary>
/// <remarks>
/// ⚠ Испорченный блоб — отказ, а не «ключа нет». Не сохраняет: фиксирует вызывающий вместе с журналом
/// (кроме <see cref="BindLegacyAsync"/>, условным обновлением).
/// </remarks>
internal sealed class ApiKeyStore(
    AppDbContext db, ISecretEnvelope envelope, ICurrentUser current, TimeProvider clock, ILogger<ApiKeyStore> log)
{
    /// <summary>⚠ Контекст AEAD входит в шифротекст — не менять.</summary>
    internal const string Контекст = "sender:keymanager:api-key";

    /// <summary>Адрес в нормальном виде: схема, хост строчными, порт, путь без «/».</summary>
    /// <remarks>Ключ, привязанный в 2.4 вместе с « sha256=…», не совпадёт — вводится заново.</remarks>
    internal static string Target(string? baseUrl)
    {
        var адрес = baseUrl?.Trim() ?? string.Empty;

        if (Uri.TryCreate(адрес, UriKind.Absolute, out var uri))
        {
            адрес = string.Create(
                CultureInfo.InvariantCulture,
                $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}");
        }

        return адрес;
    }

    /// <summary>Ключ API для адреса или <c>null</c>, если не задан.</summary>
    /// <remarks>⚠ Ключ без привязки не открывается: его привязывает только <c>migrate</c> или повторный ввод.</remarks>
    /// <exception cref="KeyManagerException">Ключ вводили для другого адреса.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Ключ есть, но не открывается.</exception>
    public async Task<string?> GetForAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var строка = await db.Set<KeyManagerState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, cancellationToken);

        if (строка is not { ApiKey: { } запечатано, DataKey: { } ключ })
        {
            return null;
        }

        var открыто = envelope.OpenText(new WrappedDataKey(ключ), new SealedSecret(запечатано), Контекст);
        var привязка = BoundSecret.Unpack(открыто);

        if (привязка is null)
        {
            throw new KeyManagerException(
                "Ключ API задан до привязки к адресу. Выполните docker compose run --rm migrate — "
                + "команда привяжет его к текущему адресу Key Manager Plus — или введите ключ заново.");
        }

        if (!привязка.IsFor(target))
        {
            throw new KeyManagerException(
                "Ключ API вводили для другого адреса Key Manager Plus: адрес сменился после ввода "
                + "ключа. Введите ключ API заново — он уходит только туда, для чего его ввели.");
        }

        return привязка.Secret;
    }

    /// <summary>Привязать ключ 1.x к <paramref name="target"/>; зовёт только <c>migrate</c>.</summary>
    /// <remarks>Условное обновление: ключ, заданный заново между чтением и записью, не затирается.</remarks>
    /// <returns><c>true</c> — привязан сейчас.</returns>
    public async Task<bool> BindLegacyAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var строка = await db.Set<KeyManagerState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Id == KeyManagerState.SingletonId, cancellationToken);

        if (строка is not { ApiKey: { } запечатано, DataKey: { } ключ })
        {
            return false;
        }

        var открыто = envelope.OpenText(new WrappedDataKey(ключ), new SealedSecret(запечатано), Контекст);

        if (BoundSecret.Unpack(открыто) is not null)
        {
            return false;
        }

        var перепечатано = envelope.SealText(
            new WrappedDataKey(ключ), new BoundSecret(target, открыто).Pack(), Контекст).Bytes;

        var изменено = await db.Set<KeyManagerState>()
            .Where(с => с.Id == KeyManagerState.SingletonId && с.ApiKey == запечатано)
            .ExecuteUpdateAsync(set => set.SetProperty(с => с.ApiKey, перепечатано), cancellationToken);

        // Без адреса и ключа: только факт.
        log.LogWarning("keymanager_api_key_bound_by_migrate");

        return изменено == 1;
    }

    /// <summary>Задан ли ключ — без его открытия.</summary>
    public Task<bool> HasAsync(CancellationToken cancellationToken = default) =>
        db.Set<KeyManagerState>()
            .AnyAsync(с => с.Id == KeyManagerState.SingletonId && с.ApiKey != null, cancellationToken);

    /// <summary>Задать ключ для адреса из <see cref="Target"/>; пустая строка снимает его.</summary>
    public async Task PutAsync(string? value, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

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
            строка.ApiKey = envelope.SealText(ключ, new BoundSecret(target, value.Trim()).Pack(), Контекст).Bytes;
            строка.DataKey = ключ.Bytes;
        }

        строка.UpdatedAt = clock.GetUtcNow();
        строка.UpdatedBy = current.Id;
    }
}
