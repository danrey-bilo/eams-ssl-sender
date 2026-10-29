using Npgsql;
using Testcontainers.PostgreSql;

namespace EmailSslSender.IntegrationTests;

/// <summary>Сброс доступа из окружения — один раз на пару «адрес + пароль».</summary>
/// <remarks>⚠ <c>migrate</c> идёт при каждом обновлении стека — забытые переменные не должны сбрасывать снова.</remarks>
public sealed class ResetAccessOnceTests : IAsyncLifetime
{
    private const string Адрес = "admin@example.test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender")
        .WithPassword("тест-не-секрет")
        .Build();

    private readonly string _мастерКлюч = MigrateCommand.NewMasterKey();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Сброс_с_тем_же_паролем_не_повторяется_а_с_другим_применяется()
    {
        (await MigrateAsync(
            "--SENDER_INIT_ADMIN_EMAIL=" + Адрес, "--SENDER_INIT_ADMIN_PASSWORD=Первый-пароль-2026!")).ShouldBe(0);

        (await MigrateAsync(Сброс("Сброшенный-пароль-2026!"))).ShouldBe(0);
        var послеСброса = await ХешAsync();
        (await ОтметокAsync()).ShouldBe(1);

        // Администратор сменил пароль, переменные забыты в стеке.
        await ВыполнитьAsync("UPDATE users SET password_hash = 'сменён-администратором' WHERE email = @a");
        (await MigrateAsync(Сброс("Сброшенный-пароль-2026!"))).ShouldBe(0);

        (await ХешAsync()).ShouldBe("сменён-администратором", "та же пара второй раз не применяется");

        // Другой пароль — новый сброс.
        (await MigrateAsync(Сброс("Ещё-один-пароль-2026!"))).ShouldBe(0);

        (await ХешAsync()).ShouldNotBe("сменён-администратором");
        (await ХешAsync()).ShouldNotBe(послеСброса);
        (await ОтметокAsync()).ShouldBe(2);
    }

    private static string[] Сброс(string пароль) =>
        ["--SENDER_RESET_ACCESS_EMAIL=" + Адрес, "--SENDER_RESET_ACCESS_PASSWORD=" + пароль];

    private Task<int> MigrateAsync(params string[] settings) =>
        MigrateCommand.RunAsync(_postgres.GetConnectionString(), _мастерКлюч, settings);

    private async Task<string> ХешAsync() =>
        (string)(await ЗначениеAsync("SELECT password_hash FROM users WHERE email = @a"))!;

    private async Task<long> ОтметокAsync() =>
        (long)(await ЗначениеAsync("SELECT count(*) FROM sender_reset_access_marks"))!;

    private async Task<object?> ЗначениеAsync(string sql)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var команда = new NpgsqlCommand(sql, db);
        команда.Parameters.AddWithValue("a", Адрес);

        return await команда.ExecuteScalarAsync(Ct);
    }

    private async Task ВыполнитьAsync(string sql)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync(Ct);
        await using var команда = new NpgsqlCommand(sql, db);
        команда.Parameters.AddWithValue("a", Адрес);
        await команда.ExecuteNonQueryAsync(Ct);
    }
}
