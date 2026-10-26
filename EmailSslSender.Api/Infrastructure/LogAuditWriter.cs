using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Журнал событий — строкой структурного лога.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Вместо модуля журнала EAMS</b> (там — append-only таблица с цепочкой хешей). Здесь
/// учётные записи, почта и Key Manager Plus, и на вопрос «кто, когда и что» — вход, отказы, смена
/// паролей, второй фактор, пароль SMTP — отвечает лог: каждая строка несёт <c>audit_action</c>, кто,
/// над чем, исход и причину отказа, а номер запроса подставляет область логирования. Один класс
/// закрывает все четыре контракта журнала, которых модули ждут от хоста: событие действия
/// (<see cref="IAuditWriter"/>), событие безопасности (<see cref="ISecurityEventWriter"/>), решение
/// о праве (<see cref="IAuthorizationAuditor"/>) — и контекст запроса держит
/// <see cref="AuditContextAccessor"/>.
///
/// ⚠ ⚠ <b>Значения изменений пишутся только у событий из списка</b> (<see cref="СоЗначениями"/>):
/// настройки и Key Manager Plus. Без них строка «настройки изменены» не говорила бы, какая и с чего
/// на что, — а это главный вопрос к журналу после подмены адреса SMTP или Key Manager Plus. У
/// остальных — только имена полей: у модуля учётных записей в изменениях лежат адреса почты и имена,
/// а лог приложения общий и уезжает в тикеты целиком. Секреты в настройках не живут по устройству
/// (они в <c>system_secrets</c> и <c>keymanager_state</c>), подписки пишут поля и число адресов, а не
/// адреса.
///
/// ⚠ Строка пишется в момент вызова, а не после фиксации: откатившееся действие оставит в логе
/// свою строку и рядом — свою ошибку.
/// </remarks>
internal sealed class LogAuditWriter(
    ICurrentUser current,
    IAuditContextAccessor context,
    ILogger<LogAuditWriter> log) : IAuditWriter, ISecurityEventWriter, IAuthorizationAuditor
{
    /// <summary>
    /// Потолок строки изменений.
    /// </summary>
    /// <remarks>
    /// ⚠ Самое длинное значение настройки — адрес в два килобайта; строка длиннее — уже не
    /// разбор, а способ залить лог одной правкой.
    /// </remarks>
    internal const int ПотолокИзменений = 4096;

    /// <summary>События, изменения которых пишутся со значениями, — по приставке имени.</summary>
    internal static readonly IReadOnlyList<string> СоЗначениями = ["settings.", "keymanager."];

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

        Строка(
            entry.Result == AuditResult.Deny ? LogLevel.Warning : LogLevel.Information,
            entry.Action.Name,
            entry.Result == AuditResult.Deny ? "deny" : "allow",
            кто,
            вид,
            entry.ResourceType,
            entry.ResourceId,
            entry.Reason,
            Изменения(entry.Action, entry.Changes));
    }

    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        // ⚠ Событие безопасности переживает откат действия по контракту — у строки лога отката нет.
        Строка(
            LogLevel.Warning,
            securityEvent.Action.Name,
            "security",
            securityEvent.ActorId ?? current.Id,
            securityEvent.ActorId is null && current.Id is null ? AuditActorType.System : current.ActorType,
            null,
            null,
            securityEvent.Summary,
            Изменения(securityEvent.Action, securityEvent.Details));

        return Task.CompletedTask;
    }

    public Task RecordAsync(
        Guid? actorId,
        AuditDecision decision,
        string? resourceType,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // ⚠ Пишутся только отказы. Ролей в проекте нет — каждая учётная запись суперпользователь, и
        // разрешение с обходом стояло бы строкой на каждом открытии экрана: журнал утонул бы в «можно»,
        // а каждое изменяющее действие и так пишет своё событие.
        if (decision.Allow)
        {
            return Task.CompletedTask;
        }

        Строка(
            LogLevel.Warning,
            "authz.denied",
            "deny",
            actorId ?? current.Id,
            current.ActorType,
            resourceType,
            resourceId,
            $"{decision.Permission}: {decision.Reason}",
            null);

        return Task.CompletedTask;
    }

    private void Строка(
        LogLevel уровень,
        string действие,
        string исход,
        Guid? кто,
        AuditActorType вид,
        string? типРесурса,
        Guid? ресурс,
        string? причина,
        string? изменения)
    {
        var адрес = context.Current.ActorIp?.ToString();

        if (изменения is null)
        {
            log.Log(
                уровень,
                "audit {audit_action} {audit_result} actor={audit_actor} {audit_actor_type} "
                + "resource={audit_resource_type}:{audit_resource_id} reason={audit_reason} ip={audit_ip}",
                действие, исход, кто, вид, типРесурса, ресурс, причина, адрес);

            return;
        }

        log.Log(
            уровень,
            "audit {audit_action} {audit_result} actor={audit_actor} {audit_actor_type} "
            + "resource={audit_resource_type}:{audit_resource_id} reason={audit_reason} ip={audit_ip} "
            + "changes={audit_changes}",
            действие, исход, кто, вид, типРесурса, ресурс, причина, адрес, изменения);
    }

    /// <summary>
    /// Изменения строкой JSON или <c>null</c>, если писать нечего: со значениями — у событий из
    /// <see cref="СоЗначениями"/>, у прочих — только имена полей.
    /// </summary>
    internal static string? Изменения(AuditAction action, IReadOnlyDictionary<string, object?>? changes)
    {
        if (changes is not { Count: > 0 })
        {
            return null;
        }

        var значения = СоЗначениями.Any(приставка => action.Name.StartsWith(приставка, StringComparison.Ordinal));

        var текст = значения
            ? JsonSerializer.Serialize(changes, Формат)
            : JsonSerializer.Serialize(changes.Keys.Order(StringComparer.Ordinal), Формат);

        return текст.Length <= ПотолокИзменений ? текст : string.Concat(текст.AsSpan(0, ПотолокИзменений), "…");
    }
}

/// <summary>Контекст журнала на запрос: номер, адрес, агент, личность.</summary>
/// <remarks>
/// Дополняет, а не затирает: привязка личности после входа не должна стирать адрес и агента,
/// известные с первой строки конвейера.
/// </remarks>
internal sealed class AuditContextAccessor : IAuditContextAccessor
{
    public AuditContext Current { get; private set; } = new();

    public void Bind(AuditContext patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        Current = Current with
        {
            RequestId = patch.RequestId ?? Current.RequestId,
            ActorIp = patch.ActorIp ?? Current.ActorIp,
            UserAgent = patch.UserAgent ?? Current.UserAgent,
            ActorId = patch.ActorId ?? Current.ActorId,
            SessionId = patch.SessionId ?? Current.SessionId,
            ActorType = patch.ActorType ?? Current.ActorType,
        };
    }
}
