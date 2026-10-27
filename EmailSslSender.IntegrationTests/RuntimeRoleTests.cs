using EmailSslSender.Api.Infrastructure;
using Npgsql;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Роль сервиса: строки читает и пишет, схему не меняет и команд в контейнере базы не выполняет.
/// </summary>
/// <remarks>
/// ⚠ Владелец здесь — суперпользователь тестового контейнера, как и в боевом образе
/// <c>postgres</c>. Схема уже накатана обвязкой <see cref="SenderHost"/>.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class RuntimeRoleTests(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Роль_сервиса_пишет_строки_но_не_меняет_схему()
    {
        var (роль, пароль) = Новая();
        await ЗавестиAsync(роль, пароль);

        await using var сервис = await ПодключитьсяAsync(роль, пароль);

        // Чтение и запись строк — работа сервиса.
        (await ЗначениеAsync<long>(сервис, "SELECT count(*) FROM users")).ShouldBeGreaterThanOrEqualTo(0);
        await ВыполнитьAsync(сервис, "UPDATE system_settings SET key = key WHERE false");

        // ⚠ Схема — только владельцу.
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "CREATE TABLE взлом (id int)")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "DROP TABLE users")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // ⚠ ⚠ Ради этого роль и заведена: у суперпользователя это выполнение команд в контейнере базы.
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "COPY (SELECT 1) TO PROGRAM 'true'")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await using var владелец = await ВладелецAsync();
        (await ЗначениеAsync<bool>(владелец, $"SELECT rolsuper OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = '{роль}'"))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Пароль_роли_хранится_проверочным_значением_и_журнал_миграций_только_для_чтения()
    {
        var (роль, пароль) = Новая();
        await ЗавестиAsync(роль, пароль);

        await using (var владелец = await ВладелецAsync())
        {
            (await ЗначениеAsync<string>(владелец, $"SELECT rolpassword FROM pg_authid WHERE rolname = '{роль}'"))
                .ShouldStartWith("SCRAM-SHA-256$4096:");
        }

        await using var сервис = await ПодключитьсяAsync(роль, пароль);
        (await ЗначениеAsync<long>(сервис, "SELECT count(*) FROM __ef_history_identity")).ShouldBeGreaterThan(0);
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "DELETE FROM __ef_history_keymanager")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// Журнал действий роли сервиса — только дописывать и заводить партиции наперёд: ни правки, ни удаления
    /// партиций (срок хранения выключен, журнал хранится всегда). Отметки сброса доступа ей закрыты.
    /// </summary>
    [Fact]
    public async Task Роль_сервиса_не_правит_и_не_стирает_журнал()
    {
        var (роль, пароль) = Новая();
        await ЗавестиAsync(роль, пароль);

        await using var сервис = await ПодключитьсяAsync(роль, пароль);

        (await ЗначениеAsync<long>(сервис, "SELECT count(*) FROM audit_log")).ShouldBeGreaterThanOrEqualTo(0);
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "UPDATE audit_log SET reason = reason WHERE false")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "DELETE FROM audit_log WHERE false")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "SELECT audit_drop_partition('audit_log_2000_01')")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ВыполнитьAsync(сервис, "SELECT count(*) FROM sender_reset_access_marks")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Партиции наперёд — работа обслуживания журнала, которое идёт ролью сервиса.
        (await ЗначениеAsync<string>(сервис, "SELECT audit_ensure_partition((now() + interval '2 months')::date)"))
            .ShouldStartWith("audit_log_");
    }

    [Fact]
    public async Task Таблица_будущей_миграции_доступна_роли_сама()
    {
        var (роль, пароль) = Новая();
        await ЗавестиAsync(роль, пароль);

        // Таблицу создаёт владелец — так её создаст следующая миграция.
        var таблица = "будущая_" + Guid.CreateVersion7().ToString("N")[..8];
        await using (var владелец = await ВладелецAsync())
        {
            await ВыполнитьAsync(владелец, $"CREATE TABLE {таблица} (id bigint GENERATED BY DEFAULT AS IDENTITY, v text)");
        }

        await using var сервис = await ПодключитьсяAsync(роль, пароль);
        await ВыполнитьAsync(сервис, $"INSERT INTO {таблица} (v) VALUES ('строка')");
        (await ЗначениеAsync<long>(сервис, $"SELECT count(*) FROM {таблица}")).ShouldBe(1);
    }

    [Fact]
    public async Task Повторный_migrate_меняет_пароль_роли()
    {
        var (роль, старый) = Новая();
        await ЗавестиAsync(роль, старый);

        var новый = "новый-" + Guid.NewGuid().ToString("N");
        await ЗавестиAsync(роль, новый);

        await using (var сервис = await ПодключитьсяAsync(роль, новый))
        {
            (await ЗначениеAsync<int>(сервис, "SELECT 1")).ShouldBe(1);
        }

        (await Should.ThrowAsync<PostgresException>(() => ПодключитьсяAsync(роль, старый)))
            .SqlState.ShouldBe(PostgresErrorCodes.InvalidPassword);
    }

    [Theory]
    [InlineData("Sender_App")]
    [InlineData("app; DROP TABLE users")]
    [InlineData("")]
    public async Task Имя_роли_проверяется_до_базы(string имя)
    {
        await using var владелец = await ВладелецAsync();

        await Should.ThrowAsync<Exception>(() => RuntimeRole.EnsureAsync(владелец, имя, "пароль", Ct));
    }

    [Fact]
    public async Task Роль_сервиса_не_может_совпадать_с_владельцем()
    {
        await using var владелец = await ВладелецAsync();
        var имя = await ЗначениеAsync<string>(владелец, "SELECT current_user");

        var ошибка = await Should.ThrowAsync<InvalidOperationException>(
            () => RuntimeRole.EnsureAsync(владелец, имя, "пароль", Ct));

        ошибка.Message.ShouldContain("совпадает с владельцем");
    }

    // --- обвязка ---------------------------------------------------------------------------

    private static (string Роль, string Пароль) Новая() =>
        ("sender_app_" + Guid.CreateVersion7().ToString("N")[..12], "п@р'оль;" + Guid.NewGuid().ToString("N"));

    private async Task ЗавестиAsync(string роль, string пароль)
    {
        await using var владелец = await ВладелецAsync();
        await RuntimeRole.EnsureAsync(владелец, роль, пароль, Ct);
    }

    private async Task<NpgsqlConnection> ВладелецAsync()
    {
        var подключение = new NpgsqlConnection(host.Postgres);
        await подключение.OpenAsync(Ct);
        return подключение;
    }

    private async Task<NpgsqlConnection> ПодключитьсяAsync(string роль, string пароль)
    {
        var строка = new NpgsqlConnectionStringBuilder(host.Postgres) { Username = роль, Password = пароль, Pooling = false };
        var подключение = new NpgsqlConnection(строка.ConnectionString);

        try
        {
            await подключение.OpenAsync(Ct);
        }
        catch
        {
            await подключение.DisposeAsync();
            throw;
        }

        return подключение;
    }

    private static async Task ВыполнитьAsync(NpgsqlConnection подключение, string sql)
    {
        await using var команда = new NpgsqlCommand(sql, подключение);
        await команда.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ЗначениеAsync<T>(NpgsqlConnection подключение, string sql)
    {
        await using var команда = new NpgsqlCommand(sql, подключение);
        return (T)(await команда.ExecuteScalarAsync(Ct))!;
    }
}
