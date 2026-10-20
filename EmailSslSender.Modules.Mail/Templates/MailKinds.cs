using EmailSslSender.Shared.Contracts.Mail;

namespace EmailSslSender.Modules.Mail.Templates;

/// <summary>
/// Виды писем, которые объявляет сам модуль почты.
/// </summary>
/// <remarks>
/// ⚠ <b>Один, и это правильное число.</b> Все остальные письма — приглашение, сброс пароля,
/// код входа, блокировка — объявляет <c>Identity</c>: их шлёт он, и тексты меняются вместе с
/// его поведением. Проверочное письмо принадлежит почте, потому что проверяет саму почту.
///
/// ⚠ <b>Имя вида — часть контракта с базой:</b> оно ложится в <c>mail_outbox.kind</c> и в ключ
/// предела частоты. Переименование сделало бы прежние строки очереди неотправляемыми.
/// </remarks>
// ⚠ `internal`, а не `public`: открытый тип модуля становится его контрактом — сослаться
// на него можно будет сразу, а убрать уже нет (правило 1, `ModuleSurface`). Наружу этот вид
// не нужен: свои письма объявляет тот, кто их шлёт, а имя вида ложится в базу строкой.
internal static class MailKinds
{
    /// <summary>Проверочное письмо из настроек почты.</summary>
    public const string Test = "test";
}

/// <summary>
/// Тексты проверочного письма.
/// </summary>
/// <remarks>
/// ⚠ Перенесены из эталона дословно, включая формулировку «ничего не значит для учётных
/// записей». Она там не для вежливости: получатель проверочного письма мог не знать, что его
/// адрес вообще есть в системе, и без этой строки письмо читается как начало чего-то.
/// </remarks>
internal sealed class MailOwnTemplates : IMailTemplateProvider
{
    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(
            MailKinds.Test,
            MailLocales.Ru,
            "Email SSL Sender: проверка почты",
            "Если вы это читаете, отправка писем настроена верно.\n\n"
            + "Письмо отправлено вручную из настроек почты в админке и ничего не значит для\n"
            + "учётных записей."),
        new(
            MailKinds.Test,
            MailLocales.Uk,
            "Email SSL Sender: перевірка пошти",
            "Якщо ви це читаєте, надсилання листів налаштовано правильно.\n\n"
            + "Лист надіслано вручну з налаштувань пошти в адмінці й нічого не означає для\n"
            + "облікових записів."),
        new(
            MailKinds.Test,
            MailLocales.En,
            "Email SSL Sender: mail check",
            "If you are reading this, sending mail is configured correctly.\n\n"
            + "This letter was sent by hand from the mail settings in the admin area and means\n"
            + "nothing for any account."),
    ];
}
