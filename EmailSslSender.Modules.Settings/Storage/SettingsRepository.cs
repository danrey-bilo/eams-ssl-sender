using EmailSslSender.Modules.Settings.Domain;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Settings.Storage;

/// <summary>
/// Единственная точка доступа к таблице настроек.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Запись идёт через отслеживание изменений, а НЕ сырым SQL, и это не вкус.</b>
/// Напрашивается <c>ExecuteSqlAsync</c> с <c>INSERT … ON CONFLICT DO UPDATE</c> — одна поездка,
/// без чтения. Но <c>ExecuteSql*</c> не участвует в <c>SaveChangesAsync</c> и при отсутствии
/// явной транзакции фиксируется <b>немедленно</b>: настройка окажется записанной, а строка
/// журнала — потерянной, если дальше что-нибудь бросит. Это прямое нарушение правила 15
/// («нет строки — нет действия»). Строк здесь единицы, а атомарность получается по построению.
///
/// ⚠ <b>Системные значения читаются ВСЕ ОДНИМ запросом.</b> Настройки читаются на самом горячем
/// пути системы: проверка прав спрашивает <c>audit.log_permission_allows</c> на каждом решении,
/// а одна проверка пароля читает пять ключей политики подряд. Запрос на ключ умножался бы на это
/// число, а не прибавлялся к нему. Таблица маленькая — десятки строк, — и брать её целиком
/// дешевле, чем ходить за каждой.
///
/// ⚠ <b>Значение хранится СТРОКОЙ JSON</b>, как <c>payload</c> уведомлений: словарь потребовал бы
/// настройки провайдера в чужом файле, а ссылки на Npgsql в этом модуле нет и быть не должно
/// (решение 0023 §1).
/// </remarks>
internal sealed class SettingsRepository(SenderDbContext db, TimeProvider clock)
{
    /// <summary>Все заданные системные значения: имя → JSON.</summary>
    /// <remarks>
    /// ⚠ <b>Строка с неизвестным ключом сюда попадает и отбрасывается ВЫШЕ</b>, при разборе по
    /// объявлению. Фильтровать по справочнику здесь значило бы дать хранилищу знать про
    /// справочник — и потерять возможность увидеть в базе то, что там на самом деле лежит.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> SystemAsync(
        CancellationToken cancellationToken = default) =>
        await db.Set<SystemSetting>()
            .AsNoTracking()
            .ToDictionaryAsync(строка => строка.Key, строка => строка.ValueJson, StringComparer.Ordinal, cancellationToken);

    /// <summary>Задать системное значение. Повторная запись того же ключа строк не плодит.</summary>
    /// <remarks>
    /// ⚠ <b>Не зовёт <c>SaveChangesAsync</c>.</b> Фиксацией распоряжается вызывающий — вместе со
    /// строкой журнала и в одной транзакции с ней (правило 15).
    /// </remarks>
    public async Task SetSystemAsync(
        string key, string valueJson, Guid? updatedBy, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<SystemSetting>()
            .FirstOrDefaultAsync(запись => запись.Key == key, cancellationToken);

        if (строка is null)
        {
            db.Set<SystemSetting>().Add(new SystemSetting
            {
                Key = key,
                ValueJson = valueJson,
                UpdatedBy = updatedBy,
                UpdatedAt = clock.GetUtcNow(),
            });

            return;
        }

        строка.ValueJson = valueJson;
        строка.UpdatedBy = updatedBy;
        строка.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>Убрать заданное системное значение — настройка возвращается к умолчанию.</summary>
    /// <returns><c>true</c>, если строка была.</returns>
    public async Task<bool> ResetSystemAsync(string key, CancellationToken cancellationToken = default)
    {
        var строка = await db.Set<SystemSetting>()
            .FirstOrDefaultAsync(запись => запись.Key == key, cancellationToken);

        if (строка is null)
        {
            return false;
        }

        db.Set<SystemSetting>().Remove(строка);
        return true;
    }
}
