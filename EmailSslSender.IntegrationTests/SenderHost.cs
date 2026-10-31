using System.Net.Http.Json;
using EmailSslSender.Api.Infrastructure;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace EmailSslSender.IntegrationTests;

/// <summary>Настоящее приложение на своём чистом PostgreSQL в контейнере на время прогона.</summary>
public sealed class SenderHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly Dictionary<string, string> _fixtureFactors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Войти паролем и при нужде завершить вход кодом; сессию настройки довести подключением фактора.</summary>
    public async Task<HttpResponseMessage> LoginFixtureAsync(HttpClient client, string email, string password,
        bool cookie, CancellationToken cancellationToken, bool completeFactor = true)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password, use_cookie = cookie }, cancellationToken);
        if (!completeFactor) return response;
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && _fixtureFactors.TryGetValue(email, out var secret))
        {
            var refusal = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            var challenge = refusal["details"]?["challenge"]?.GetValue<string>();
            if (challenge is null) return response;

            // Иначе два входа в одном 30-секундном шаге упрутся в защиту от повтора кода.
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE user_two_factor SET last_step = -1 WHERE user_id IN (SELECT id FROM users WHERE email = {email})", cancellationToken);
            response.Dispose();
            return await client.PostAsJsonAsync("/api/auth/two-factor",
                new { challenge, code = TestTotpCode(secret), use_cookie = cookie }, cancellationToken);
        }
        if (response.IsSuccessStatusCode)
        {
            var tokens = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            await CompleteInitialFactorAsync(client, tokens["access_token"]!.GetValue<string>(), cancellationToken);
        }
        return response;
    }

    /// <summary>Завести учётную запись приглашением и активировать её паролем.</summary>
    public async Task<HttpResponseMessage> CreateActivatedUserAsync(HttpClient client, object draft, CancellationToken cancellationToken)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToNode(draft)!.AsObject();
        var password = body["password"]!.GetValue<string>();
        var mustChange = body["must_change_password"]?.GetValue<bool>() ?? false;
        body.Remove("password");
        body.Remove("must_change_password");
        var response = await client.PostAsJsonAsync("/api/users", body, cancellationToken);
        if (!response.IsSuccessStatusCode) return response;
        var created = (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken))!;
        var invitation = created["invitation_token"]!.GetValue<string>();
        var activation = await client.PostAsJsonAsync("/api/activation", new { token = invitation, password }, cancellationToken);
        activation.EnsureSuccessStatusCode();
        var activated = (await activation.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken))!;
        if (mustChange)
        {
            (await client.PatchAsJsonAsync($"/api/users/{activated["id"]!.GetValue<Guid>()}",
                new { must_change_password = true }, cancellationToken)).EnsureSuccessStatusCode();
            activated["must_change_password"] = true;
        }
        created["user"] = activated;
        var original = response.Content;
        response.Content = JsonContent.Create(created);
        original.Dispose();
        return response;
    }

    /// <summary>Подключить аутентификатор в сессии настройки — тем же путём, что человек.</summary>
    public async Task CompleteInitialFactorAsync(HttpClient client, string access, CancellationToken cancellationToken)
    {
        async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await client.SendAsync(request, cancellationToken);
        }
        var meResponse = await RequestAsync(HttpMethod.Get, "/api/me");
        meResponse.EnsureSuccessStatusCode();
        var me = (await meResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken))!;
        if (me["must_change_password"]!.GetValue<bool>() || !me["must_enroll_two_factor"]!.GetValue<bool>()) return;
        var setup = await RequestAsync(HttpMethod.Post, "/api/me/two-factor/totp/setup");
        setup.EnsureSuccessStatusCode();
        var factor = (await setup.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken))!;
        _fixtureFactors[me["email"]!.GetValue<string>()] = factor["secret"]!.GetValue<string>();
        var code = TestTotpCode(factor["secret"]!.GetValue<string>());
        (await RequestAsync(HttpMethod.Post, "/api/me/two-factor/totp/confirm", new { code })).EnsureSuccessStatusCode();
    }

    /// <summary>Секрет аутентификатора, подключённого обвязкой.</summary>
    public string? FactorSecret(string email) => _fixtureFactors.GetValueOrDefault(email);

    /// <summary>Текущий код аутентификатора по секрету base32 (RFC 6238).</summary>
    public static string TestTotpCode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var symbol in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(symbol);
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
        }
        var counter = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Имя коллекции: один контейнер и один хост на весь прогон, тесты по очереди.</summary>
    public const string Collection = "sender-host";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender")
        .WithPassword("тест-не-секрет")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        await MigrateAsync();

        User.Завести = id => ЗавестиУчётнуюЗаписьAsync(id).GetAwaiter().GetResult();
    }

    /// <summary>Активная учётная запись администратора без пароля — под подставную личность.</summary>
    private async Task ЗавестиУчётнуюЗаписьAsync(Guid id)
    {
        await using var db = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();

        await using var команда = new Npgsql.NpgsqlCommand(
            "INSERT INTO users (id, email, password_hash, is_superuser, activated_at) "
            + "VALUES (@id, @email, '!', true, now())",
            db);
        команда.Parameters.AddWithValue("id", id);
        команда.Parameters.AddWithValue("email", $"test-{id:N}@example.test");

        await команда.ExecuteNonQueryAsync();
    }

    /// <summary>⚠ Схема — настоящей командой <c>migrate</c> до поднятия: на отставшей схеме хост не стартует.</summary>
    private async Task MigrateAsync() =>
        (await MigrateCommand.RunAsync(_postgres.GetConnectionString(), _мастерКлюч))
            .ShouldBe(0, "команда migrate не прошла на чистой базе");

    /// <summary>Одноразовый мастер-ключ прогона — общий у <c>migrate</c> и сервера.</summary>
    private readonly string _мастерКлюч = MigrateCommand.NewMasterKey();

    public override async ValueTask DisposeAsync()
    {
        // ⚠ Таблица настроек обязана остаться пустой: забытая строка роняет чужие тесты.
        var осталось = await СтрокНастроекAsync();

        await base.DisposeAsync();
        await _postgres.DisposeAsync();

        if (осталось > 0)
        {
            throw new InvalidOperationException(
                $"После коллекции в таблице настроек осталось строк: {осталось}. "
                + "Тест, снявший подмену читателя, обязан убирать за собой "
                + "через SystemSettingsFixture.");
        }
    }

    /// <summary>Строка подключения к базе прогона.</summary>
    public string Postgres => _postgres.GetConnectionString();

    /// <summary>Кто пришёл с запросом — задаёт тест.</summary>
    public TestUser User { get; } = new();

    /// <summary>Настройки, которыми управляет тест.</summary>
    public TestSettings Settings { get; } = new();

    /// <summary>⚠ Настоящие настройки из базы — только через <see cref="SystemSettingsFixture"/>, она чистит таблицу.</summary>
    public bool НастоящиеНастройки { get; set; }

    /// <summary>Очистить таблицу настроек и вернуть строки набора <c>sender</c>.</summary>
    public async Task ОчиститьНастройкиAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM system_settings; "
            + "INSERT INTO system_settings (key, value, updated_at) VALUES "
            + "('auth.step_up_minutes', '10', now()), ('ui.locale', '\"ru\"', now());",
            cancellationToken);
    }

    /// <summary>Записать настройку мимо фикстуры — нужен одному тесту обвязки.</summary>
    public async Task ЗаписатьМимоФикстурыAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO system_settings (key, value, updated_at) VALUES ('auth.lockout_threshold', '1', now())");
    }

    /// <summary>Строк в таблице настроек сверх набора <c>sender</c>.</summary>
    public async Task<int> СтрокНастроекAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var значения = await db.Database
            .SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM system_settings "
                + "WHERE NOT (key = 'auth.step_up_minutes' AND value = '10'::jsonb) "
                + "AND NOT (key = 'ui.locale' AND value = '\"ru\"'::jsonb)")
            .ToListAsync(cancellationToken);

        return значения[0];
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());

        // Пределы подняты: тесты входят десятки раз с одного адреса.
        builder.UseSetting("RateLimits:LoginPerMinute", "1000");
        builder.UseSetting("RateLimits:LoginPerAddressPerMinute", "1000");
        builder.UseSetting("RateLimits:ActivationPerMinute", "10000");

        builder.UseSetting(MasterKeyFile.InlineKey, _мастерКлюч);

        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<ICurrentUser>(sp =>
                User.Overridden ? User : sp.GetRequiredService<RequestCurrentUser>());

            var настоящиеНастройки = services.Last(service => service.ServiceType == typeof(ISettingsReader));

            services.Remove(настоящиеНастройки);

            services.AddScoped<ISettingsReader>(sp => НастоящиеНастройки
                ? (ISettingsReader)настоящиеНастройки.ImplementationFactory!(sp)
                : Settings);

            // Тестовые контроллеры — частью приложения.
            services.AddControllers().AddApplicationPart(typeof(SenderHost).Assembly);
        });
    }
}

[CollectionDefinition(SenderHost.Collection)]
public sealed class SenderHostCollection : ICollectionFixture<SenderHost>;
