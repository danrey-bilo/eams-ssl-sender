using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Каждое событие журнала — ещё и строкой «audit …» в лог.</summary>
/// <remarks>⚠ Значения изменений — только у <see cref="СоЗначениями"/>, у прочих лишь имена полей: лог уезжает в тикеты.</remarks>
internal sealed class LogAuditWriter(
    ICurrentUser current,
    IAuditContextAccessor context,
    ILogger<LogAuditWriter> log) : IAuditWriter, ISecurityEventWriter, IAuthorizationAuditor
{
    /// <summary>Потолок строки изменений — чтобы одной правкой не залить лог.</summary>
    internal const int ПотолокИзменений = 4096;

    /// <summary>События, изменения которых пишутся со значениями, — по приставке имени.</summary>
    internal static readonly IReadOnlyList<string> СоЗначениями = ["settings.", "keymanager."];

    // Кириллица как есть; управляющие знаки и кавычки экранируются — строку лога не подделать.
    private static readonly JsonSerializerOptions Формат = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Службы модулей не обязаны передавать актора — по умолчанию тот, кто пришёл с запросом.
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

        // Только отказы: разрешения стояли бы строкой на каждом открытии экрана.
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

    /// <summary>Изменения строкой JSON или <c>null</c>, если писать нечего.</summary>
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
