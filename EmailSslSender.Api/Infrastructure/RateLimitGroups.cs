using System.Globalization;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Дорогие пути и их пределы — одна классификация на оба слоя: по адресу и по человеку.
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
///   предел на человека после опознания (<see cref="UserRateLimitMiddleware"/>), а по адресу
///   потолок в <c>AddressFactor</c> (10) раз выше;
/// - <b>активация и сброс пароля</b> — по адресу: опознавать там некого.
///
/// Какие пути дорогие — знание хоста, а не модуля: атрибут на действии потребовал бы, чтобы модуль
/// знал имя политики хоста.
/// </remarks>
internal sealed class RateLimitGroups
{
    /// <summary>Группа пути: имя, предел по адресу и предел на человека, если он есть.</summary>
    /// <param name="Name">Имя группы — часть ключа счётчика.</param>
    /// <param name="PerAddress">Запросов в минуту с одного адреса (IPv6 — с сети /64).</param>
    /// <param name="PerUser">Запросов в минуту на учётную запись; <c>null</c> — не считается.</param>
    /// <param name="Total">
    /// Предел на всех клиентов вместе. ⚠ Анонимные вход и сброс считают argon2 (64 МиБ на
    /// хеш): поток со множества адресов иначе съедал бы память и процессор сервера целиком.
    /// </param>
    public sealed record Group(string Name, int PerAddress, int? PerUser, int? Total = null);

    private readonly Group _вход;
    private readonly Group _переспрос;
    private readonly Group _активация;
    private readonly Group _проба;

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

        _вход = new Group(
            "вход", входПоАдресу, PerUser: null,
            Total: configuration.GetValue("RateLimits:LoginTotalPerMinute", 300));
        _переспрос = new Group("переспрос", LoginPerMinute * множитель, LoginPerMinute);

        // ⚠ Готовность открыта без входа и каждый раз спрашивает базу (ревью безопасности
        // 2026-10-20). Проба контейнера ходит раз в полминуты с 127.0.0.1 — предел её не задевает.
        _проба = new Group("проба", configuration.GetValue("RateLimits:ReadyPerAddressPerMinute", 120), PerUser: null);
        _активация = new Group(
            "активация", активация, PerUser: null,
            Total: configuration.GetValue("RateLimits:ActivationTotalPerMinute", 60));
    }

    /// <summary>Попыток входа в минуту на пару «адрес + почта» — для модуля личностей.</summary>
    public int LoginPerMinute { get; }

    /// <summary>Группа пути или <c>null</c>, если путь не дорогой.</summary>
    public Group? Of(PathString path)
    {
        // ⚠ Второй фактор входа — в той же группе, что и вход: иначе перебор кода упирался бы
        // лишь в блокировку учётной записи.
        if (path.StartsWithSegments("/api/auth/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/auth/two-factor", StringComparison.OrdinalIgnoreCase))
        {
            return _вход;
        }

        // ⚠ ⚠ Две двери доказательства личности ВНУТРИ открытой сессии (ревью 2026-09-15, S2):
        // код переподтверждения и текущий пароль при смене. Счётчик учётной записи их уже
        // держит; предел дополняет его. Своя группа — чтобы подбор здесь не выжигал квоту формы
        // входа и наоборот.
        // мутация 2026-09-15: оба пути подменены несуществующими → покраснели оба случая
        // AuthApi.Переспрос_внутри_сессии_режется_частотой.
        if (path.StartsWithSegments("/api/me/step-up", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/me/two-factor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/me/password", StringComparison.OrdinalIgnoreCase))
        {
            return _переспрос;
        }

        if (path.StartsWithSegments("/api/ready", StringComparison.OrdinalIgnoreCase))
        {
            return _проба;
        }

        return path.StartsWithSegments("/api/activation", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/password-reset", StringComparison.OrdinalIgnoreCase)
                ? _активация
                : null;
    }

    /// <summary>Ответить 429 тем же конвертом, что и всё остальное.</summary>
    /// <remarks>
    /// По умолчанию отказ ограничителя — 503, и это ровно тот случай, когда умолчание вводит в
    /// заблуждение: клиент решит, что сервер лежит, и начнёт повторять.
    /// </remarks>
    public static async Task RejectAsync(HttpContext context, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (retryAfter is { } через)
        {
            context.Response.Headers.RetryAfter = ((int)через.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        await context.Response.WriteAsJsonAsync(
            new ErrorResponse(
                ErrorCodes.RateLimited,
                "Слишком много попыток. Подождите немного.",
                null,
                context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var id) ? id as string ?? string.Empty : string.Empty),
            cancellationToken);
    }
}
