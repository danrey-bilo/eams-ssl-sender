using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;

namespace EmailSslSender.Modules.Mail.Secrets;

/// <summary>Привязка пароля SMTP, заданного до привязки, к текущим настройкам почты — для <c>migrate</c>.</summary>
internal sealed class SmtpPasswordLegacyBinding(SystemSecrets secrets, ISettingsReader settings) : ILegacySecretBinder
{
    public string Name => "пароль SMTP";

    public async Task<bool> BindAsync(CancellationToken cancellationToken = default)
    {
        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        // Сервера нет — привязывать не к чему: пароль откроется после ввода заново.
        if (string.IsNullOrWhiteSpace(политика.Host))
        {
            return false;
        }

        return await secrets.BindLegacyAsync(SystemSecretNames.SmtpPassword, SmtpSender.Цель(политика), cancellationToken);
    }
}
