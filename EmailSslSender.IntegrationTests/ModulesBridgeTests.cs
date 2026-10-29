using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EmailSslSender.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Crypto;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EmailSslSender.IntegrationTests;

/// <summary>Обновление 1.x: прежняя схема с данными → <c>migrate</c> → схема модулей EAMS.</summary>
/// <remarks>⚠ Новая и обновлённая установки дают одну схему, секреты 1.x открываются конвертом модулей.</remarks>
public sealed class ModulesBridgeTests : IAsyncLifetime
{
    /// <summary>Последняя миграция 1.4.0.</summary>
    private const string Версия14 = "20261026154101_RenameSenderName";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender")
        .WithPassword("тест-не-секрет")
        .Build();

    private readonly byte[] _ключ = RandomNumberGenerator.GetBytes(32);

    private string МастерКлюч => Convert.ToBase64String(_ключ);

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Прежняя_установка_доходит_до_схемы_модулей_и_сохраняет_секреты()
    {
        var прежняя = _postgres.GetConnectionString();
        var новая = await НоваяБазаAsync("fresh");

        await using (var legacy = SchemaMigrator.Legacy(прежняя))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(Версия14, Ct);
        }

        var кто = Guid.CreateVersion7();
        var totp = RandomNumberGenerator.GetBytes(20);
        const string ПарольSmtp = "smtp-пароль-1.x";
        const string КлючKmp = "11111111-2222-3333-4444-555555555555";

        await ЗаполнитьAsync(прежняя, кто, totp, ПарольSmtp, КлючKmp);

        (await MigrateCommand.RunAsync(прежняя, МастерКлюч)).ShouldBe(0, "migrate на прежней схеме");
        (await MigrateCommand.RunAsync(новая, МастерКлюч)).ShouldBe(0, "migrate на пустой базе");

        // Каталог без учёта порядка колонок.
        (await КаталогAsync(прежняя)).ShouldBe(await КаталогAsync(новая));

        await using var db = new NpgsqlConnection(прежняя);
        await db.OpenAsync(Ct);

        (await ЗначениеAsync<bool>(db, $"SELECT is_superuser FROM users WHERE id = '{кто}'"))
            .ShouldBeTrue("каждая учётная запись — суперпользователь: ролей в проекте нет");

        (await ЗначениеAsync<string>(db, "SELECT value::text FROM system_settings WHERE key = 'auth.step_up_minutes'"))
            .ShouldBe("10");

        (await ЗначениеAsync<long>(db, "SELECT count(*) FROM mail_outbox WHERE sent_at IS NULL AND expires_at <= now()"))
            .ShouldBe(1, "неотправленное письмо прежнего конверта умирает, а не уходит без тела");

        var конверт = new SecretEnvelope(MasterKeyRing.Parse(МастерКлюч));

        var (ключTotp, секретTotp) = await ПараAsync(db, $"SELECT data_key, secret FROM user_two_factor WHERE user_id = '{кто}'");
        конверт.Open(ключTotp, секретTotp, $"user:{кто}:totp_seed").ShouldBe(totp);

        var (ключSmtp, парольSmtp) = await ПараAsync(db, "SELECT data_key, sealed FROM system_secrets WHERE key = 'smtp_password'");
        конверт.OpenText(ключSmtp, парольSmtp, "eams:system-secret:smtp_password").ShouldBe(ПарольSmtp);

        var (ключKmp, ключApi) = await ПараAsync(db, "SELECT data_key, api_key FROM keymanager_state WHERE id = 1");
        конверт.OpenText(ключKmp, ключApi, "sender:keymanager:api-key").ShouldBe(КлючKmp);

        // Повторный migrate ничего не трогает.
        (await MigrateCommand.RunAsync(прежняя, МастерКлюч)).ShouldBe(0);
        var (ещёРаз, _) = await ПараAsync(db, "SELECT data_key, sealed FROM system_secrets WHERE key = 'smtp_password'");
        ещёРаз.Bytes.ShouldBe(ключSmtp.Bytes, "повторная перекладка — значит, проверка «уже переложено» не работает");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Данные 1.x под прежним конвертом «sender:».</summary>
    private async Task ЗаполнитьAsync(string connection, Guid кто, byte[] totp, string парольSmtp, string ключKmp)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(Ct);

        var (ключTotp, секретTotp) = ПрежнийКонверт.Seal(_ключ, totp, $"user:{кто}:totp_seed");
        var (ключSmtp, запечатанSmtp) = ПрежнийКонверт.Seal(_ключ, Encoding.UTF8.GetBytes(парольSmtp), "sender:system-secret:smtp_password");
        var (ключДанныхKmp, запечатанKmp) = ПрежнийКонверт.Seal(_ключ, Encoding.UTF8.GetBytes(ключKmp), "sender:keymanager:api-key");

        await using var вставка = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, password_hash, activated_at, two_factor_configured_at)
            VALUES (@id, 'admin@example.test', '!', now(), now());
            INSERT INTO user_two_factor (user_id, data_key, secret, confirmed_at) VALUES (@id, @k1, @s1, now());
            INSERT INTO system_secrets (key, sealed, data_key, updated_at) VALUES ('smtp_password', @s2, @k2, now());
            INSERT INTO keymanager_state (id, api_key, data_key, updated_at) VALUES (1, @s3, @k3, now());
            INSERT INTO mail_outbox (id, to_address, subject, body, data_key, kind)
            VALUES (gen_random_uuid(), 'a@example.test', 'тема', '\x00', '\x00', 'certificate_delivery');
            INSERT INTO mail_outbox (id, to_address, subject, body, data_key, kind, sent_at)
            VALUES (gen_random_uuid(), 'b@example.test', 'тема', '\x00', '\x00', 'certificate_delivery', now());
            """,
            db);

        вставка.Parameters.AddWithValue("id", кто);
        вставка.Parameters.AddWithValue("k1", ключTotp);
        вставка.Parameters.AddWithValue("s1", секретTotp);
        вставка.Parameters.AddWithValue("k2", ключSmtp);
        вставка.Parameters.AddWithValue("s2", запечатанSmtp);
        вставка.Parameters.AddWithValue("k3", ключДанныхKmp);
        вставка.Parameters.AddWithValue("s3", запечатанKmp);

        await вставка.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string> НоваяБазаAsync(string имя)
    {
        await using (var db = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await db.OpenAsync(Ct);
            await using var создать = new NpgsqlCommand($"CREATE DATABASE {имя}", db);
            await создать.ExecuteNonQueryAsync(Ct);
        }

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = имя }.ConnectionString;
    }

    /// <summary>Каталог схемы <c>public</c> без таблиц истории.</summary>
    private static async Task<List<string>> КаталогAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(Ct);

        await using var запрос = new NpgsqlCommand(
            """
            SELECT 'col ' || table_name || '.' || column_name || ' ' || data_type
                || coalesce('(' || character_maximum_length || ')', '') || ' null=' || is_nullable
                || ' def=' || coalesce(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND left(table_name, 2) <> '__'
            UNION ALL
            SELECT 'con ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
            FROM pg_constraint WHERE connamespace = 'public'::regnamespace AND left(conrelid::regclass::text, 3) <> '"__'
            UNION ALL
            SELECT 'idx ' || indexname || ' ' || indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND left(tablename, 2) <> '__'
            UNION ALL
            SELECT 'ext ' || extname FROM pg_extension
            ORDER BY 1
            """,
            db);

        var строки = new List<string>();

        await using var читатель = await запрос.ExecuteReaderAsync(Ct);

        while (await читатель.ReadAsync(Ct))
        {
            строки.Add(читатель.GetString(0));
        }

        строки.Count.ShouldBeGreaterThan(100, "каталог пуст — сравнивать нечего");

        return строки;
    }

    private static async Task<T> ЗначениеAsync<T>(NpgsqlConnection db, string sql)
    {
        await using var команда = new NpgsqlCommand(sql, db);

        return (T)(await команда.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<(WrappedDataKey Key, SealedSecret Sealed)> ПараAsync(NpgsqlConnection db, string sql)
    {
        await using var команда = new NpgsqlCommand(sql, db);
        await using var читатель = await команда.ExecuteReaderAsync(Ct);

        (await читатель.ReadAsync(Ct)).ShouldBeTrue(sql);

        return (new WrappedDataKey((byte[])читатель.GetValue(0)), new SealedSecret((byte[])читатель.GetValue(1)));
    }

    /// <summary>Конверт 1.x.</summary>
    private static class ПрежнийКонверт
    {
        public static (byte[] Wrapped, byte[] Sealed) Seal(byte[] kek, byte[] открыто, string context)
        {
            var ключДанных = RandomNumberGenerator.GetBytes(32);

            var обёртка = new byte[9 + 12 + 16 + 32];
            обёртка[0] = 1;
            BinaryPrimitives.WriteInt32BigEndian(обёртка.AsSpan(1, 4), 1);
            BinaryPrimitives.WriteInt32BigEndian(обёртка.AsSpan(5, 4), 1);
            RandomNumberGenerator.Fill(обёртка.AsSpan(9, 12));

            using (var aes = new AesGcm(kek, 16))
            {
                aes.Encrypt(
                    обёртка.AsSpan(9, 12), ключДанных, обёртка.AsSpan(37), обёртка.AsSpan(21, 16),
                    [.. "sender:dek:v1\0"u8, .. обёртка.AsSpan(0, 9)]);
            }

            var запечатано = new byte[5 + 12 + 16 + открыто.Length];
            запечатано[0] = 1;
            BinaryPrimitives.WriteInt32BigEndian(запечатано.AsSpan(1, 4), 1);
            RandomNumberGenerator.Fill(запечатано.AsSpan(5, 12));

            using (var aes = new AesGcm(ключДанных, 16))
            {
                aes.Encrypt(
                    запечатано.AsSpan(5, 12), открыто, запечатано.AsSpan(33), запечатано.AsSpan(17, 16),
                    [.. "sender:secret:v1\0"u8, .. запечатано.AsSpan(0, 5), .. Encoding.UTF8.GetBytes(context)]);
            }

            return (обёртка, запечатано);
        }
    }
}
