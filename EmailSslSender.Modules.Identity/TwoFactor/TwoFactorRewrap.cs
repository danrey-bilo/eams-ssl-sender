using System.Globalization;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Перекладывает обёртки ключей второго фактора под действующую версию мастер-ключа.
/// </summary>
/// <remarks>
/// Единственное на сегодня запечатанное в системе — секрет второго фактора и обёрнутый ключ
/// данных при нём (<c>user_two_factor</c>). Придут хранилища и учётные данные — заведут свои
/// такие же реализации, и ротация накроет их <b>без единой правки здесь</b>: ходит по всем
/// <see cref="IRewrapTarget"/>, а кто в этом списке — ей безразлично.
///
/// ⚠ <b>Трогается только обёртка.</b> Сам секрет не расшифровывается и не перешифровывается
/// вовсе: мастер-ключ шифрует ключ данных, а не данные. Отсюда важное следствие — открытый
/// секрет в память при перекладывании не попадает ни на мгновение, и ротацию можно гонять
/// когда угодно.
///
/// ⚠ <b>Отбирать «нуждающиеся» запросом нельзя.</b> Версия ключа лежит внутри байтов обёртки,
/// а не колонкой, поэтому проход идёт по всем строкам подряд и решает по каждой отдельно.
/// Курсор — идентификатор человека: он неизменен, уникален и упорядочен, а значит порция не
/// перескочит строку и не покажет её дважды.
/// </remarks>
internal sealed class TwoFactorRewrap(SenderDbContext db, ISecretEnvelope envelope) : IRewrapTarget
{
    public string Name => "user_two_factor";

    public Task<RewrapBatch> RewrapAsync(
        int batch, string? after = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(
            async (token) =>
            {
                var from = Guid.TryParse(after, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : Guid.Empty;

                // Без отслеживания: писать будем условным обновлением, а не через трекер.
                var rows = await db.Set<UserTwoFactor>()
                    .Where(row => row.UserId.CompareTo(from) > 0)
                    .OrderBy(row => row.UserId)
                    .Take(Math.Max(1, batch))
                    .AsNoTracking()
                    .ToListAsync(token);

                if (rows.Count == 0)
                {
                    return new RewrapBatch(0, 0, null);
                }

                var moved = 0;

                foreach (var row in rows)
                {
                    var было = row.DataKey;
                    var key = new WrappedDataKey(было);

                    // ⚠ Уже под действующей версией — не трогаем. Без этой проверки повторный
                    // запуск переписывал бы столбец с ключом у всех подряд, а перезапись
                    // столбца с ключом — единственное место, где сбой стоит секрета.
                    if (!envelope.NeedsRewrap(key))
                    {
                        continue;
                    }

                    var стало = envelope.Rewrap(key).Bytes;

                    // ⚠ <b>Условным обновлением, а не присваиванием.</b> Между чтением порции и
                    // записью человек мог перезавести себе фактор: `SetupAsync` заменяет и ключ,
                    // и секрет. Присвоив своё, мы положили бы обёртку от СТАРОГО ключа поверх
                    // нового — а секрет рядом уже новый, и открыть его стало бы нечем. Это не
                    // «строка потерялась»: это молча испорченный второй фактор у конкретного
                    // человека, и обнаружит он это на входе. При порции в двести строк окно
                    // между чтением и записью — не микросекунды.
                    //
                    // Не сошлось — пропускаем: значит фактор перезавели, и его обёртка и так
                    // под действующей версией.
                    var затронуто = await db.Set<UserTwoFactor>()
                        .Where(candidate => candidate.UserId == row.UserId && candidate.DataKey == было)
                        .ExecuteUpdateAsync(
                            set => set.SetProperty(candidate => candidate.DataKey, стало), token);

                    if (затронуто == 1)
                    {
                        moved++;
                    }
                }

                // Прочитали меньше, чем просили — значит дошли до конца.
                var next = rows.Count < Math.Max(1, batch)
                    ? null
                    : rows[^1].UserId.ToString();

                return new RewrapBatch(rows.Count, moved, next);
            },
            cancellationToken);
}
