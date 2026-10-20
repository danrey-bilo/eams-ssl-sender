using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Modules.Tokens.Retention;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Modules.Tokens.Storage;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Tokens.Tests;

/// <summary>
/// Уборка мёртвых токенов и их секретов.
/// </summary>
/// <remarks>
/// <b>Приехало из <c>Identity/RetentionTests.cs</c> (этап 10.5.6)</b> — та его часть, что
/// про токены. Одноразовые ссылки и просроченные доступы остались у соседа.
///
/// ⚠ <b>Что здесь НЕ должно удаляться — половина смысла сьюта.</b> Уборщик, способный
/// удалить живое, выкидывает людей из системы молча, и обнаруживается это не логом, а
/// звонками.
/// </remarks>
[Collection(TokenDatabaseCollection.Name)]
public sealed class TokenRetentionTests(TokenDatabase database)
{
    [Fact]
    public async Task Мёртвая_сессия_и_её_токены_убираются()
    {
        var session = await SignInAsync();

        await AgeAsync(session, days: 60);

        var result = await RunAsync();

        result.Tokens.ShouldBeGreaterThanOrEqualTo(1);
        (await ExistsAsync(session)).ShouldBeFalse();

        // Токены уехали вместе с сессией по каскаду — своих строк не осталось.
        (await TokenCountAsync(session)).ShouldBe(0);
    }

    [Fact]
    public async Task Живая_сессия_не_трогается()
    {
        var session = await SignInAsync();

        await RunAsync();

        (await ExistsAsync(session)).ShouldBeTrue();
        (await TokenCountAsync(session)).ShouldBe(2, "токены доступа и обновления на месте");
    }

    [Fact]
    public async Task Недавно_закрытая_сессия_ещё_хранится()
    {
        // Срок хранения считается от момента закрытия, а не от факта закрытия: вчерашний
        // выход должен оставаться видимым, пока разбирают происшествие.
        var session = await SignInAsync();

        await SetRevokedAsync(session, DateTimeOffset.UtcNow.AddDays(-1));

        await RunAsync();

        (await ExistsAsync(session)).ShouldBeTrue();
    }

    [Fact]
    public async Task Давно_закрытая_сессия_убирается_даже_если_срок_её_ещё_не_вышел()
    {
        // Сессия закрыта кражей час назад, а её собственный срок — двенадцать часов вперёд.
        // Держать её до конца срока незачем: она уже мертва.
        var session = await SignInAsync();

        await SetRevokedAsync(session, DateTimeOffset.UtcNow.AddDays(-60));

        await RunAsync();

        (await ExistsAsync(session)).ShouldBeFalse();
    }

    // --- обвязка -----------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RequestOrigin Origin =
        new(System.Net.IPAddress.Loopback, "EAMS-Tests/1.0");

    private async Task<CleanupResult> RunAsync()
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider.GetRequiredService<TokenCleanup>().RunAsync(Ct);
    }

    /// <summary>Выдать пару и вернуть строку выдачи.</summary>
    private async Task<Guid> SignInAsync(string _ = "")
    {
        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenIssuer>();

        var pair = await db.InTransactionAsync(
            async (inner) =>
            {
                var issued = await tokens.IssueAsync(
                    new TokenIssueRequest(
                        Guid.CreateVersion7(), TokenKinds.Session, Origin.UserAgent, Origin.Address),
                    inner);

                await db.SaveChangesAsync(inner);

                return issued;
            },
            Ct);

        return pair.TokenId;
    }

    /// <summary>Отодвинуть сроки строки и её секретов в прошлое.</summary>
    private Task AgeAsync(Guid token, int days) =>
        database.ReadAsync(async db =>
        {
            var past = DateTimeOffset.UtcNow.AddDays(-days);

            await db.Set<AuthTokenSecret>().Where(row => row.TokenId == token)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExpiresAt, past), Ct);

            return await db.Set<AuthToken>().Where(row => row.Id == token)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExpiresAt, past), Ct);
        });

    private Task SetRevokedAsync(Guid token, DateTimeOffset when) =>
        database.ReadAsync(db => db.Set<AuthToken>()
            .Where(row => row.Id == token)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.RevokedAt, when), Ct));

    private Task<bool> ExistsAsync(Guid token) =>
        database.ReadAsync(db => db.Set<AuthToken>().AnyAsync(row => row.Id == token, Ct));

    private Task<int> TokenCountAsync(Guid token) =>
        database.ReadAsync(db => db.Set<AuthTokenSecret>().CountAsync(row => row.TokenId == token, Ct));
}
