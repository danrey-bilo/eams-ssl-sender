using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Core.Contracts.Settings;

namespace EmailSslSender.IntegrationTests;

/// <summary>Язык интерфейса — у каждого свой, по умолчанию язык установки.</summary>
[Collection(SenderHost.Collection)]
public sealed class LanguageApi(SenderHost host)
{
    private const string Адрес = "/api/me/preferences";

    [Fact]
    public async Task Кто_не_выбирал_видит_русский()
    {
        host.User.SignIn();

        (await ЯзыкAsync()).ShouldBe("ru");
    }

    [Fact]
    public async Task Свой_язык_сохраняется_и_не_трогает_других()
    {
        host.User.SignIn();

        var ответ = await host.CreateClient().PatchAsJsonAsync(Адрес, Выбрать("uk"), Ct);

        ответ.IsSuccessStatusCode.ShouldBeTrue(await ответ.Content.ReadAsStringAsync(Ct));
        (await ЯзыкAsync()).ShouldBe("uk");

        host.User.SignIn();

        (await ЯзыкAsync()).ShouldBe("ru");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("")]
    public async Task Чужой_язык_не_принимается(string язык)
    {
        host.User.SignIn();

        var ответ = await host.CreateClient().PatchAsJsonAsync(Адрес, Выбрать(язык), Ct);

        ответ.IsSuccessStatusCode.ShouldBeFalse();
        (await ЯзыкAsync()).ShouldBe("ru");
    }

    [Fact]
    public async Task Письма_человеку_идут_на_его_языке()
    {
        // Приглашение и сброс пароля берут язык отсюда.
        var кто = host.User.SignIn();

        (await host.CreateClient().PatchAsJsonAsync(Адрес, Выбрать("en"), Ct)).EnsureSuccessStatusCode();

        await using var scope = host.Services.CreateAsyncScope();
        var языки = scope.ServiceProvider.GetRequiredService<IUserLocale>();

        (await языки.ForAsync(кто, Ct)).ShouldBe("en");
        (await языки.ForAsync(Guid.CreateVersion7(), Ct)).ShouldBe("ru");
    }

    private static object Выбрать(string язык) => new { values = new Dictionary<string, string> { ["ui.locale"] = язык } };

    private async Task<string> ЯзыкAsync()
    {
        var предпочтения = await host.CreateClient().GetFromJsonAsync<JsonObject>(Адрес, Ct);

        return предпочтения!["effective"]!["ui.locale"]!.GetValue<string>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
