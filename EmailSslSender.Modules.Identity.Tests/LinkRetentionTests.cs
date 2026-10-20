using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Retention;
using EmailSslSender.Modules.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Уборка одноразовых ссылок и просроченных выданных доступов.
/// </summary>
/// <remarks>
/// <b>Осталось здесь при расщеплении сьюта (этап 10.5.6).</b> Уборка мёртвых токенов и их
/// секретов уехала в <c>EmailSslSender.Modules.Tokens.Tests/TokenRetentionTests.cs</c> вместе с
/// таблицами: один уборщик на две чужие друг другу схемы означал бы, что модуль ходит в
/// таблицы соседа.
///
/// ⚠ Половина тестов здесь — про то, чего уборщик делать <b>НЕ</b> должен, и это не
/// перестраховка. Проверка, у которой испытан только один исход, может удалять всё подряд и
/// выглядеть исправной; а уборщик, способный удалить живое, отбирает у приглашённого его
/// ссылку молча.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class LinkRetentionTests(IdentityDatabase database)
{

    [Fact]
    public async Task Действующее_приглашение_не_трогается_а_просроченное_убирается()
    {
        database.Actor.AsSuperuser();

        var live = await InviteAsync();
        var stale = await InviteAsync();

        await AgeLinkAsync(stale, days: 60);

        await RunAsync();

        (await LinkExistsAsync(live)).ShouldBeTrue("действующая ссылка нужна приглашённому");
        (await LinkExistsAsync(stale)).ShouldBeFalse();
    }

    [Fact]
    public async Task Пустой_проход_молчит_в_журнале()
    {
        // Ежедневная строка «убрано ноль» — это шум, а журнал безопасности от шума уже
        // один раз чистили.
        await RunAsync();

        var before = database.Audit.Entries.Count(entry => entry.Action.Name == "auth.cleanup");

        var result = await RunAsync();

        result.Any.ShouldBeFalse();
        database.Audit.Entries.Count(entry => entry.Action.Name == "auth.cleanup").ShouldBe(before);
    }

    [Fact]
    public async Task Убранное_попадает_в_журнал_с_числами()
    {
        // Неверно выставленный срок хранения должно быть видно по счётчикам, а не по
        // опустевшей таблице.
        var stale = await InviteAsync();

        await AgeLinkAsync(stale, days: 60);
        await RunAsync();

        var entry = database.Audit.Entries.Last(row => row.Action.Name == "identity.cleanup");

        entry.ActorType.ShouldBe(Shared.Contracts.Audit.AuditActorType.System);
        entry.ActorId.ShouldBeNull("убирает система, а не человек");
        entry.Changes.ShouldNotBeNull();
        entry.Changes!["older_than_days"].ShouldBe(30);
    }

    // --- обвязка -------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private async Task<CleanupResult> RunAsync()
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider.GetRequiredService<LinkCleanup>().RunAsync(Ct);
    }

    private async Task<(Guid Id, string Email)> ActiveUserAsync()
    {
        database.Actor.AsSuperuser();

        var email = $"retention-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", Strong), Ct));

        return (user.Id, email);
    }

    private async Task<string> InviteAsync()
    {
        var email = $"invited-{Guid.CreateVersion7():N}@example.com";
        var (_, invitation) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Приглашённый", null), Ct));

        return invitation!.Token;
    }

    private Task AgeLinkAsync(string token, int days) =>
        database.ReadAsync(db => db.Set<IdentityToken>()
            .Where(row => row.TokenHash == Hash(token))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-days)), Ct));

    private Task<bool> LinkExistsAsync(string token) =>
        database.ReadAsync(db => db.Set<IdentityToken>().AnyAsync(row => row.TokenHash == Hash(token), Ct));

    /// <summary>Тот же хеш, что кладёт модуль: в базе ссылка живёт только им.</summary>
    private static byte[] Hash(string token) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
