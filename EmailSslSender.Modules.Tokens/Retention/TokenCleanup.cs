using EmailSslSender.Modules.Tokens.Contracts;
using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Modules.Tokens.Naming;
using EmailSslSender.Modules.Tokens.Settings;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Tokens.Retention;

/// <summary>Сколько чего убрано за один проход.</summary>
/// <param name="Tokens">Закрытых и просроченных строк выдачи.</param>
/// <param name="Secrets">Мёртвых секретов доступа и обновления.</param>
internal readonly record struct CleanupResult(int Tokens, int Secrets)
{
    /// <summary>Было ли что убирать. Пустой проход в журнал не пишется.</summary>
    public bool Any => Tokens + Secrets > 0;
}

/// <summary>
/// Уборка мёртвых токенов и их секретов.
/// </summary>
/// <remarks>
/// <b>Приехало из <c>Identity/Retention/SessionCleanup.cs</c> (этап 10.5.6)</b> — та его
/// половина, что про токены. Одноразовые ссылки и просроченные выданные доступы остались у
/// соседа: один уборщик на две чужие друг другу схемы означал бы, что модуль ходит в чужие
/// таблицы.
///
/// ⚠ <b>До 2026-08-22 не убирал никто.</b> При сроке токена доступа в пятнадцать минут один
/// работающий человек даёт около тридцати строк секретов за день и порядка семи тысяч за
/// год. Сто человек — семьсот тысяч строк, из которых живых десятки. Комментарий «отзыв и
/// уборка ходят по строке» стоял в схеме с самого начала; уборки не было.
///
/// <b>Что здесь НЕ удаляется.</b> Живые строки, действующие секреты — что бы ни было
/// написано в настройке. Уборщик, способный удалить живое, выкидывает людей из системы
/// молча, и обнаруживается это не логом, а звонками.
///
/// <b>История остаётся в журнале.</b> Строка после её конца ничего не рассказывает — кто,
/// когда и откуда входил, записано в append-only журнале, и срок хранения у него свой.
///
/// ⚠ <b>Всё одной транзакцией.</b> Удаление идёт оператором и состоится немедленно, а строка
/// журнала уезжает на сохранении: без общей транзакции обрыв между ними означал бы удаление
/// без следа (правило 15).
/// </remarks>
internal sealed class TokenCleanup(
    SenderDbContext db,
    ISettingsReader settings,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<CleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var days = Math.Max(1, await settings.GetAsync(TokenSettings.RetentionDays, cancellationToken));
        var cutoff = clock.GetUtcNow().AddDays(-days);

        return await db.InTransactionAsync(
            async (token) =>
            {
                // Порядок значим: сначала секреты живых строк (сами строки остаются), потом
                // сами строки — вместе со своими секретами по каскаду.
                var secrets = await db.Set<AuthTokenSecret>()
                    .Where(row => row.ExpiresAt < cutoff)
                    .ExecuteDeleteAsync(token);

                var tokens = await db.Set<AuthToken>()
                    .Where(row => row.ExpiresAt < cutoff || (row.RevokedAt != null && row.RevokedAt < cutoff))
                    .ExecuteDeleteAsync(token);

                var result = new CleanupResult(tokens, secrets);

                if (!result.Any)
                {
                    // Пустой проход в журнал не пишется: ежедневная строка «убрано ноль» — это
                    // шум, а журнал безопасности от шума уже один раз чистили.
                    return result;
                }

                // Актора нет: убирает система, а не человек. Строка нужна, чтобы «куда делись
                // старые сессии» имело ответ, и чтобы неверно выставленный срок было видно по
                // счётчикам.
                audit.Write(new AuditEntry(
                    TokenActions.Cleaned,
                    ActorType: AuditActorType.System,
                    ResourceType: TokenResources.Session,
                    Changes: new Dictionary<string, object?>
                    {
                        ["sessions"] = tokens,
                        ["tokens"] = secrets,
                        ["older_than_days"] = days,
                    }));

                await db.SaveChangesAsync(token);

                return result;
            },
            cancellationToken);
    }
}
