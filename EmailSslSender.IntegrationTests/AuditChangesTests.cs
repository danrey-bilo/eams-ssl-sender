using System.Net;
using System.Net.Http.Json;
using EmailSslSender.Api.Infrastructure;
using Modules.Core.Contracts.Audit;
using Modules.Crypto;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Журнал говорит, ЧТО изменилось: значения — у настроек и Key Manager Plus, у прочих — имена полей.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Без значений строка «settings.system.update» не говорила бы, какой ключ и с чего на что, —
/// то есть после подмены <c>mail.smtp_host</c> или <c>keymanager.base_url</c> журнал молчал бы о
/// главном. Писать всё подряд тоже нельзя: у учётных записей в изменениях лежат адреса почты.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class AuditChangesTests(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditAction Событие = new("user.update");

    private static readonly AuditAction Настройка = new("settings.system.update");

    [Fact]
    public void У_прочих_событий_пишутся_только_имена_полей()
    {
        var перо = new Перо();

        Журнал(перо).Write(new AuditEntry(
            Событие,
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal) { ["email"] = "кто@example.com" }));

        перо.Строки.Single().ShouldNotContain("кто@example.com");
        перо.Поля.Single()["audit_changes"].ShouldBe("""["email"]""");
    }

    [Fact]
    public void У_настроек_изменения_пишутся_ключами_и_значениями()
    {
        var перо = new Перо();

        Журнал(перо).Write(new AuditEntry(
            Настройка,
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mail.smtp_host"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["from"] = "smtp.example.com",
                    ["to"] = "чужой.example.com",
                },
            }));

        var строка = перо.Строки.Single();
        строка.ShouldContain("settings.system.update");
        строка.ShouldContain("""{"mail.smtp_host":{"from":"smtp.example.com","to":"чужой.example.com"}}""");
        перо.Поля.Single()["audit_changes"].ShouldBeOfType<string>();
    }

    [Fact]
    public void Отказ_в_праве_пишется_а_разрешение_нет()
    {
        var перо = new Перо();
        var журнал = Журнал(перо);

        журнал.RecordAsync(null, new AuditDecision(true, "users.manage", "суперпользователь", Bypass: true), null, null, Ct);
        перо.Строки.ShouldBeEmpty("разрешений с обходом было бы по строке на каждый экран");

        журнал.RecordAsync(null, new AuditDecision(false, "users.manage", "права нет"), "user", null, Ct);
        перо.Строки.Single().ShouldContain("authz.denied");
    }

    [Fact]
    public void Перевод_строки_в_значении_не_рвёт_строку_лога()
    {
        var изменения = LogAuditWriter.Изменения(
            Настройка,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = "a\naudit поддельная строка" });

        изменения.ShouldNotBeNull().ShouldNotContain('\n');
    }

    [Fact]
    public void Длинные_изменения_обрезаются()
    {
        var изменения = LogAuditWriter.Изменения(
            Настройка,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = new string('я', 10_000) });

        изменения.ShouldNotBeNull().Length.ShouldBe(LogAuditWriter.ПотолокИзменений + 1);
    }

    [Fact]
    public async Task Правка_настроек_пишет_в_журнал_ключ_и_было_стало()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        host.User.SignIn();

        var перо = new Перо();

        await using var фабрика = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddSingleton<ILogger<LogAuditWriter>>(перо);
        }));

        (await фабрика.CreateClient().PatchAsJsonAsync(
                "/api/settings/system",
                new { values = new Dictionary<string, object?> { ["mail.smtp_host"] = "чужой.example.com" } },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var строка = перо.Строки.Single(с => с.Contains("settings.system.update", StringComparison.Ordinal));
        строка.ShouldContain("\"mail.smtp_host\"");
        строка.ShouldContain("чужой.example.com");
    }

    [Fact]
    public async Task Ротация_ключей_хоста_доходит_до_всех_запечатанных()
    {
        await using var scope = host.Services.CreateAsyncScope();

        var цели = scope.ServiceProvider.GetServices<IRewrapTarget>().Select(цель => цель.Name).ToList();

        цели.ShouldContain("mail_outbox", "без этой цели rewrap-keys не трогал ключи писем в очереди");
        цели.ShouldContain("keymanager_state", "без этой цели rewrap-keys не трогал ключ API Key Manager Plus");
    }

    private static LogAuditWriter Журнал(Перо перо) => new(new TestUser(), new ПустойКонтекст(), перо);

    /// <summary>Контекст запроса без запроса: адреса и номера нет.</summary>
    private sealed class ПустойКонтекст : IAuditContextAccessor
    {
        public AuditContext Current { get; } = new();

        public void Bind(AuditContext patch)
        {
        }
    }

    /// <summary>Логгер журнала, запоминающий строки и структурные поля.</summary>
    private sealed class Перо : ILogger<LogAuditWriter>
    {
        public List<string> Строки { get; } = [];

        public List<Dictionary<string, object?>> Поля { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (Строки)
            {
                Строки.Add(formatter(state, exception));
                Поля.Add(state is IEnumerable<KeyValuePair<string, object?>> пары
                    ? пары.ToDictionary(пара => пара.Key, пара => пара.Value, StringComparer.Ordinal)
                    : []);
            }
        }
    }
}
