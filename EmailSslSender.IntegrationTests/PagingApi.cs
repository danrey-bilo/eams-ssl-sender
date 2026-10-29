using System.Net;
using System.Text.Json;
using Modules.Core.Contracts.Audit;

namespace EmailSslSender.IntegrationTests;

/// <summary>Размер страницы: предел вне допустимого — 422 на всех выдачах с курсором, без подрезки.</summary>
[Collection(SenderHost.Collection)]
public sealed class PagingApi(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Выдачи с курсором и их права.</summary>
    /// <remarks>⚠ Новая постраничная выдача — новая строка здесь.</remarks>
    public static TheoryData<string, string[]> Выдачи() => new()
    {
        { "/api/users", [] },
    };

    [Theory]
    [MemberData(nameof(Выдачи))]
    public async Task Предел_вне_допустимого_отвергается_везде_одинаково(string адрес, string[] права)
    {
        Войти(права);

        foreach (var предел in new[] { 0, -1, 100_000 })
        {
            var ответ = await host.CreateClient().GetAsync($"{адрес}?limit={предел}", Ct);

            ответ.StatusCode.ShouldBe(
                HttpStatusCode.UnprocessableContent,
                $"{адрес}?limit={предел} — предел вне допустимого обязан отвергаться, а не "
                + "подрезаться молча");

            var тело = JsonDocument.Parse(await ответ.Content.ReadAsStringAsync(Ct));

            тело.RootElement.GetProperty("code").GetString().ShouldBe("VALIDATION_ERROR", адрес);

            // Сообщение называет допустимый диапазон.
            тело.RootElement.GetProperty("message").GetString()
                .ShouldNotBeNull(адрес)
                .ShouldContain("до", Case.Sensitive, адрес);
        }
    }

    [Theory]
    [MemberData(nameof(Выдачи))]
    public async Task Предел_внутри_допустимого_и_его_отсутствие_работают(string адрес, string[] права)
    {
        Войти(права);

        foreach (var запрос in new[] { string.Empty, "?limit=1", "?limit=20" })
        {
            var ответ = await host.CreateClient().GetAsync($"{адрес}{запрос}", Ct);

            ответ.StatusCode.ShouldBe(HttpStatusCode.OK, $"{адрес}{запрос}");
        }
    }

    private void Войти(string[] права)
    {
        host.User.SignIn();

        // Журнал пишет и само чтение — нужен вид актора.
        host.User.ActorType = AuditActorType.User;
    }
}
