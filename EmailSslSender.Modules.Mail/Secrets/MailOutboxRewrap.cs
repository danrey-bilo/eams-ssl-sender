using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Mail.Secrets;

/// <summary>
/// Перекладывает обёртки ключей данных неотправленных писем под действующую версию мастер-ключа.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Без этой цели <c>rewrap-keys</c> отчитывался об успехе, не тронув очередь.</b> У каждого
/// письма свой ключ данных, обёрнутый мастер-ключом; ими запечатаны тело (код входа, ссылка
/// сброса) и вложения (закрытый ключ сертификата). Выведи администратор старую версию
/// мастер-ключа после такой ротации — неотправленные письма перестали бы открываться, и насос
/// хоронил бы их одно за другим с <c>CryptographicException</c>.
///
/// ⚠ <b>Только неотправленные</b> (<c>sent_at IS NULL</c>, включая мёртвые): отправленное больше
/// не открывается никем и уходит уборкой по сроку, а его обёртка под старой версией — ровно то,
/// что выведенный ключ и должен сделать нечитаемым.
///
/// ⚠ Трогается только обёртка; тело и вложения в память не открываются. Курсор — номер письма
/// (UUIDv7, упорядочен), обновление условное по прежней обёртке — довод тот же, что у
/// <c>SystemSecretsRewrap</c>.
/// </remarks>
internal sealed class MailOutboxRewrap(SenderDbContext db, ISecretEnvelope envelope) : IRewrapTarget
{
    public string Name => "mail_outbox";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async token =>
            {
                var от = after is null ? Guid.Empty : Guid.Parse(after);
                var сколько = Math.Max(1, batch);

                var строки = await db.Set<MailOutbox>()
                    .AsNoTracking()
                    .Where(п => п.SentAt == null && п.Id.CompareTo(от) > 0)
                    .OrderBy(п => п.Id)
                    .Select(п => new { п.Id, п.DataKey })
                    .Take(сколько)
                    .ToListAsync(token);

                var переложено = 0;

                foreach (var строка in строки)
                {
                    var было = строка.DataKey;
                    var ключ = new WrappedDataKey(было);

                    // ⚠ Уже под действующей версией — не трогаем: повторный запуск после обрыва
                    // не переписывает столбец с ключом заново.
                    if (!envelope.NeedsRewrap(ключ))
                    {
                        continue;
                    }

                    var стало = envelope.Rewrap(ключ).Bytes;

                    // ⚠ Условное обновление: письмо не меняет ключ данных после постановки, но
                    // обёртку мог переложить параллельный проход — его запись не затирается.
                    переложено += await db.Set<MailOutbox>()
                        .Where(п => п.Id == строка.Id && п.DataKey == было)
                        .ExecuteUpdateAsync(set => set.SetProperty(п => п.DataKey, стало), token);
                }

                var дальше = строки.Count < сколько ? null : строки[^1].Id.ToString();

                return new RewrapBatch(строки.Count, переложено, дальше);
            },
            cancellationToken);
}
