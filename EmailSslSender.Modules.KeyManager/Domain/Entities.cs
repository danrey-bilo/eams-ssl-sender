namespace EmailSslSender.Modules.KeyManager.Domain;

/// <summary>Состояние связи с Key Manager Plus: одна строка на установку.</summary>
/// <remarks>Ключ API запечатан здесь: имена <c>system_secrets</c> закрыты внутри модуля почты.</remarks>
internal sealed class KeyManagerState
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;

    /// <summary>Ключ API (<c>AUTHTOKEN</c>), запечатанный; <c>null</c> — не задан.</summary>
    public byte[]? ApiKey { get; set; }

    /// <summary>Ключ данных, обёрнутый мастер-ключом.</summary>
    public byte[]? DataKey { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Без внешнего ключа: <c>users</c> — чужая таблица.</summary>
    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? LastPollAt { get; set; }

    /// <summary><c>null</c> — последний опрос удался.</summary>
    public string? LastPollError { get; set; }

    public int? LastPollCount { get; set; }
}

/// <summary>Сертификат, замеченный опросом.</summary>
/// <remarks>Ключ — «Common Name + серийный»: старый и продлённый лежат в KMP рядом.</remarks>
internal sealed class KnownCertificate
{
    public required string CommonName { get; set; }

    public required string SerialNumber { get; set; }

    /// <summary><c>null</c> — KMP не прислал дату или она не разобралась.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Первое появление — момент, когда замечено продление.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>Подписка: какие сертификаты, кому, в каких файлах и когда слать.</summary>
internal sealed class CertificateSubscription
{
    public const int MaxCertificates = 50;

    public const int MaxDescription = 500;

    /// <summary>UUIDv7 из приложения.</summary>
    public required Guid Id { get; set; }

    /// <summary>Описание для администраторов.</summary>
    /// <remarks>⚠ Только в системе: в письмо не идёт, в журнал — лишь длина (текст может называть людей).</remarks>
    public string? Description { get; set; }

    /// <summary>Common Name в порядке, заданном администратором.</summary>
    public required string[] CommonNames { get; set; }

    public required string[] Recipients { get; set; }

    /// <summary>Типы файлов KMP (<c>fileType</c>).</summary>
    public required string[] Formats { get; set; }

    public bool IncludePassphrase { get; set; }

    /// <summary>Слать сертификат при новом серийном у его Common Name — только его.</summary>
    public bool OnChange { get; set; } = true;

    /// <summary>Слать все раз в столько дней; <c>null</c> — без расписания.</summary>
    public int? IntervalDays { get; set; }

    /// <summary>Период и время держат только опрос; ручная отправка шлёт всегда.</summary>
    public DateOnly? ActiveFrom { get; set; }

    public DateOnly? ActiveUntil { get; set; }

    /// <summary>«В это время», а не «не раньше»; <c>null</c> — сразу.</summary>
    public TimeOnly? SendTime { get; set; }

    /// <summary>Пояс IANA из браузера администратора.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Последнее разобранное время отправки.</summary>
    /// <remarks>⚠ Каждое время разбирается один раз, иначе опросы до полуночи слали бы повторно.</remarks>
    public DateTimeOffset? LastSlotAt { get; set; }

    /// <summary>Язык письма: <c>ru</c>, <c>uk</c> или <c>en</c>.</summary>
    public required string Locale { get; set; }

    /// <summary>Отключённую опрос не шлёт; вручную — можно.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    /// <summary>Последний отправленный серийный по Common Name подписки.</summary>
    public Dictionary<string, string> SentSerials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>NotBefore</c> последнего отправленного по Common Name.</summary>
    /// <remarks>⚠ Защита от отката: выпущенный раньше сам не уходит; срок для этого не годится.</remarks>
    public Dictionary<string, string> SentIssued { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>Последняя полная отправка — от неё «раз в N дней».</summary>
    public DateTimeOffset? LastFullSentAt { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary><c>null</c> — последняя попытка удалась.</summary>
    public string? LastError { get; set; }

    /// <summary>Письма последней отправки, судьба которых ещё не сверена с очередью.</summary>
    public Guid[]? PendingMailIds { get; set; }

    /// <summary>Серийные в письмах <see cref="PendingMailIds"/> — для автоповтора.</summary>
    public Dictionary<string, string> PendingSerials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool PendingFull { get; set; }

    /// <summary>Автоповторы подряд, когда не дошло ни одно письмо.</summary>
    public int AutoRetries { get; set; }
}
