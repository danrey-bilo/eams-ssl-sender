using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Naming;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Retention;

/// <summary>Сколько чего убрано за один проход.</summary>
/// <param name="Links">Погашенных и просроченных одноразовых ссылок и кодов.</param>
internal readonly record struct CleanupResult(int Links)
{
    /// <summary>Было ли что убирать. Пустой проход в журнал не пишется.</summary>
    public bool Any => Links > 0;
}

/// <summary>
/// Уборка одноразовых ссылок и кодов.
/// </summary>
/// <remarks>
/// ⚠ <b>Сессии убирает модуль токенов</b> — вместе со своими таблицами. Один уборщик на две
/// чужие друг другу схемы означал бы, что модуль личностей ходит в таблицы соседа.
///
/// <b>Что здесь НЕ удаляется.</b> Живые сессии, действующие токены и непогашенные ссылки со
/// сроком в будущем — что бы ни было написано в настройке. Уборщик, способный удалить живое,
/// выкидывает людей из системы молча, и обнаруживается это не логом, а звонками.
///
/// ⚠ <b>Всё одной транзакцией.</b> Удаление идёт оператором и состоится немедленно, а строка
/// журнала уезжает на сохранении: без общей транзакции обрыв между ними означал бы удаление
/// без следа.
/// </remarks>
internal sealed class LinkCleanup(
    SenderDbContext db,
    ISettingsReader settings,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<CleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var days = Math.Max(1, await settings.GetAsync(IdentitySettings.LinkRetentionDays, cancellationToken));
        var cutoff = clock.GetUtcNow().AddDays(-days);

        return await db.InTransactionAsync(
            async (token) =>
            {
                var links = await db.Set<IdentityToken>()
                    .Where(row => row.ExpiresAt < cutoff)
                    .ExecuteDeleteAsync(token);


                var result = new CleanupResult(links);

                if (!result.Any)
                {
                    // Пустой проход в журнал не пишется: ежедневная строка «убрано ноль» — это
                    // шум, а журнал безопасности от шума уже один раз чистили.
                    return result;
                }

                // Актора нет: убирает система, а не человек. Строка нужна, чтобы «куда делись
                // старые приглашения» имело ответ, и чтобы неверно выставленный срок было видно по
                // счётчикам.
                audit.Write(new AuditEntry(
                    IdentityActions.LinksCleaned,
                    ActorType: AuditActorType.System,
                    ResourceType: IdentityResources.User,
                    Changes: new Dictionary<string, object?>
                    {
                        ["links"] = links,
                        ["older_than_days"] = days,
                    }));

                await db.SaveChangesAsync(token);

                return result;
            },
            cancellationToken);
    }
}
