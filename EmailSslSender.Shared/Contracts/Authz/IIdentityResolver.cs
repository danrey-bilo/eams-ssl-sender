using EmailSslSender.Shared.Contracts.Audit;

namespace EmailSslSender.Shared.Contracts.Authz;

/// <summary>Что распознавателю личности видно в запросе.</summary>
/// <remarks>
/// Не <c>HttpContext</c>: модулю незачем знать про конвейер ASP.NET, а тестам незачем его
/// поднимать.
/// </remarks>
public interface IIdentityRequest
{
    /// <summary>Значение заголовка или <c>null</c>.</summary>
    string? Header(string name);

    /// <summary>Значение куки или <c>null</c>.</summary>
    string? Cookie(string name);
}

/// <summary>Распознанная личность.</summary>
/// <param name="UserId">Чья.</param>
/// <param name="ActorType">Человек или система.</param>
/// <param name="SessionId">Какой сессией пришли.</param>
/// <param name="MustChangePassword">Ограничен до смены пароля.</param>
/// <param name="MustEnrollTwoFactor">Ограничен до подключения второго фактора.</param>
/// <param name="StepUpAt">Когда личность подтверждали последний раз — на этом токене.</param>
public sealed record ResolvedIdentity(
    Guid UserId,
    AuditActorType ActorType,
    Guid? SessionId = null,
    bool MustChangePassword = false,
    bool MustEnrollTwoFactor = false,
    DateTimeOffset? StepUpAt = null);

/// <summary>
/// Кто умеет узнать человека по запросу.
/// </summary>
/// <remarks>
/// Хост опрашивает распознавателей цепочкой: <c>null</c> — «не ко мне, спроси следующего»,
/// исключение — «моё, но негодное». Так новый способ входа встаёт рядом, не трогая прежний.
/// </remarks>
public interface IIdentityResolver
{
    /// <summary>Личность или <c>null</c>, если запрос не к этому распознавателю.</summary>
    Task<ResolvedIdentity?> ResolveAsync(IIdentityRequest request, CancellationToken cancellationToken = default);
}
