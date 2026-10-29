using Microsoft.EntityFrameworkCore;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Движок прав проекта: ролей нет, каждая активная учётная запись — администратор.</summary>
/// <remarks>⚠ Отключённая учётная запись не может ничего — движок спрашивают и не о вошедших.</remarks>
internal sealed class AdministratorPermissions(IAccounts accounts, IEnumerable<IPermissionProvider> providers)
    : IPermissionChecker
{
    public async Task<bool> CanAsync(
        Guid userId,
        string permission,
        string? resourceType = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default) =>
        (await accounts.FlagsAsync(userId, cancellationToken))?.IsActive == true;

    public async Task<IReadOnlyList<string>> EffectiveAsync(
        Guid userId,
        string? resourceType = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default) =>
        (await accounts.FlagsAsync(userId, cancellationToken))?.IsActive == true ? Все() : [];

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> EffectiveManyAsync(
        Guid userId,
        string resourceType,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);

        IReadOnlyList<string> права = (await accounts.FlagsAsync(userId, cancellationToken))?.IsActive == true ? Все() : [];

        return resourceIds.Distinct().ToDictionary(id => id, _ => права);
    }

    public async Task<IReadOnlyCollection<Guid>> WhoCanAsync(
        string permission, CancellationToken cancellationToken = default) =>
        await accounts.Query().Where(account => account.IsActive).Select(account => account.Id).ToListAsync(cancellationToken);

    private string[] Все() =>
    [
        .. providers
            .SelectMany(provider => provider.Permissions)
            .Where(permission => !permission.Deprecated)
            .Select(permission => permission.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];
}
