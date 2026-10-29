using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Audit;
using Npgsql;

namespace EmailSslSender.IntegrationTests;

/// <summary>Журнал действий в базе: пишется, читается, не правится задним числом, цепочка сходится.</summary>
[Collection(SenderHost.Collection)]
public sealed class AuditJournalTests(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Действие_пишется_в_журнал_и_читается_через_API()
    {
        host.User.SignIn();
        var клиент = host.CreateClient();

        var создано = await клиент.PostAsJsonAsync(
            "/api/certificate-subscriptions",
            new { common_name = "journal.example.com", recipients = new[] { "it@example.com" } },
            Ct);
        создано.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await создано.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();

        try
        {
            var журнал = await клиент.GetFromJsonAsync<JsonNode>(
                $"/api/audit?action=keymanager.subscription_created&resource_id={id}", Ct);

            журнал!.ToJsonString().ShouldContain(id.ToString());
            журнал.ToJsonString().ShouldNotContain("it@example.com", Case.Sensitive, "адреса получателей в журнал не идут");
        }
        finally
        {
            await клиент.DeleteAsync($"/api/certificate-subscriptions/{id}", CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("UPDATE audit_log SET reason = 'подмена'")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("TRUNCATE audit_log")]
    public async Task Журнал_задним_числом_не_правится_и_не_стирается(string sql)
    {
        // Хоть одна строка для UPDATE и DELETE.
        host.User.SignIn();
        (await host.CreateClient().PostAsJsonAsync("/api/certificate-subscriptions", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var db = new NpgsqlConnection(host.Postgres);
        await db.OpenAsync(Ct);

        // ⚠ Владельцем схемы: неизменяемость держат триггеры, а не права роли.
        await using var команда = new NpgsqlCommand(sql, db);
        await Should.ThrowAsync<PostgresException>(() => команда.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Цепочка_журнала_сходится()
    {
        host.User.SignIn();
        await host.CreateClient().GetAsync("/api/certificate-subscriptions", Ct);

        var итог = await AuditModule.CheckChainAsync(host.Services, cancellationToken: Ct);

        итог.Ok.ShouldBeTrue(string.Join("; ", итог.Lines.Where(л => !л.Ok).Select(л => $"{л.Partition}: {л.Broken}")));
        итог.Lines.ShouldNotBeEmpty();
    }
}
