namespace EmailSslSender.Modules.KeyManager.Domain;

/// <summary>
/// Состояние связи с Key Manager Plus: одна строка на установку.
/// </summary>
/// <remarks>
/// ⚠ <b>Свой запечатанный ключ API, а не строка в <c>system_secrets</c>.</b> Набор имён там закрыт
/// внутри модуля почты, и класть туда чужой секрет значило бы лезть в соседа мимо контракта.
/// </remarks>
internal sealed class KeyManagerState
{
    /// <summary>Единственная строка всегда с этим ключом.</summary>
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;

    /// <summary>Ключ API (<c>AUTHTOKEN</c>), запечатанный. <c>null</c> — не задан.</summary>
    public byte[]? ApiKey { get; set; }

    /// <summary>Ключ данных ключа API, обёрнутый мастер-ключом.</summary>
    public byte[]? DataKey { get; set; }

    /// <summary>Когда ключ API задали или сняли.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Кто. Без внешнего ключа: <c>users</c> — чужая таблица (правило 1).</summary>
    public Guid? UpdatedBy { get; set; }

    /// <summary>Когда закончился последний опрос — удачный или нет.</summary>
    public DateTimeOffset? LastPollAt { get; set; }

    /// <summary>Почему не удался последний опрос. <c>null</c> — удался.</summary>
    public string? LastPollError { get; set; }

    /// <summary>Сколько сертификатов вернул последний удачный опрос.</summary>
    public int? LastPollCount { get; set; }
}

/// <summary>
/// Сертификат, который Key Manager Plus показывал при опросе.
/// </summary>
/// <remarks>
/// ⚠ <b>Ключ — пара «Common Name + серийный номер».</b> Продлённый сертификат — это тот же
/// Common Name с новым серийным номером; старый может ещё лежать в Key Manager Plus рядом с
/// новым, поэтому один Common Name — несколько строк.
/// </remarks>
internal sealed class KnownCertificate
{
    public required string CommonName { get; set; }

    public required string SerialNumber { get; set; }

    /// <summary>Действует до. <c>null</c> — Key Manager Plus дату не прислал или прислал неразборчиво.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Когда опрос увидел его впервые — то есть когда замечено продление.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Когда опрос видел его в последний раз.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// Подписка: какие сертификаты, кому, в каких файлах и когда слать.
/// </summary>
/// <remarks>
/// ⚠ <b>Сертификатов в подписке от одного до <see cref="MaxCertificates"/></b> (решение владельца
/// 2026-10-27): письмо несёт их таблицей и одним архивом. При обновлении уходят только обновлённые, по
/// расписанию и вручную — все.
/// </remarks>
internal sealed class CertificateSubscription
{
    /// <summary>Сколько сертификатов в одной подписке.</summary>
    public const int MaxCertificates = 50;

    /// <summary>Самое длинное описание подписки, в знаках.</summary>
    public const int MaxDescription = 500;

    /// <summary>UUIDv7 из приложения.</summary>
    public required Guid Id { get; set; }

    /// <summary>
    /// Описание для администраторов: что это за рассылка и для кого. <c>null</c> — без описания.
    /// </summary>
    /// <remarks>
    /// ⚠ Только в системе (решение владельца 2026-10-28): в письма и в журнал не уходит — в журнал
    /// пишется лишь его длина, текст может называть людей.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>Common Name сертификатов в Key Manager Plus, например <c>*.example.com</c>; в порядке, заданном администратором.</summary>
    public required string[] CommonNames { get; set; }

    /// <summary>Адреса получателей: по письму на каждый.</summary>
    public required string[] Recipients { get; set; }

    /// <summary>Типы файлов Key Manager Plus (<c>fileType</c>): <c>PEM</c>, <c>KEY</c>, <c>PKCS</c>…</summary>
    public required string[] Formats { get; set; }

    /// <summary>Писать ли в письмо пароль закрытого ключа из Key Manager Plus.</summary>
    public bool IncludePassphrase { get; set; }

    /// <summary>Слать ли сертификат, когда у его Common Name появился новый серийный номер, — только его.</summary>
    public bool OnChange { get; set; } = true;

    /// <summary>Слать ли повторно все сертификаты раз в столько дней. <c>null</c> — без расписания.</summary>
    public int? IntervalDays { get; set; }

    /// <summary>С какого дня (включительно) опрос шлёт подписку сам. <c>null</c> — без начала.</summary>
    /// <remarks>
    /// ⚠ ⚠ Период и время отправки (решение владельца 2026-10-28) держат только опрос: ручная отправка —
    /// решение администратора и шлёт всегда. День и время — по <see cref="TimeZone"/>. Разбор — у
    /// <c>SendSchedule</c>.
    /// </remarks>
    public DateOnly? ActiveFrom { get; set; }

    /// <summary>По какой день (включительно) опрос шлёт подписку сам. <c>null</c> — без окончания.</summary>
    public DateOnly? ActiveUntil { get; set; }

    /// <summary>
    /// В какое время суток опрос шлёт подписку. <c>null</c> — сразу, как опрос заметил, что пора.
    /// </summary>
    /// <remarks>
    /// ⚠ «В это время», а не «не раньше»: обновление, замеченное после него, ждёт следующего дня.
    /// </remarks>
    public TimeOnly? SendTime { get; set; }

    /// <summary>
    /// Часовой пояс IANA (<c>Europe/Kyiv</c>) дат и времени отправки — из браузера администратора.
    /// <c>null</c>, если ни дат, ни времени нет.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Последнее время отправки, которое опрос уже разобрал: отправил или решил, что слать нечего.
    /// </summary>
    /// <remarks>
    /// ⚠ Одно время отправки разбирается один раз — иначе каждый опрос до полуночи слал бы замеченное
    /// после него. Новое расписание и включение подписки ставят сюда «сейчас»: прошедшее сегодня не
    /// догоняется, первое письмо — в ближайшее время отправки.
    /// </remarks>
    public DateTimeOffset? LastSlotAt { get; set; }

    /// <summary>Язык письма: <c>ru</c>, <c>uk</c> или <c>en</c>.</summary>
    public required string Locale { get; set; }

    /// <summary>Отключённая подписка опросом не отправляется; вручную — можно.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    /// <summary>
    /// Серийный номер, ушедший последним, по каждому Common Name подписки (ключ — как записан в подписке).
    /// </summary>
    /// <remarks>По нему «при обновлении» отличает обновлённый сертификат от уже отправленного.</remarks>
    public Dictionary<string, string> SentSerials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Когда выпущен (<c>NotBefore</c>, ISO 8601) сертификат, ушедший последним, — по Common Name подписки.
    /// </summary>
    /// <remarks>
    /// ⚠ Защита от отката: Key Manager Plus может снова внести старый сертификат (сканирование нашло его
    /// на ещё не обновлённом сервере), и «замеченный позже всех» стал бы им. Сертификат, выпущенный раньше
    /// отправленного, сам не уходит (разбор у <c>CertificateDelivery</c>). Срок для этого не годится:
    /// продлённый досрочно под новые предельные сроки истекает раньше прежнего.
    /// </remarks>
    public Dictionary<string, string> SentIssued { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Когда письма последней отправки легли в очередь — любой, и частичной тоже.</summary>
    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>Когда последний раз уходили все сертификаты подписки: от неё считается «раз в N дней».</summary>
    public DateTimeOffset? LastFullSentAt { get; set; }

    /// <summary>Когда пробовали в последний раз — удачно или нет.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Почему не удалась последняя попытка. <c>null</c> — удалась.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Номера писем последней отправки, судьба которых ещё не сверена. <c>null</c> — сверять нечего.
    /// </summary>
    /// <remarks>
    /// ⚠ Отправка отмечается, когда письма легли в очередь, — а дойдут ли они, решится позже. Опрос
    /// сверяет эти номера с очередью: умершее письмо становится <see cref="LastError"/>, а не
    /// молчаливым «отправлено».
    /// </remarks>
    public Guid[]? PendingMailIds { get; set; }

    /// <summary>
    /// Какие серийные номера несут письма <see cref="PendingMailIds"/> — по Common Name.
    /// </summary>
    /// <remarks>
    /// По ним опрос снимает отметку «отправлено», если не дошло ни одно письмо отправки (автоповтор).
    /// </remarks>
    public Dictionary<string, string> PendingSerials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Была ли отправка <see cref="PendingMailIds"/> полной — сдвинула ли она «раз в N дней».</summary>
    public bool PendingFull { get; set; }

    /// <summary>Сколько раз подряд отправку повторили сами, потому что не дошло ни одно письмо.</summary>
    public int AutoRetries { get; set; }
}
