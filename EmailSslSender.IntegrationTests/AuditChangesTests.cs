using System.Net;
using System.Net.Http.Json;
using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Crypto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Журнал говорит, ЧТО изменилось, — там, где вызывающий поручился, что это можно писать.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Прежде <c>LogAuditWriter</c> выбрасывал <c>Changes</c> целиком: строка
/// «settings.system.update» не говорила, какой ключ и с чего на что, — то есть после подмены
/// <c>mail.smtp_host</c> или <c>keymanager.base_url</c> журнал молчал о главном. Писать всё
/// подряд тоже нельзя: у учётных записей в <c>Changes</c> лежат адреса почты.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class AuditChangesTests(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditAction Событие = new("test.audit");

    [Fact]
    public void Без_поручительства_изменения_в_лог_не_идут()
    {
        var перо = new Перо();

        Журнал(перо).Write(new AuditEntry(
            Событие,
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal) { ["email"] = "кто@example.com" }));

        перо.Строки.Single().ShouldNotContain("кто@example.com");
        перо.Поля.Single().ContainsKey("audit_changes").ShouldBeFalse();
    }

    [Fact]
    public void С_поручительством_изменения_пишутся_ключами_и_значениями()
    {
        var перо = new Перо();

        Журнал(перо).Write(new AuditEntry(
            Событие,
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mail.smtp_host"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["from"] = "smtp.example.com",
                    ["to"] = "чужой.example.com",
                },
            },
            LogChanges: true));

        var строка = перо.Строки.Single();
        строка.ShouldContain("test.audit");
        строка.ShouldContain("""{"mail.smtp_host":{"from":"smtp.example.com","to":"чужой.example.com"}}""");
        перо.Поля.Single()["audit_changes"].ShouldBeOfType<string>();
    }

    [Fact]
    public void Перевод_строки_в_значении_не_рвёт_строку_лога()
    {
        var изменения = LogAuditWriter.Изменения(new AuditEntry(
            Событие,
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = "a\naudit поддельная строка" },
            LogChanges: true));

        изменения.ShouldNotBeNull().ShouldNotContain('\n');
    }

    [Fact]
    public void Длинные_изменения_обрезаются()
    {
        var изменения = LogAuditWriter.Изменения(new AuditEntry(
            Событие,
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = new string('я', 10_000) },
            LogChanges: true));

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
        строка.ShouldContain("\"to\":\"чужой.example.com\"");
    }

    [Fact]
    public async Task Ротация_ключей_хоста_доходит_до_очереди_писем()
    {
        await using var scope = host.Services.CreateAsyncScope();

        scope.ServiceProvider.GetServices<IRewrapTarget>().Select(цель => цель.Name)
            .ShouldContain("mail_outbox", "без этой цели rewrap-keys не трогал ключи писем в очереди");
    }

    private static LogAuditWriter Журнал(Перо перо) => new(new TestUser(), new HttpContextAccessor(), перо);

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
