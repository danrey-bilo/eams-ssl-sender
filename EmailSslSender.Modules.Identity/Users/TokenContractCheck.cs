using EmailSslSender.Shared.Contracts.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>
/// Проверяет при старте, что выдавать и гасить токены есть кому.
/// </summary>
/// <remarks>
/// ⚠ <b>С этапа 10.5 модуль личностей не выдаёт токенов сам</b> (решение 0017): вход
/// проверяет пароль и зовёт <see cref="ITokenIssuer"/>, смена пароля зовёт
/// <see cref="ITokenRevoker"/>. Забудь хост подключить модуль токенов — и система
/// поднимется, ответит на живость и на готовность, отдаст документ OpenAPI, а умрёт на
/// первом <c>POST /api/auth/login</c>. То есть в бою и на людях.
///
/// Отказ при старте стоит минуту. Тот же приём и по той же причине стоит у мастер-ключа и у
/// замка единственного экземпляра.
/// </remarks>
internal sealed class TokenContractCheck(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();

        Require<ITokenIssuer>(scope.ServiceProvider, "войти будет нельзя");
        Require<ITokenRevoker>(scope.ServiceProvider, "смена пароля не выгонит прежние сессии");
        Require<ITokenIntrospector>(scope.ServiceProvider, "предъявленный токен некому проверить");
        Require<IStepUpMarker>(scope.ServiceProvider, "подтверждение второго фактора не завершит вход");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Require<T>(IServiceProvider services, string последствие)
        where T : class
    {
        if (services.GetService<T>() is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Модуль личностей подключён, а {typeof(T).Name} не даёт никто — {последствие}. "
            + "Токены принадлежат модулю Tokens: допишите AddTokensModule() в Program.cs "
            + "после AddIdentityModule().");
    }
}
