using System.Threading.RateLimiting;
using Modules.Core.Composition;
using Modules.Hosting;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Пределы частоты проекта: числа для классов дорогих путей и потолки на всех клиентов вместе.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Предел по адресу — потолок, а не единственная мера.</b> Люди одной организации
/// часто выходят в сеть с одного офисного адреса, и предел «10 входов в минуту на адрес»
/// отказывал бы им по утрам. Поэтому:
///
/// - <b>вход</b> — по адресу потолок <c>LoginPerAddressPerMinute</c> (100) против потока; перебор
///   одной учётной записи держат предел на пару «адрес + почта» в модуле личностей
///   (<c>LoginPerMinute</c>, 10) и блокировка после неудач;
/// - <b>переспрос внутри сессии</b> (код переподтверждения, второй фактор, смена пароля) —
///   предел на человека после опознания, а по адресу потолок в <c>AddressFactor</c> (10) раз выше;
/// - <b>активация и сброс пароля</b> — по адресу: опознавать там некого.
///
/// ⚠ <b>Какие пути дорогие, объявляют модули</b> (правила <see cref="RateLimitRule"/>, решение EAMS
/// 0111), ограничивает набор хостинга (<see cref="ModuleRateLimits"/>). Здесь — только числа и их
/// прежние ключи конфигурации, чтобы установки не потеряли своих значений.
/// </remarks>
internal sealed class RateLimitGroups
{
    private readonly Dictionary<string, int> _всего;
    private readonly int _проба;

    public RateLimitGroups(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        LoginPerMinute = configuration.GetValue("RateLimits:LoginPerMinute", 10);

        var входПоАдресу = configuration.GetValue("RateLimits:LoginPerAddressPerMinute", 100);

        // ⚠ Активация и сброс ограничиваются тоже, и не ради перебора ссылки: 256 бит
        // случайности перебрать нельзя. Причина другая — «забыл пароль» ставит письмо, и
        // поток просьб иначе превратил бы установку в рассыльщика чужими руками.
        var активация = configuration.GetValue("RateLimits:ActivationPerMinute", 20);

        var множитель = Math.Max(1, configuration.GetValue("RateLimits:AddressFactor", 10));

        Policies = new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal)
        {
            [RateLimitClasses.SignIn] = new(входПоАдресу, PerUser: null),
            [RateLimitClasses.Reauthentication] = new(LoginPerMinute * множитель, LoginPerMinute),
            [RateLimitClasses.OneTimeLink] = new(активация, PerUser: null),
        };

        // ⚠ Второе звено — предел на всех клиентов вместе для классов, где запрос считает argon2
        // без входа (ревью безопасности 2026-10-20): предел «на адрес» поток со множества адресов
        // не держит, а память и процессор у сервера одни.
        _всего = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RateLimitClasses.SignIn] = configuration.GetValue("RateLimits:LoginTotalPerMinute", 300),
            [RateLimitClasses.OneTimeLink] = configuration.GetValue("RateLimits:ActivationTotalPerMinute", 60),
        };

        // ⚠ Готовность открыта без входа и каждый раз спрашивает базу (ревью безопасности
        // 2026-10-20). Проба контейнера ходит раз в полминуты с 127.0.0.1 — предел её не задевает.
        _проба = configuration.GetValue("RateLimits:ReadyPerAddressPerMinute", 120);
    }

    /// <summary>Попыток входа в минуту на пару «адрес + почта» — для модуля личностей.</summary>
    public int LoginPerMinute { get; }

    /// <summary>Пределы классов — для ограничителя набора хостинга.</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> Policies { get; }

    /// <summary>
    /// Достроить ограничитель набора хостинга звеньями проекта: потолок на всех и проба готовности.
    /// </summary>
    /// <remarks>
    /// ⚠ Зовётся <b>после</b> <c>AddModuleRateLimits</c>: настройки ограничителя применяются по
    /// порядку, и здесь цепочка надевается на уже собранный ограничитель классов, а не заменяет его.
    /// </remarks>
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
