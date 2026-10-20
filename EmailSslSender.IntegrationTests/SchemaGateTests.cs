using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Shared.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Приложение не стартует на отставшей схеме (этап 11.5, правило 16).
/// </summary>
/// <remarks>
/// ⚠ <b>Своя пустая база, и по-другому этого не проверить.</b> Общий хост коллекции уже
/// накатан — на нём «схема отстала» не наступает никогда, и проверка зеленела бы, ничего не
/// проверяя. Секунды на свой контейнер здесь покупают единственный настоящий вопрос: что
/// увидит оператор, поднявший новый образ на старой базе.
///
/// ⚠ <b>Чего этот тест НЕ утверждает: что так безопаснее.</b> Он утверждает, что отказ
/// внятен. Смысл разделения — в порядке «копия, потом миграция», и порядок этот держится
/// вниманием оператора; машина здесь может только не отнимать у него момент, когда копию ещё
/// можно снять.
/// </remarks>
public sealed class SchemaGateTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender")
        .WithPassword("тест-не-секрет")
        .Build();


    public async ValueTask InitializeAsync() =>
        await _postgres.StartAsync();

    public async ValueTask DisposeAsync() =>
        await _postgres.DisposeAsync();

    [Fact]
    public async Task На_ненакатанной_схеме_приложение_не_стартует_и_объясняет_почему()
    {
        await using var factory = Factory();

        var отказ = Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // Сообщение обязано нести три вещи: что не так, чего не хватает поимённо и что
        // набрать. Без третьего оператор в три часа ночи вспоминает синтаксис по памяти.
        отказ.Message.ShouldContain("Схема базы отстала");
        отказ.Message.ShouldContain("InitialCreate");
        // ⚠ Именно `run --rm migrate` — отдельная служба: только у неё пароль владельца схемы,
        // служба api ходит ролью без права менять схему. И БЕЗ `dotnet EmailSslSender.Api.dll`:
        // у образа это `ENTRYPOINT`, а команду `migrate` служба передаёт сама.
        отказ.Message.ShouldContain("docker compose run --rm migrate");

        // ⚠ И про копию — тоже в сообщении. Разделение шагов заведено ровно затем, чтобы
        // копию было где снять; напоминание об этом в тексте отказа — единственное место,
        // где оператор его точно прочитает.
        отказ.Message.ShouldContain("копия базы");
    }

    [Fact]
    public async Task После_миграции_то_же_приложение_поднимается()
    {
        // ⚠ Зеркальный тест, и без него первый доказывает не то: приложение, не стартующее
        // никогда, тоже прошло бы проверку «не стартует на отставшей схеме».
        await МигрироватьAsync();

        await using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/api/health", Ct);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    /// <summary>
    /// Накатить схему, не поднимая приложения.
    /// </summary>
    /// <remarks>
    /// ⚠ Модули здесь не подключаются, и они не нужны: миграции — это классы в сборке хоста,
    /// а не модель. Контекст без конфигураций модулей знает пустую модель и всё равно
    /// применяет все миграции. В производстве ту же работу делает команда <c>migrate</c>,
    /// и она тоже возвращается раньше проверки.
    ///
    /// ⚠ ⚠ <b>А вот утверждение «<c>MigrateAsync</c> исполняет миграции, а не сверяет с
    /// моделью» было НЕВЕРНЫМ, и оно стоило разбора</b> (подэтап 27.4). Сверяет: внутри
    /// <c>Migrator.ValidateMigrations</c>, и на пустой модели поднимает
    /// <c>PendingModelChangesWarning</c> — а он настроен ошибкой. Здесь этого не видно лишь
    /// потому, что база свежая и миграции есть что применять; на уже накатанной схеме тот же
    /// вызов бросит. Поэтому предупреждение глушится явно — ровно как в <c>SenderHost</c>, шаг 1,
    /// где эта же опасность была названа с самого начала. Два файла одного коммита обращались
    /// с одной известной опасностью по-разному; теперь одинаково.
    /// </remarks>
    private async Task МигрироватьAsync()
    {
        var options = new DbContextOptionsBuilder<SenderDbContext>()
            .UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsAssembly(typeof(DatabaseStartup).Assembly.FullName))
            .ConfigureWarnings(предупреждения =>
                предупреждения.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var контекст = new SenderDbContext(options, []);

        await контекст.Database.MigrateAsync(Ct);
    }

    // --- обвязка ---------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Настоящее приложение на своей базе.</summary>
    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting(
                MasterKeyFile.InlineKey,
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        });
}
