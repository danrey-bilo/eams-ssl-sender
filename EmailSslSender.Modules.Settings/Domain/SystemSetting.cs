namespace EmailSslSender.Modules.Settings.Domain;

/// <summary>
/// Заданное значение системной настройки: одно на установку.
/// </summary>
/// <remarks>
/// ⚠ <b>«Не задано» выражается ОТСУТСТВИЕМ строки, а не значением.</b> Это не мелочь схемы, а
/// то, чем .NET-версия отличается от Python в лучшую сторону. Там «не задано» и заданный
/// <c>null</c> лежат в одной колонке, и различить их можно только особым часовым
/// <c>_MISSING</c> — значением, которое не является значением, и о котором обязан знать каждый
/// читатель. Здесь <c>'null'::jsonb</c> остаётся законным значением («журнал хранить
/// бессрочно»), а «не задано» — это просто нет строки.
///
/// ⚠ <b><c>UpdatedBy</c> без внешнего ключа на пользователей</b> — правило 1, тот же приём, что
/// у <c>audit_log.actor_id</c>. Цена — сирота при удалении человека; она названа на странице
/// модуля.
/// </remarks>
internal sealed class SystemSetting
{
    /// <summary>Имя настройки. Первичный ключ.</summary>
    public required string Key { get; set; }

    /// <summary>Значение в виде JSON. Хранится строкой — как `payload` уведомлений.</summary>
    public required string ValueJson { get; set; }

    /// <summary>Кто правил в последний раз. `null` — правка не человеком (миграция, команда).</summary>
    public Guid? UpdatedBy { get; set; }

    /// <summary>Когда правили в последний раз.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
