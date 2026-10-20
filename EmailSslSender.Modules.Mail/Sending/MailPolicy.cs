using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>
/// Восемь настроек почты, свёрнутые в одно значение.
/// </summary>
/// <param name="Enabled">Включена ли отправка вообще.</param>
/// <param name="Host">Сервер исходящей почты.</param>
/// <param name="Port">Порт.</param>
/// <param name="Security">Как защищено соединение: <c>starttls</c>, <c>tls</c>, <c>none</c>.</param>
/// <param name="Username">Учётная запись SMTP. Пусто — реле без авторизации.</param>
/// <param name="FromName">Имя отправителя.</param>
/// <param name="FromAddress">Адрес отправителя. Пусто — почта не настроена.</param>
/// <param name="RatePerHour">Сколько писем в час на адрес и вид.</param>
/// <param name="MaxAttempts">Сколько раз пробовать доставить.</param>
/// <remarks>
/// ⚠ <b>Читается ОДНИМ проходом, а не по ключу на месте использования.</b> Настройки читаются на
/// горячем пути; девять отдельных обращений в разных местах превратились бы в восемь чтений
/// снимка — а собранные вместе они образуют осмысленное целое, у которого есть свойство
/// «настроено».
/// </remarks>
internal sealed record MailPolicy(
    bool Enabled,
    string? Host,
    int Port,
    string Security,
    string? Username,
    string FromName,
    string? FromAddress,
    int RatePerHour,
    int MaxAttempts)
{
    /// <summary>
    /// Настроена ли почта настолько, чтобы письмо имело шанс уйти.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Вычисляется, а не хранится настройкой.</b> Отдельный признак «настроено» разошёлся
    /// бы с действительностью в тот момент, когда администратор стёр адрес сервера, не сняв
    /// галочку: система считала бы себя настроенной и предлагала бы код письмом.
    ///
    /// ⚠ <b>Пароль в условие НЕ входит</b>, и это не пропуск: реле в той же сети принимает
    /// почту без авторизации, и требовать пароль значило бы запретить законную установку.
    /// </remarks>
    /// <remarks>
    /// ⚠ <b>Адрес отправителя входит в условие с вскрытия 44.</b> Прежде его не было вовсе и
    /// он угадывался из учётной записи SMTP; угаданный адрес даёт либо невалидный <c>From</c>
    /// (пользователь провайдера — обычно не адрес), либо письмо «от» домена получателя — то
    /// есть отказ SPF. Не задан адрес — почта считается ненастроенной, и это честнее, чем
    /// письмо, которое молча съест спам-фильтр.
    /// </remarks>
    public bool Configured =>
        Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    /// <summary>Прочитать все девять.</summary>
    public static async Task<MailPolicy> ReadAsync(
        ISettingsReader settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new MailPolicy(
            await settings.GetAsync(MailSettings.Enabled, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpHost, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpPort, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpSecurity, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpUsername, cancellationToken),
            await settings.GetAsync(MailSettings.FromName, cancellationToken),
            await settings.GetAsync(MailSettings.FromAddress, cancellationToken),
            await settings.GetAsync(MailSettings.RatePerHour, cancellationToken),
            await settings.GetAsync(MailSettings.MaxAttempts, cancellationToken));
    }
}
