namespace EmailSslSender.Shared.Contracts.Authz;

/// <summary>
/// Свеж ли второй фактор у сессии, пришедшей с этим запросом.
/// </summary>
/// <remarks>
/// <b>Живёт в общей библиотеке, потому что спрашивают двое, а отвечает третий.</b> Спрашивают
/// фильтр хоста (действия с <see cref="RequireRecentSecondFactorAttribute"/>) и модуль настроек
/// (правка ключей защиты); отвечает модуль личностей — окно <c>auth.step_up_minutes</c> его
/// настройка. Ссылаться на модуль личностей не может ни один из спрашивающих.
///
/// ⚠ ⚠ <b>Отметка — с ТОКЕНА этой сессии</b> (<see cref="ICurrentUser.StepUpAt"/>), а не с
/// человека: код, введённый в одной вкладке, не открывает опасное в остальных его сессиях, а
/// украденная живая сессия не оживает оттого, что законный владелец ввёл код у себя.
/// </remarks>
public interface IStepUpFreshness
{
    /// <summary>
    /// Пропустить, если код подтверждён на этой сессии в пределах окна; иначе отказать.
    /// </summary>
    /// <exception cref="Errors.AppException">
    /// <c>403 SECOND_FACTOR_REQUIRED</c>, в подробностях <c>window_minutes</c> — сколько живёт
    /// подтверждение. Подтвердить — <c>POST /api/me/step-up</c> с кодом из приложения.
    /// </exception>
    Task RequireAsync(CancellationToken cancellationToken = default);
}
