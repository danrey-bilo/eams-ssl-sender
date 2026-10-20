using System.Collections.Concurrent;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Единственная дверь к каждому действию: вошёл ли, не ограничен ли, свеж ли второй фактор.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Перенесён из EAMS (<c>PermissionFilter</c>) без проверки прав</b> — ролей и прав в
/// этой установке нет, каждая учётная запись — администратор. Остались три вопроса, и порядок
/// их несущий:
///
/// <list type="number">
/// <item><b>DEFAULT DENY.</b> Действие без <see cref="PublicEndpointAttribute"/> требует входа.
/// Забытая пометка даёт 401 громко и сразу; обратное умолчание отдавало бы данные молча.</item>
/// <item><b>Ограничения личности</b> — смена пароля и подключение второго фактора. Пока они
/// действуют, открыто только помеченное <see cref="AllowWhilePasswordChangeRequiredAttribute"/>
/// и <see cref="AllowWhileTwoFactorEnrollmentRequiredAttribute"/>.</item>
/// <item><b>Свежий второй фактор</b> на действиях с <see cref="RequireRecentSecondFactorAttribute"/>:
/// код, подтверждённый в ЭТОЙ сессии за последние пять минут.</item>
/// </list>
/// </remarks>
internal sealed class AuthenticationFilter(ICurrentUser user, TimeProvider clock) : IAsyncAuthorizationFilter
{
    /// <summary>Сколько живёт подтверждение второго фактора для опасных действий.</summary>
    internal static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(5);

    private sealed record Объявление(
        bool Открыт,
        bool ПриСменеПароля,
        bool ПриВключенииФактора,
        bool СвежийФактор);

    /// <summary>
    /// Разобранные пометки действий.
    /// </summary>
    /// <remarks>
    /// Пометки известны при старте и не меняются, а фильтр стоит на пути каждого запроса —
    /// разбирать их заново на каждом незачем.
    /// </remarks>
    private static readonly ConcurrentDictionary<ActionDescriptor, Объявление> Разобранные = new();

    private static Объявление Разобрать(ActionDescriptor descriptor)
    {
        var открыт = false;
        var приСменеПароля = false;
        var приВключенииФактора = false;
        var свежийФактор = false;

        foreach (var пометка in descriptor.EndpointMetadata)
        {
            switch (пометка)
            {
                case PublicEndpointAttribute:
                    открыт = true;
                    break;
                case AllowWhilePasswordChangeRequiredAttribute:
                    приСменеПароля = true;
                    break;
                case AllowWhileTwoFactorEnrollmentRequiredAttribute:
                    приВключенииФактора = true;
                    break;
                case RequireRecentSecondFactorAttribute:
                    свежийФактор = true;
                    break;
                default:
                    break;
            }
        }

        return new Объявление(открыт, приСменеПароля, приВключенииФактора, свежийФактор);
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var объявлено = Разобранные.GetOrAdd(context.ActionDescriptor, Разобрать);

        if (объявлено.Открыт)
        {
            return Task.CompletedTask;
        }

        // мутация 2026-10-20: условие ослаблено до никогда-не-истины → покраснели
        // ЗакрытоПоУмолчанию.Каждое_действие_без_пометки_отвечает_невошедшему_401 и
        // ЗакрытоПоУмолчанию.Действие_без_пометки_закрыто_для_невошедшего.
        if (user.Id is null)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", System.Net.HttpStatusCode.Unauthorized);
        }

        // ⚠ Ограничение проверяется ДО всего остального. Человек, которому система велела
        // сменить пароль, не должен ничего успеть: пароль ему выдал администратор, и до замены
        // он временный. Исключения объявляют модули пометкой на своих эндпоинтах.
        if (user.MustChangePassword && !объявлено.ПриСменеПароля)
        {
            throw new AppException(
                ErrorCodes.PasswordChangeRequired,
                "Сначала смените пароль",
                System.Net.HttpStatusCode.Forbidden);
        }

        // Второе ограничение того же рода: сессия без подтверждённого фактора — сессия
        // настройки (первый вход), ей открыто только подключение аутентификатора.
        if (user.MustEnrollTwoFactor && !объявлено.ПриВключенииФактора)
        {
            throw new AppException(
                ErrorCodes.TwoFactorEnrollmentRequired,
                "Сначала подключите приложение-аутентификатор",
                System.Net.HttpStatusCode.Forbidden);
        }

        // ⚠ Отметка берётся с ТОКЕНА этой сессии, а не с человека: переподтверждение в одной
        // вкладке не открывает опасное в остальных.
        if (объявлено.СвежийФактор
            && (user.StepUpAt is null || user.StepUpAt < clock.GetUtcNow() - StepUpWindow))
        {
            throw new AppException(
                ErrorCodes.SecondFactorRequired,
                "Подтвердите личность кодом второго фактора.",
                System.Net.HttpStatusCode.Forbidden);
        }

        return Task.CompletedTask;
    }
}
