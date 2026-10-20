using EmailSslSender.Shared.Contracts.Mail;

namespace EmailSslSender.Modules.Identity.Mail;

/// <summary>
/// Виды писем, которые шлёт модуль личностей.
/// </summary>
/// <remarks>
/// ⚠ <b>Имена совпадают с эталонными дословно.</b> Вид ложится в <c>mail_outbox.kind</c> и в
/// ключ предела частоты; переименование сделало бы прежние строки очереди неотправляемыми и
/// обнулило бы счётчик частоты на живой установке.
/// </remarks>
internal static class IdentityMailKinds
{
    /// <summary>Приглашение: ссылка, по которой человек задаёт себе пароль.</summary>
    public const string Invitation = "invitation";

    /// <summary>Восстановление пароля.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>Доступ приостановлен.</summary>
    public const string AccountSuspended = "account_suspended";

    /// <summary>Одноразовый код входа.</summary>
    public const string LoginCode = "login_code";
}

/// <summary>
/// Тексты писем модуля личностей: четыре вида на трёх языках.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Тексты объявляет тот, кто шлёт, а не почта.</b> Иначе добавление письма о блокировке
/// требовало бы правки модуля почты — ровно того, что запрещает правило 1.
///
/// ⚠ <b>Перенесены из эталона дословно, и каждый абзац там не случаен.</b> «Администратор
/// вашего пароля не знает и знать не будет» — это ответ на вопрос, который человек задаёт в
/// первый день. «Если такие письма приходят повторно, сообщите администратору» — это
/// превращение назойливости в сигнал безопасности. «Форма входа причину не называет» —
/// объяснение, почему о блокировке сообщают письмом, а не на экране.
/// </remarks>
internal sealed class IdentityMailTemplates : IMailTemplateProvider
{
    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        // --- приглашение --------------------------------------------------------------------
        new(
            IdentityMailKinds.Invitation,
            MailLocales.Ru,
            "Email SSL Sender: приглашение",
            "Для вас создана учётная запись в Email SSL Sender.\n\n"
            + "Пароль задаёте вы сами, по ссылке. Она действует {minutes} минут и работает\n"
            + "один раз:\n{link}\n\n"
            + "Администратор вашего пароля не знает и знать не будет.\n\n"
            + "Ссылка истекла — попросите администратора прислать новую."),
        new(
            IdentityMailKinds.Invitation,
            MailLocales.Uk,
            "Email SSL Sender: запрошення",
            "Для вас створено обліковий запис в Email SSL Sender.\n\n"
            + "Пароль задаєте ви самі, за посиланням. Воно діє {minutes} хвилин і працює\n"
            + "один раз:\n{link}\n\n"
            + "Адміністратор вашого пароля не знає і знати не буде.\n\n"
            + "Посилання застаріло — попросіть адміністратора надіслати нове."),
        new(
            IdentityMailKinds.Invitation,
            MailLocales.En,
            "Email SSL Sender: invitation",
            "An account has been created for you in Email SSL Sender.\n\n"
            + "You set the password yourself, through this link. It works once and expires\n"
            + "in {minutes} minutes:\n{link}\n\n"
            + "Your administrator does not know your password and never will.\n\n"
            + "If the link has expired, ask your administrator for a new one."),

        // --- восстановление пароля ----------------------------------------------------------
        new(
            IdentityMailKinds.PasswordReset,
            MailLocales.Ru,
            "Email SSL Sender: восстановление пароля",
            "Кто-то запросил смену пароля для этого адреса.\n\n"
            + "Ссылка действует {minutes} минут и срабатывает один раз:\n{link}\n\n"
            + "После смены пароля все ваши сессии будут закрыты — войти придётся заново на\n"
            + "всех устройствах.\n\n"
            + "Если запрос не ваш, ничего делать не нужно: без перехода по ссылке пароль\n"
            + "не меняется. Но если такие письма приходят повторно, сообщите администратору —\n"
            + "значит, ваш адрес кому-то известен и его пробуют."),
        new(
            IdentityMailKinds.PasswordReset,
            MailLocales.Uk,
            "Email SSL Sender: відновлення пароля",
            "Хтось запросив зміну пароля для цієї адреси.\n\n"
            + "Посилання діє {minutes} хвилин і працює один раз:\n{link}\n\n"
            + "Після зміни пароля всі ваші сесії буде закрито — увійти доведеться заново на\n"
            + "всіх пристроях.\n\n"
            + "Якщо запит не ваш, робити нічого не потрібно: без переходу за посиланням пароль\n"
            + "не змінюється. Але якщо такі листи надходять повторно, повідомте адміністратора —\n"
            + "отже, вашу адресу комусь відомо і її пробують."),
        new(
            IdentityMailKinds.PasswordReset,
            MailLocales.En,
            "Email SSL Sender: password reset",
            "Someone asked to change the password for this address.\n\n"
            + "The link works once and expires in {minutes} minutes:\n{link}\n\n"
            + "Changing the password closes all your sessions — you will sign in again on\n"
            + "every device.\n\n"
            + "If this was not you, do nothing: the password does not change until the link is\n"
            + "opened. But if these letters keep arriving, tell your administrator — it means\n"
            + "someone knows your address and is trying it."),

        // --- доступ приостановлен -----------------------------------------------------------
        new(
            IdentityMailKinds.AccountSuspended,
            MailLocales.Ru,
            "Email SSL Sender: доступ приостановлен",
            "Доступ к Email SSL Sender для этого адреса приостановлен.\n\n"
            + "Форма входа причину не называет — она отвечает одинаково на все случаи, чтобы\n"
            + "не подсказывать посторонним, какие адреса существуют. Поэтому мы пишем письмом.\n\n"
            + "Восстановить доступ может администратор."),
        new(
            IdentityMailKinds.AccountSuspended,
            MailLocales.Uk,
            "Email SSL Sender: доступ припинено",
            "Доступ до Email SSL Sender для цієї адреси припинено.\n\n"
            + "Форма входу причину не називає — вона відповідає однаково на всі випадки, щоб\n"
            + "не підказувати стороннім, які адреси існують. Тому ми пишемо листом.\n\n"
            + "Відновити доступ може адміністратор."),
        new(
            IdentityMailKinds.AccountSuspended,
            MailLocales.En,
            "Email SSL Sender: access suspended",
            "Access to Email SSL Sender for this address has been suspended.\n\n"
            + "The sign-in form does not say why — it answers the same way in every case, so it\n"
            + "cannot tell strangers which addresses exist. That is why we write instead.\n\n"
            + "Your administrator can restore access."),

        // --- одноразовый код входа ----------------------------------------------------------
        //
        // ⚠ ⚠ Про «не вы» сказано ПЕРВЫМ делом, а не в конце: код, пришедший тому, кто не
        // входил, означает, что пароль знает кто-то ещё, — и это единственный момент, когда
        // человек может об этом узнать.
        new(
            IdentityMailKinds.LoginCode,
            MailLocales.Ru,
            "Email SSL Sender: код для входа",
            "Код для входа: {code}\n\n"
            + "Он действует {minutes} минут и работает один раз.\n\n"
            + "Если вы не входили — кто-то знает ваш пароль. Смените его и сообщите\n"
            + "администратору."),
        new(
            IdentityMailKinds.LoginCode,
            MailLocales.Uk,
            "Email SSL Sender: код для входу",
            "Код для входу: {code}\n\n"
            + "Він діє {minutes} хвилин і працює один раз.\n\n"
            + "Якщо ви не входили — хтось знає ваш пароль. Змініть його та повідомте\n"
            + "адміністратора."),
        new(
            IdentityMailKinds.LoginCode,
            MailLocales.En,
            "Email SSL Sender: sign-in code",
            "Sign-in code: {code}\n\n"
            + "It works once and expires in {minutes} minutes.\n\n"
            + "If this was not you, someone knows your password. Change it and tell your\n"
            + "administrator."),
    ];
}
