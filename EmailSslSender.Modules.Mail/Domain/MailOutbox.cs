namespace EmailSslSender.Modules.Mail.Domain;

/// <summary>
/// Письмо в очереди на отправку.
/// </summary>
/// <remarks>
/// ⚠ <b>Очередь в базе, а не в памяти, и это не выбор реализации.</b> Письмо — это код входа
/// или ссылка сброса; потерять его при перезапуске значит запереть человека снаружи. Очередь в
/// памяти теряет всё, что не успела отправить, и теряет молча.
///
/// ⚠ <b>Отправленное не удаляется сразу</b> — остаётся с проставленным <c>SentAt</c> до уборки
/// по сроку. Разбор жалобы «письмо не дошло» начинается с вопроса «а мы его отправляли», и
/// ответ на него обязан существовать дольше самой отправки.
/// </remarks>
internal sealed class MailOutbox
{
    /// <summary>UUIDv7 из приложения: возрастает во времени, порядок очереди совпадает с ключом.</summary>
    public required Guid Id { get; set; }

    /// <summary>Кому. Одно письмо — один адрес: рассылка это N строк, а не одна с N адресами.</summary>
    public required string ToAddress { get; set; }

    /// <summary>Тема. Открытая: секрета в ней нет, а при разборе пользы много.</summary>
    public required string Subject { get; set; }

    /// <summary>
    /// Тело письма.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Запечатано конвертом, а не лежит открытым</b> — расхождение с Python-версией,
    /// принятое решением 0029. В теле живут одноразовый код входа и ссылка сброса: предъявивший
    /// её задаёт чужой пароль. Дамп базы иначе отдаёт действующие ссылки всех писем, которые
    /// ещё не ушли.
    /// </remarks>
    public required byte[] SealedBody { get; set; }

    /// <summary>Ключ данных этого письма, обёрнутый мастер-ключом.</summary>
    public required byte[] DataKey { get; set; }

    /// <summary>Вид письма: приглашение, сброс, код входа. Из закрытого справочника.</summary>
    public required string Kind { get; set; }

    /// <summary>Когда поставлено в очередь.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Когда пробовать в следующий раз.
    /// </summary>
    /// <remarks>
    /// ⚠ Не «когда поставлено»: после неудачи насос отодвигает попытку, и очередь обязана
    /// отличать «ещё не пробовали» от «пробовали и ждём». Частичный индекс построен по этой
    /// колонке.
    /// </remarks>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Сколько раз пробовали. Дошло до потолка — письмо мёртвое.</summary>
    public int Attempts { get; set; }

    /// <summary>Когда ушло. <c>null</c> — ещё в очереди.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Чем кончилась последняя попытка. ⚠ Тип и текст без секретов (правило 14).</summary>
    public string? LastError { get; set; }
}
