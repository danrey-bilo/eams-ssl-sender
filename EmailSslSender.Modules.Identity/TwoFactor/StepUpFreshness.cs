using System.Net;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Свеж ли второй фактор у этой сессии: отметка токена плюс окно <c>auth.step_up_minutes</c>.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Окно названо в ОДНОМ месте — здесь.</b> По нему же отказ называет
/// <c>window_minutes</c>, а <c>GET /api/me</c> и <c>POST /api/me/step-up</c> — момент
/// <c>step_up_until</c>. Второе место, где написано, сколько живёт подтверждение, разошлось бы с
/// первым молча: экран обещал бы ещё пять минут, а сервер уже отказывал бы.
///
/// ⚠ <b>Отметка — с токена, и пересчитывать её здесь нечего.</b> Ставят её вход с кодом
/// (<c>SignInService</c>), подключение аутентификатора (<c>TwoFactorService.ConfirmAsync</c>) и
/// <see cref="StepUpService"/>; читается она из личности запроса как есть.
///
/// Граница включительная: отметка ровно окно назад ещё годна, мгновением позже — уже нет.
/// </remarks>
internal sealed class StepUpFreshness(
    ICurrentUser current,
    ISettingsReader settings,
    TimeProvider clock) : IStepUpFreshness
{
    public async Task RequireAsync(CancellationToken cancellationToken = default)
    {
        var минут = await МинутAsync(cancellationToken);

        if (current.StepUpAt is { } когда && clock.GetUtcNow() <= когда.AddMinutes(минут))
        {
            return;
        }

        throw new AppException(
            ErrorCodes.SecondFactorRequired,
            "Подтвердите действие кодом из приложения.",
            HttpStatusCode.Forbidden,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["window_minutes"] = минут });
    }

    /// <summary>
    /// До какого момента отметка открывает опасные действия.
    /// </summary>
    /// <param name="stepUpAt">Когда подтвердили на этой сессии; <c>null</c> — не подтверждали.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>
    /// Момент конца окна — или <c>null</c>, если подтверждения нет или окно уже прошло: экран
    /// показывает «код действует до …» только тогда, когда это правда прямо сейчас.
    /// </returns>
    public async Task<DateTimeOffset?> UntilAsync(DateTimeOffset? stepUpAt, CancellationToken cancellationToken = default)
    {
        if (stepUpAt is not { } когда)
        {
            return null;
        }

        var до = когда.AddMinutes(await МинутAsync(cancellationToken));

        return clock.GetUtcNow() <= до ? до : null;
    }

    /// <summary>
    /// Окно в минутах — не меньше одной.
    /// </summary>
    /// <remarks>
    /// Границы ключа держит справочник настроек; нижняя повторена здесь затем, чтобы негодное
    /// значение из подставного читателя или старой строки не превратило окно в «никогда» и не
    /// заперло все опасные действия разом.
    /// </remarks>
    private async Task<int> МинутAsync(CancellationToken cancellationToken) =>
        Math.Max(1, await settings.GetAsync(IdentitySettings.StepUpMinutes, cancellationToken));
}
