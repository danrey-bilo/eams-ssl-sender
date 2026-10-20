using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Журнал событий — строкой структурного лога.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Вместо модуля журнала EAMS</b> (там — append-only таблица с цепочкой хешей). Здесь
/// учётные записи и почта, и на вопрос «кто, когда и что» — вход, отказы, смена паролей,
/// второй фактор, пароль SMTP — отвечает лог: каждая строка несёт <c>audit_action</c>, кто,
/// над чем, исход и причину отказа, а номер запроса подставляет область логирования.
///
/// ⚠ <b><c>Changes</c> в лог НЕ пишется.</b> Там бывают адреса почты и имена, а лог приложения
/// общий и уезжает в тикеты целиком. Идентификаторы и имена событий — пишутся.
///
/// ⚠ Строка пишется в момент вызова, а не после фиксации: откатившееся действие оставит в логе
/// свою строку и рядом — свою ошибку. Нужен журнал «откатилось — нет строки» — подключите
/// реализацию <see cref="IAuditWriter"/>, пишущую таблицу в той же транзакции.
/// </remarks>
internal sealed class LogAuditWriter(
    ICurrentUser current,
    IHttpContextAccessor http,
    ILogger<LogAuditWriter> log) : IAuditWriter
{
    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Актор по умолчанию — тот, кто пришёл с запросом: службы модулей не обязаны его
        // передавать, и строка без него отвечала бы «кто-то» на главный вопрос журнала.
        var кто = entry.ActorId ?? current.Id;
        var вид = entry.ActorType ?? (кто is null ? AuditActorType.System : current.ActorType);
        var адрес = http.HttpContext?.Connection.RemoteIpAddress?.ToString();

        var уровень = entry.Result == AuditResult.Deny ? LogLevel.Warning : LogLevel.Information;

        log.Log(
            уровень,
            "audit {audit_action} {audit_result} actor={audit_actor} {audit_actor_type} "
            + "resource={audit_resource_type}:{audit_resource_id} reason={audit_reason} ip={audit_ip}",
            entry.Action.Name,
            entry.Result == AuditResult.Deny ? "deny" : "allow",
            кто,
            вид,
            entry.ResourceType,
            entry.ResourceId,
            entry.Reason,
            адрес);
    }
}
