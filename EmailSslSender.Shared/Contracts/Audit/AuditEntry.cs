namespace EmailSslSender.Shared.Contracts.Audit;

/// <summary>Кто совершил действие.</summary>
public enum AuditActorType
{
    /// <summary>Человек.</summary>
    User,

    /// <summary>Сама система: фоновые задачи, срок хранения, истечение сессий.</summary>
    System,
}

/// <summary>Чем кончилось.</summary>
public enum AuditResult
{
    /// <summary>Разрешено.</summary>
    Allow,

    /// <summary>Отказано.</summary>
    Deny,
}

/// <summary>
/// Событие для записи в журнал.
/// </summary>
/// <remarks>
/// <b>Здесь нет ни адреса, ни номера обращения — и это намеренно.</b> Они берутся из
/// контекста запроса самим журналом: ни одному бизнес-правилу они не нужны.
/// </remarks>
/// <param name="Action">Имя события.</param>
/// <param name="ActorId">Кто. <c>Guid</c>, а не сущность: журнал переживает удаление человека.</param>
/// <param name="ActorType">Человек или система.</param>
/// <param name="Result">Разрешено или отказано.</param>
/// <param name="ResourceType">Тип ресурса: <c>user</c>, <c>session</c>.</param>
/// <param name="ResourceId">Идентификатор ресурса.</param>
/// <param name="Reason">Почему отказано. <b>Наружу не уходит</b> — только в журнал.</param>
/// <param name="Changes">
/// Что изменилось. ⚠ <b>В лог не пишется</b> (<c>LogAuditWriter</c>): здесь бывают адреса
/// почты, а лог приложения общий. Поле оставлено затем, чтобы журнал с таблицей можно было
/// подключить, не переписывая вызывающих.
/// </param>
public sealed record AuditEntry(
    AuditAction Action,
    Guid? ActorId = null,
    AuditActorType? ActorType = null,
    AuditResult Result = AuditResult.Allow,
    string? ResourceType = null,
    Guid? ResourceId = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Changes = null);

/// <summary>
/// Запись события в журнал.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>В этой установке журнал — строка структурного лога</b> (<c>LogAuditWriter</c> у
/// хоста), а не таблица. Отдельного модуля журнала нет: установка держит учётные записи и
/// почту, и «кто, когда и что» отвечает лог — вход, отказы, смена паролей, пароль SMTP.
///
/// ⚠ <b>Строка пишется в момент вызова, а не после фиксации.</b> Откатившееся действие
/// оставит в логе свою строку и рядом — свою ошибку. Таблица в той же транзакции дала бы
/// «откатилось — нет строки»; если это понадобится, подключается своя реализация этого
/// интерфейса, вызывающие при этом не меняются.
/// </remarks>
public interface IAuditWriter
{
    /// <summary>Записать событие.</summary>
    void Write(AuditEntry entry);
}
