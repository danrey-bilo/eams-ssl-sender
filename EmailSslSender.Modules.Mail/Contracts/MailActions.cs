using EmailSslSender.Shared.Contracts.Audit;

namespace EmailSslSender.Modules.Mail.Contracts;

/// <summary>
/// События журнала, которые пишет модуль почты.
/// </summary>
/// <remarks>
/// Публичный тип — на эти имена ссылаются тесты. Имя события не меняется никогда: оно лежит в
/// строках журнала, а журнал append-only.
///
/// ⚠ <b>Сама отправка письма событием НЕ является.</b> Записывается то, что делает человек:
/// задал пароль SMTP, послал проверочное письмо. Строка на каждое ушедшее письмо — это строка
/// на каждый вход в систему (код второго фактора), то есть повторение ошибки
/// <c>permission_check</c>, залившего журнал стенда на три четверти.
/// </remarks>
public static class MailActions
{
    /// <summary>Задан или убран пароль SMTP.</summary>
    /// <remarks>
    /// ⚠ Значение в `changes` НЕ идёт — ни старое, ни новое. Это единственное событие модуля,
    /// где соблазн записать «было/стало» стоил бы пароля в append-only журнале: строку оттуда
    /// нельзя ни изменить, ни удалить.
    /// </remarks>
    public static readonly AuditAction PasswordSet = new("mail.password_set", "Задан пароль SMTP");

    /// <summary>Послано проверочное письмо.</summary>
    public static readonly AuditAction TestSent = new("mail.test_sent", "Послано проверочное письмо");
}
