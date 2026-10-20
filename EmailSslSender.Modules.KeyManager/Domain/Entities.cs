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
/// Подписка: какой сертификат, кому, в каких файлах и когда слать.
/// </summary>
internal sealed class CertificateSubscription
{
    /// <summary>UUIDv7 из приложения.</summary>
    public required Guid Id { get; set; }

    /// <summary>Common Name сертификата в Key Manager Plus, например <c>*.example.com</c>.</summary>
    public required string CommonName { get; set; }

    /// <summary>Адреса получателей: по письму на каждый.</summary>
    public required string[] Recipients { get; set; }

    /// <summary>Типы файлов Key Manager Plus (<c>fileType</c>): <c>PEM</c>, <c>KEY</c>, <c>PKCS</c>…</summary>
    public required string[] Formats { get; set; }

    /// <summary>Писать ли в письмо пароль закрытого ключа из Key Manager Plus.</summary>
    public bool IncludePassphrase { get; set; }

    /// <summary>Слать ли, когда у Common Name появился новый серийный номер.</summary>
    public bool OnChange { get; set; } = true;

    /// <summary>Слать ли повторно раз в столько дней. <c>null</c> — без расписания.</summary>
    public int? IntervalDays { get; set; }

    /// <summary>Язык письма: <c>ru</c>, <c>uk</c> или <c>en</c>.</summary>
    public required string Locale { get; set; }

    /// <summary>Отключённая подписка опросом не отправляется; вручную — можно.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    /// <summary>Серийный номер последнего отправленного сертификата.</summary>
    public string? LastSentSerial { get; set; }

    /// <summary>Когда письма последней отправки легли в очередь.</summary>
    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>Когда пробовали в последний раз — удачно или нет.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Почему не удалась последняя попытка. <c>null</c> — удалась.</summary>
    public string? LastError { get; set; }
}
