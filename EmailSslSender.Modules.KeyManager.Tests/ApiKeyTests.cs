using System.Text;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Ключ API Key Manager Plus: лежит запечатанным, снимается, перекладывается при ротации.
/// </summary>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class ApiKeyTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ключ = "75408B31-C7D6-4F5A-9A0E-1B2C3D4E5F60";

    public ValueTask InitializeAsync()
    {
        база.Сброс();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        база.Конверт.Вернуть();

        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>()
            .Set<KeyManagerState>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ключ_лежит_запечатанным_и_открывается()
    {
        await Задать(Ключ);

        var строка = await Строка();
        строка.ApiKey.ShouldNotBeNull();
        строка.ApiKey.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Ключ)).ShouldBe(-1, "ключ в базе не лежит открытым");
        строка.UpdatedBy.ShouldBe(база.Личность.Id);

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        (await хранилище.HasAsync(Ct)).ShouldBeTrue();
        (await хранилище.GetAsync(Ct)).ShouldBe(Ключ);
    }

    [Fact]
    public async Task Пустая_строка_снимает_ключ()
    {
        await Задать(Ключ);
        await Задать("");

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        (await хранилище.HasAsync(Ct)).ShouldBeFalse();
        (await хранилище.GetAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Ротация_перекладывает_обёртку_и_ключ_открывается()
    {
        await Задать(Ключ);
        var было = (await Строка()).DataKey!;

        база.Конверт.Повернуть();

        await using (var scope = база.Scope())
        {
            var итог = await scope.ServiceProvider.GetRequiredService<ApiKeyRewrap>().RewrapAsync(10, null, Ct);
            итог.Rewrapped.ShouldBe(1);
            итог.Next.ShouldBeNull();
        }

        var стало = (await Строка()).DataKey!;
        стало.ShouldNotBe(было);
        база.Конверт.NeedsRewrap(new WrappedDataKey(стало)).ShouldBeFalse();

        await using (var scope = база.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<ApiKeyStore>().GetAsync(Ct)).ShouldBe(Ключ);

            // Повторный запуск ничего не трогает.
            (await scope.ServiceProvider.GetRequiredService<ApiKeyRewrap>().RewrapAsync(10, null, Ct))
                .Rewrapped.ShouldBe(0);
        }
    }

    private async Task Задать(string значение)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        await scope.ServiceProvider.GetRequiredService<ApiKeyStore>().PutAsync(значение, Ct);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<KeyManagerState> Строка()
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<SenderDbContext>()
            .Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct);
    }
}
