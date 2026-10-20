using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
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
/// ⚠ ⚠ <b><c>Changes</c> пишется ТОЛЬКО под поручительство вызывающего</b>
/// (<see cref="AuditEntry.LogChanges"/>). Без него строка «настройки изменены» не говорила бы,
/// какая и с чего на что, — а это главный вопрос к журналу после подмены адреса SMTP или Key
/// Manager Plus. Но у модуля учётных записей в <c>Changes</c> лежат адреса почты и имена, а лог
/// приложения общий и уезжает в тикеты целиком: о чужих <c>Changes</c> журнал не знает ничего,
/// поэтому умолчание закрытое. Настройки секретов не держат по устройству (они в
/// <c>system_secrets</c> и <c>keymanager_state</c>), подписки пишут поля и число адресов, а не
/// адреса.
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
    /// <summary>
    /// Потолок строки изменений.
    /// </summary>
    /// <remarks>
    /// ⚠ Самое длинное значение настройки — адрес в два килобайта; строка длиннее — уже не
    /// разбор, а способ залить лог одной правкой.
    /// </remarks>
    internal const int ПотолокИзменений = 4096;

    /// <summary>
    /// JSON с кириллицей как есть: строку журнала читает человек.
    /// </summary>
    /// <remarks>
    /// ⚠ Управляющие знаки и кавычки по-прежнему экранируются — перевод строки в значении не
    /// разорвёт строку лога и не подделает соседнюю.
    /// </remarks>
    private static readonly JsonSerializerOptions Формат = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Актор по умолчанию — тот, кто пришёл с запросом: службы модулей не обязаны его
        // передавать, и строка без него отвечала бы «кто-то» на главный вопрос журнала.
        var кто = entry.ActorId ?? current.Id;
        var вид = entry.ActorType ?? (кто is null ? AuditActorType.System : current.ActorType);
        var адрес = http.HttpContext?.Connection.RemoteIpAddress?.ToString();

        var уровень = entry.Result == AuditResult.Deny ? LogLevel.Warning : LogLevel.Information;
        var изменения = Изменения(entry);

        if (изменения is null)
        {
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

            return;
        }

        log.Log(
            уровень,
            "audit {audit_action} {audit_result} actor={audit_actor} {audit_actor_type} "
            + "resource={audit_resource_type}:{audit_resource_id} reason={audit_reason} ip={audit_ip} "
            + "changes={audit_changes}",
            entry.Action.Name,
            entry.Result == AuditResult.Deny ? "deny" : "allow",
            кто,
            вид,
            entry.ResourceType,
            entry.ResourceId,
            entry.Reason,
            адрес,
            изменения);
    }

    /// <summary>
    /// Изменения строкой JSON или <c>null</c>, если писать нечего или нельзя.
    /// </summary>
    internal static string? Изменения(AuditEntry entry)
    {
        if (!entry.LogChanges || entry.Changes is not { Count: > 0 } изменения)
        {
            return null;
        }

        var текст = JsonSerializer.Serialize(изменения, Формат);

        return текст.Length <= ПотолокИзменений ? текст : string.Concat(текст.AsSpan(0, ПотолокИзменений), "…");
    }
}
