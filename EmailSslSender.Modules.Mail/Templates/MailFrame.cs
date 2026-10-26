using EmailSslSender.Shared;
using EmailSslSender.Shared.Contracts.Mail;

namespace EmailSslSender.Modules.Mail.Templates;

/// <summary>
/// Рамка каждого письма: приветствие, суть, благодарность и имя сервиса.
/// </summary>
/// <remarks>
/// Решение владельца 2026-10-26: письмо — человеческое, как уведомления самого Key Manager Plus
/// («Hi», что произошло, «Thank You,» и имя сервиса). Рамка одна на все виды писем: тексты модулей
/// несут только суть, и новое письмо не может забыть приветствие или подпись.
/// </remarks>
internal static class MailFrame
{
    /// <summary>Обернуть тело письма рамкой на языке шаблона.</summary>
    public static string Wrap(string body, string locale)
    {
        ArgumentNullException.ThrowIfNull(body);

        var (привет, спасибо) = locale switch
        {
            MailLocales.Ru => ("Здравствуйте!", "Спасибо,"),
            MailLocales.Uk => ("Вітаємо!", "Дякуємо,"),
            _ => ("Hi,", "Thank You,"),
        };

        return $"{привет}\n\n{body.Trim('\n')}\n\n{спасибо}\n{Product.Name}\n";
    }
}
