using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Settings;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Ключ API Key Manager Plus: лежит запечатанным вместе с адресом, открывается только для него,
/// снимается, перекладывается при ротации.
/// </summary>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class ApiKeyTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ключ = "75408B31-C7D6-4F5A-9A0E-1B2C3D4E5F60";

    private const string Адрес = "https://kmp.example.test:6565";

    private const string Отпечаток = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    private static readonly string Цель = ApiKeyStore.Target(Адрес, null);

    public ValueTask InitializeAsync()
    {
        база.Сброс();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        база.Конверт.Вернуть();

        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
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
        (await хранилище.GetForAsync(Цель, Ct)).ShouldBe(Ключ);
    }

    [Fact]
    public async Task Пустая_строка_снимает_ключ()
    {
        await Задать(Ключ);
        await Задать("");

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        (await хранилище.HasAsync(Ct)).ShouldBeFalse();
        (await хранилище.GetForAsync(Цель, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Ключ_не_открывается_для_другого_адреса_или_отпечатка()
    {
        // ⚠ Ровно сценарий перехваченной сессии: адрес (и отпечаток) переставлены на свой сервер,
        // и проверка связи понесла бы ключ туда.
        await Задать(Ключ);

        await using var scope = база.Scope();
        var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();

        var чужойАдрес = await Should.ThrowAsync<KeyManagerException>(
            () => хранилище.GetForAsync(ApiKeyStore.Target("https://evil.example.test:6565", null), Ct));
        чужойАдрес.Message.ShouldContain("Введите ключ API заново");
        чужойАдрес.Message.ShouldNotContain(Ключ);

        await Should.ThrowAsync<KeyManagerException>(
            () => хранилище.GetForAsync(ApiKeyStore.Target(Адрес, Отпечаток), Ct));

        await Should.ThrowAsync<KeyManagerException>(
            () => хранилище.GetForAsync(ApiKeyStore.Target("https://kmp.example.test:7070", null), Ct));

        (await хранилище.GetForAsync(Цель, Ct)).ShouldBe(Ключ, "свой адрес по-прежнему открывает");
    }

    [Theory]
    [InlineData("https://KMP.Example.Test:6565/", null)]
    [InlineData(" https://kmp.example.test:6565 ", "")]
    [InlineData("https://kmp.example.test:6565/?x=1", null)]
    public void Один_адрес_в_разной_записи_одна_цель(string адрес, string? отпечаток)
    {
        ApiKeyStore.Target(адрес, отпечаток).ShouldBe(Цель);
    }

    [Fact]
    public void Умолчательный_порт_и_отпечаток_в_разной_записи_одна_цель()
    {
        ApiKeyStore.Target("https://kmp.example.test", null)
            .ShouldBe(ApiKeyStore.Target("https://kmp.example.test:443/", null));

        ApiKeyStore.Target(Адрес, Отпечаток.ToLowerInvariant())
            .ShouldBe(ApiKeyStore.Target(Адрес, Отпечаток.Replace(":", "", StringComparison.Ordinal)));

        ApiKeyStore.Target(Адрес, Отпечаток).ShouldNotBe(Цель);
        ApiKeyStore.Target("https://kmp.example.test:6565/kmp", null).ShouldNotBe(Цель);
    }

    [Fact]
    public async Task Старый_ключ_без_привязки_открывается_только_после_привязки_командой_migrate()
    {
        // ⚠ Ключ, запечатанный до привязки, — голая строка. Во время работы он не открывается
        // вовсе (ревью безопасности 2026-10-20: «доверие с первого раза» отдавало его тому, кто
        // первым подменил адрес), а migrate привязывает его к адресу из настроек.
        await using (var scope = база.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ключДанных = база.Конверт.CreateDataKey();

            db.Set<KeyManagerState>().Add(new KeyManagerState
            {
                ApiKey = база.Конверт.SealText(ключДанных, Ключ, ApiKeyStore.Контекст).Bytes,
                DataKey = ключДанных.Bytes,
            });

            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = база.Scope())
        {
            var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();

            (await Should.ThrowAsync<KeyManagerException>(() => хранилище.GetForAsync(Цель, Ct)))
                .Message.ShouldContain("migrate");
            (await хранилище.BindLegacyAsync(Цель, Ct)).ShouldBeTrue();
            (await хранилище.BindLegacyAsync(Цель, Ct)).ShouldBeFalse("уже привязан — повторно нечего");
            (await хранилище.GetForAsync(Цель, Ct)).ShouldBe(Ключ);
        }

        var строка = await Строка();
        var открыто = база.Конверт.OpenText(
            new WrappedDataKey(строка.DataKey!), new SealedSecret(строка.ApiKey!), ApiKeyStore.Контекст);
        var привязка = BoundSecret.Unpack(открыто).ShouldNotBeNull("старый ключ перепечатан с привязкой");
        привязка.Target.ShouldBe(Цель);
        привязка.Secret.ShouldBe(Ключ);

        база.Логи.Строки.ShouldContain(с => с.Contains("keymanager_api_key_bound_by_migrate", StringComparison.Ordinal));
        база.Логи.Строки.ShouldAllBe(с => !с.Contains(Ключ, StringComparison.Ordinal));

        await using (var scope = база.Scope())
        {
            var хранилище = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();

            await Should.ThrowAsync<KeyManagerException>(
                () => хранилище.GetForAsync(ApiKeyStore.Target("https://evil.example.test", null), Ct));
            (await хранилище.GetForAsync(Цель, Ct)).ShouldBe(Ключ);
        }
    }

    [Fact]
    public async Task Соединитель_отказывает_сменившемуся_адресу_до_запроса()
    {
        база.Настройки.Значения[KeyManagerSettings.BaseUrl.Name] = Адрес;
        await Задать(Ключ);

        await using var scope = база.Scope();
        var соединитель = new KeyManagerConnector(
            база.Настройки, scope.ServiceProvider.GetRequiredService<ApiKeyStore>());

        // Свой адрес — клиент открывается (сети при этом нет: запросов не было).
        using (await соединитель.OpenAsync(Ct))
        {
        }

        база.Настройки.Значения[KeyManagerSettings.BaseUrl.Name] = "https://evil.example.test:6565";

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => соединитель.OpenAsync(Ct));
        ошибка.Message.ShouldContain("заново");

        база.Настройки.Значения[KeyManagerSettings.BaseUrl.Name] = Адрес;
        база.Настройки.Значения[KeyManagerSettings.ServerSha256.Name] = Отпечаток;

        await Should.ThrowAsync<KeyManagerException>(() => соединитель.OpenAsync(Ct));
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
            (await scope.ServiceProvider.GetRequiredService<ApiKeyStore>().GetForAsync(Цель, Ct)).ShouldBe(Ключ);

            // Повторный запуск ничего не трогает.
            (await scope.ServiceProvider.GetRequiredService<ApiKeyRewrap>().RewrapAsync(10, null, Ct))
                .Rewrapped.ShouldBe(0);
        }
    }

    private async Task Задать(string значение)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await scope.ServiceProvider.GetRequiredService<ApiKeyStore>().PutAsync(
            значение,
            ApiKeyStore.Target(
                (string?)база.Настройки.Значения.GetValueOrDefault(KeyManagerSettings.BaseUrl.Name) ?? Адрес,
                (string?)база.Настройки.Значения.GetValueOrDefault(KeyManagerSettings.ServerSha256.Name)),
            Ct);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<KeyManagerState> Строка()
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct);
    }
}
