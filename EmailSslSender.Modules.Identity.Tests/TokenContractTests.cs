using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Сборка без модуля токенов падает при СТАРТЕ, а не при первом входе (этап 10.5.5).
/// </summary>
/// <remarks>
/// ⚠ <b>Ради этого свойства проверка и заведена.</b> С этапа 10.5 модуль личностей не выдаёт
/// токенов сам (решение 0017). Забудь хост подключить модуль токенов — и система <b>поднимется
/// как ни в чём не бывало</b>: ответит на живость, на готовность, отдаст документ OpenAPI. И
/// умрёт на первом <c>POST /api/auth/login</c>, то есть в бою и на людях.
///
/// Отказ при старте стоит минуту. Тот же приём и по той же причине стоит у мастер-ключа и у
/// замка единственного экземпляра.
///
/// Без базы и без хоста: проверяется разрешение служб, и поднимать ради этого контейнер
/// значило бы платить контейнером за три <c>if</c>.
/// </remarks>
public sealed class TokenContractTests
{
    [Fact]
    public async Task Без_модуля_токенов_старт_падает_и_объясняет()
    {
        var отказ = await Should.ThrowAsync<InvalidOperationException>(
            () => Проверка(new ServiceCollection()).StartAsync(Ct));

        // Отказ обязан говорить, ЧТО сломается и ЧТО дописать: читать его будет тот, кто
        // собирает хост, и «служба не зарегистрирована» ему ничего не объяснит.
        отказ.Message.ShouldContain("ITokenIssuer");
        отказ.Message.ShouldContain("войти будет нельзя");
        отказ.Message.ShouldContain("AddTokensModule");
    }

    /// <summary>
    /// Названы все три, а не только первый.
    /// </summary>
    /// <remarks>
    /// ⚠ Проверка, останавливающаяся на первом отсутствующем контракте, заставила бы
    /// собирающего хост чинить их по одному, перезапуская систему трижды. Здесь она
    /// проверяет каждый — и последствие у каждого своё.
    /// </remarks>
    [Theory]
    [InlineData("ITokenRevoker", "смена пароля")]
    [InlineData("ITokenIntrospector", "некому проверить")]
    [InlineData("IStepUpMarker", "подтверждение второго фактора")]
    public async Task Каждый_недостающий_контракт_называется_отдельно(string имя, string последствие)
    {
        var services = new ServiceCollection();

        // Даём всё, кроме проверяемого: иначе отказ остановился бы на первом.
        if (имя != "ITokenIssuer")
        {
            services.AddSingleton<ITokenIssuer, НетТокенов>();
        }

        if (имя != "ITokenRevoker")
        {
            services.AddSingleton<ITokenRevoker, НетТокенов>();
        }

        if (имя != "ITokenIntrospector")
        {
            services.AddSingleton<ITokenIntrospector, НетТокенов>();
        }
        if (имя != "IStepUpMarker") services.AddSingleton<IStepUpMarker, НетТокенов>();

        var отказ = await Should.ThrowAsync<InvalidOperationException>(
            () => Проверка(services).StartAsync(Ct));

        отказ.Message.ShouldContain(имя);
        отказ.Message.ShouldContain(последствие);
    }

    /// <summary>А с контрактами старт молчит — иначе проверка отвергала бы всё подряд.</summary>
    [Fact]
    public async Task С_контрактами_старт_молчит()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ITokenIssuer, НетТокенов>();
        services.AddSingleton<ITokenRevoker, НетТокенов>();
        services.AddSingleton<ITokenIntrospector, НетТокенов>();
        services.AddSingleton<IStepUpMarker, НетТокенов>();

        await Should.NotThrowAsync(() => Проверка(services).StartAsync(Ct));
    }

    private static TokenContractCheck Проверка(IServiceCollection services) =>
        new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Заглушки: проверке важно только, что реализация есть.</summary>
    private sealed class НетТокенов : ITokenIssuer, ITokenRevoker, ITokenIntrospector, IStepUpMarker
    {
        public Task<TokenPair> IssueAsync(
            TokenIssueRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TokenPair> RotateAsync(
            string refresh, RequestOrigin origin, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RevokeAsync(Guid tokenId, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> RevokeAllAsync(
            Guid userId, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> RevokeOthersAsync(
            Guid userId, Guid? keep, string reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public bool Recognises(string presented) => false;

        public Task<bool> MarkAsync(Guid tokenId, DateTimeOffset at, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<TokenIdentity?> IntrospectAsync(
            string presented, CancellationToken cancellationToken = default) =>
            Task.FromResult<TokenIdentity?>(null);
    }
}
