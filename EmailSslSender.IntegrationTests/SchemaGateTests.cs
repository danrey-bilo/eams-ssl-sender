using EmailSslSender.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace EmailSslSender.IntegrationTests;

/// <summary>Приложение не стартует на отставшей схеме и внятно объясняет почему.</summary>
/// <remarks>⚠ Своя пустая база: общий хост коллекции уже накатан.</remarks>
public sealed class SchemaGateTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender")
        .WithPassword("тест-не-секрет")
        .Build();

    private readonly string _мастерКлюч = MigrateCommand.NewMasterKey();

    public async ValueTask InitializeAsync() =>
        await _postgres.StartAsync();

    public async ValueTask DisposeAsync() =>
        await _postgres.DisposeAsync();

    [Fact]
    public async Task На_ненакатанной_схеме_приложение_не_стартует_и_объясняет_почему()
    {
        await using var factory = Factory();

        var отказ = Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // Что не так, чего не хватает и что набрать.
        отказ.Message.ShouldContain("Схема базы отстала");
        отказ.Message.ShouldContain("identity: 20261022231421_Baseline");
        // ⚠ Отдельная служба migrate: только у неё пароль владельца схемы.
        отказ.Message.ShouldContain("docker compose run --rm migrate");

        // Напоминание о копии перед миграцией.
        отказ.Message.ShouldContain("копия базы");
    }

    [Fact]
    public async Task После_миграции_то_же_приложение_поднимается()
    {
        // Зеркало первого: иначе прошло бы и приложение, не стартующее никогда.
        await МигрироватьAsync();

        await using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/api/health", Ct);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private async Task МигрироватьAsync() =>
        (await MigrateCommand.RunAsync(_postgres.GetConnectionString(), _мастерКлюч)).ShouldBe(0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting(MasterKeyFile.InlineKey, _мастерКлюч);
        });
}
