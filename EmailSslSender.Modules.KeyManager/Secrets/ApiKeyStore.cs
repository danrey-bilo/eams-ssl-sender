using System.Globalization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Authz;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Secrets;

/// <summary>
/// Ключ API Key Manager Plus: запечатать вместе с адресом, открыть для адреса, снять.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Испорченный блоб даёт ОТКАЗ, а не «ключа нет»</b> — довод тот же, что у
/// <c>SystemSecrets</c> почты: молчаливое «не задан» увело бы разбор в «Key Manager Plus не
/// принимает ключ», а это авария хранилища или подмена.
///
/// ⚠ ⚠ <b>Ключ привязан к адресу, для которого его ввели</b> (<see cref="BoundSecret"/>). Открыть его
/// можно только для того же адреса — сменивший адрес вводит ключ заново. Иначе перехваченная на
/// время учётная запись направила бы ключ (а через него все закрытые ключи установки) на свой
/// сервер одной проверкой связи.
///
/// ⚠ Не зовёт <c>SaveChangesAsync</c>: фиксацией распоряжается вызывающий — вместе со строкой
/// журнала (правило 15). Исключение — перепечатка старого ключа при первом использовании
/// (<see cref="GetForAsync"/>): она пишет сама, условным обновлением.
/// </remarks>
internal sealed class ApiKeyStore(
    AppDbContext db, ISecretEnvelope envelope, ICurrentUser current, TimeProvider clock, ILogger<ApiKeyStore> log)
{
    /// <summary>
    /// Контекст AEAD ключа API.
    /// </summary>
    /// <remarks>
    /// ⚠ Входит в шифротекст: после первой установки его менять нельзя, иначе прежний ключ
    /// перестанет открываться.
    /// </remarks>
    internal const string Контекст = "sender:keymanager:api-key";

    /// <summary>
    /// Куда уходит ключ: адрес сервера в нормальном виде.
    /// </summary>
    /// <remarks>
    /// ⚠ Нормальный вид — схема, имя хоста строчными, порт (и умолчательный), путь без
    /// завершающей косой черты: «https://KMP:443/» и «https://kmp» — один адрес, и переписанный
    /// по-другому адрес не должен требовать ключа заново. Строка запроса не входит: клиент её
    /// не использует.
    ///
    /// ⚠ ⚠ До 2.5 к адресу дописывался закреплённый отпечаток (« sha256=…»); ключ без отпечатка
    /// привязан к одному адресу — такая привязка открывается и сейчас. Ключ, введённый при
    /// закреплённом отпечатке, после обновления вводится заново.
    /// </remarks>
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

    /// <summary>
    /// Открыть ключ API для адреса или <c>null</c>, если он не задан.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Старый ключ без привязки не открывается</b> (ревью безопасности 2026-10-20): его
    /// привязывает к текущему адресу команда <c>migrate</c> (<see cref="BindLegacyAsync"/>), а
    /// иначе — повторный ввод. Прежнее «доверие с первого раза» отдавало ключ тому, кто первым
    /// подменил адрес из захваченной сессии.
    /// </remarks>
    /// <param name="target">Адрес из <see cref="Target"/> по текущим настройкам.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="KeyManagerException">Ключ вводили для другого адреса.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Ключ есть, но не открывается.
    /// </exception>
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

    /// <summary>
    /// Привязать ключ без привязки к адресу <paramref name="target"/>. Зовёт только <c>migrate</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Тем же ключом данных и условным обновлением по прежнему запечатанному: ротация
    /// перекладывает обёртку, а не ключ данных, а ключ, заданный заново между чтением и
    /// записью, не затирается.
    /// </remarks>
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

        // ⚠ Без адреса и тем более без ключа: строка о факте, адрес виден в настройках.
        log.LogWarning("keymanager_api_key_bound_by_migrate");

        return изменено == 1;
    }

    /// <summary>Задан ли ключ — без его открытия.</summary>
    public Task<bool> HasAsync(CancellationToken cancellationToken = default) =>
        db.Set<KeyManagerState>()
            .AnyAsync(с => с.Id == KeyManagerState.SingletonId && с.ApiKey != null, cancellationToken);

    /// <summary>Задать ключ для адреса; пустая строка снимает его.</summary>
    /// <param name="value">Ключ API.</param>
    /// <param name="target">Адрес из <see cref="Target"/> по настройкам на момент ввода.</param>
    /// <param name="cancellationToken">Отмена.</param>
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
