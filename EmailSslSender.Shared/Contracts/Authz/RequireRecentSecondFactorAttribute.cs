namespace EmailSslSender.Shared.Contracts.Authz;

/// <summary>
/// Действие требует кода второго фактора, подтверждённого в этой сессии за последние пять минут.
/// </summary>
/// <remarks>
/// Ставится на действия, после которых установку не узнать: пароль SMTP и проверочное письмо
/// (подменивший почтовый сервер получает все коды входа и ссылки сброса), заведение,
/// отключение и удаление людей. Проверяет фильтр хоста по <see cref="ICurrentUser.StepUpAt"/>;
/// подтвердить личность — <c>POST /api/me/step-up</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireRecentSecondFactorAttribute : Attribute;
