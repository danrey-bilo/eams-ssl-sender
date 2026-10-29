using System.Threading.RateLimiting;
using Modules.Core.Composition;
using Modules.Hosting;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Числа пределов для классов дорогих путей и потолки на всех клиентов вместе.</summary>
/// <remarks>Предел по адресу — лишь потолок: офис выходит в сеть с одного адреса.</remarks>
internal sealed class RateLimitGroups
{
    private readonly Dictionary<string, int> _всего;
    private readonly int _проба;

    public RateLimitGroups(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        LoginPerMinute = configuration.GetValue("RateLimits:LoginPerMinute", 10);

        var входПоАдресу = configuration.GetValue("RateLimits:LoginPerAddressPerMinute", 100);

        // «Забыл пароль» ставит письмо — без предела установка стала бы рассыльщиком.
        var активация = configuration.GetValue("RateLimits:ActivationPerMinute", 20);

        var множитель = Math.Max(1, configuration.GetValue("RateLimits:AddressFactor", 10));

        Policies = new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal)
        {
            [RateLimitClasses.SignIn] = new(входПоАдресу, PerUser: null),
            [RateLimitClasses.Reauthentication] = new(LoginPerMinute * множитель, LoginPerMinute),
            [RateLimitClasses.OneTimeLink] = new(активация, PerUser: null),

            // Выгрузка журнала держит строки в памяти разом.
            [RateLimitClasses.BulkTransfer] = new(
                configuration.GetValue("RateLimits:BulkTransferPerAddressPerMinute", 10),
                configuration.GetValue("RateLimits:BulkTransferPerUserPerMinute", 5)),
        };

        // ⚠ Предел на всех вместе там, где argon2 считается без входа: поток со многих адресов.
        _всего = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RateLimitClasses.SignIn] = configuration.GetValue("RateLimits:LoginTotalPerMinute", 300),
            [RateLimitClasses.OneTimeLink] = configuration.GetValue("RateLimits:ActivationTotalPerMinute", 60),
        };

        // Готовность открыта без входа и спрашивает базу.
        _проба = configuration.GetValue("RateLimits:ReadyPerAddressPerMinute", 120);
    }

    /// <summary>Попыток входа в минуту на пару «адрес + почта» — для модуля личностей.</summary>
    public int LoginPerMinute { get; }

    /// <summary>Пределы классов — для ограничителя набора хостинга.</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> Policies { get; }

    /// <summary>Достроить ограничитель хостинга потолком на всех и пробой готовности.</summary>
    /// <remarks>⚠ Только после <c>AddModuleRateLimits</c>: цепочка надевается на собранный ограничитель.</remarks>
    public void ChainProjectLimits(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(limiter =>
        {
            var классы = limiter.GlobalLimiter
                ?? throw new InvalidOperationException(
                    "Ограничитель классов не собран: AddModuleRateLimits должен стоять раньше.");

            var всего = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.RequestServices.GetRequiredService<ModuleRateLimits>().Of(context.Request.Path) is { } найдено
                && _всего.TryGetValue(найдено.Class, out var предел)
                    ? RateLimitPartition.GetFixedWindowLimiter("всего:" + найдено.Class, _ => Окно(предел))
                    : RateLimitPartition.GetNoLimiter("прочее"));

            var проба = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api/ready", StringComparison.OrdinalIgnoreCase)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        "проба:" + ModuleRateLimits.AddressKey(context.Connection.RemoteIpAddress), _ => Окно(_проба))
                    : RateLimitPartition.GetNoLimiter("прочее"));

            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(классы, всего, проба);
        });
    }

    private static FixedWindowRateLimiterOptions Окно(int предел) => new()
    {
        PermitLimit = предел,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}
