using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Tokens.Settings;

/// <summary>
/// Настройки модуля токенов.
/// </summary>
/// <remarks>
/// ⚠ <b>Пять ключей приехали из модуля личностей ДОСЛОВНО</b> (этап 10.5.4), включая
/// приставку <c>auth.</c>. Ключ настройки лежит в таблице настроек уже поднятых установок;
/// переименование означало бы, что после выкатки система молча вернулась к умолчаниям — то
/// есть срок сессии, выставленный администратором в час, стал бы двенадцатью.
///
/// Приставка <c>auth.</c> расходится с именем модуля, и расхождение названо, а не спрятано:
/// цена переименования выше цены расхождения.
/// </remarks>
internal static class TokenSettings
{
    /// <summary>Сколько живёт токен доступа, минут.</summary>
    /// <remarks>
    /// Короткий нарочно: он предъявляется на каждом запросе и потому чаще всего оказывается
    /// там, где его быть не должно, — в логе прокси, в отчёте об ошибке, в истории вкладки.
    /// Пятнадцать минут — компромисс между этим и числом обращений за обновлением.
    /// </remarks>
    public static readonly SettingKey<int> AccessTtlMinutes = new("auth.access_ttl_minutes",
        15,
        "Access token lifetime, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 1440);

    /// <summary>Через сколько простоя сессия кончается.</summary>
    /// <remarks>
    /// NIST SP 800-63B-4, §2.2.3 (AAL2): «The inactivity timeout SHOULD be no more than
    /// 1 hour». Час и взят. OWASP для приложений высокой ценности называет 2–5 минут, но
    /// это про сессию в банковском смысле; менеджер паролей открывают и читают, и
    /// пятиминутный простой выкидывал бы человека посреди работы.
    /// </remarks>
    public static readonly SettingKey<int> SessionIdleMinutes = new("auth.session_idle_minutes",
        60,
        "Idle timeout, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 10_080);

    /// <summary>Сколько сессия живёт вообще, сколько бы ею ни пользовались.</summary>
    /// <remarks>
    /// ⚠ <b>Второй срок обязателен, и он не дублирует первый.</b> Простой закрывает забытый
    /// ноутбук; абсолютный закрывает украденный токен, которым пользуются активно, — такой
    /// простоя не накопит никогда. OWASP: «All sessions should implement an absolute
    /// timeout, regardless of session activity», и называет 4–8 часов для рабочего дня.
    /// NIST AAL2 разрешает до 24. Двенадцать закрывают длинный рабочий день, не требуя
    /// входить заново в обед.
    /// </remarks>
    public static readonly SettingKey<int> SessionAbsoluteHours = new("auth.session_absolute_hours",
        12,
        "Absolute session lifetime, hours",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 720);

    /// <summary>Окно снисхождения при повторном обновлении, секунд.</summary>
    /// <remarks>
    /// ⚠ <b>Это плата за гонку вкладок, и она осознанная.</b> Две вкладки, заметившие
    /// истёкший токен доступа одновременно, пойдут обновляться обе — и вторая предъявит уже
    /// потраченный токен. Без окна это выглядело бы как кража, и человека выкинуло бы из
    /// системы за то, что у него открыто два окна.
    ///
    /// OWASP описывает ровно этот приём и признаёт остаточный риск: тот, кто украл токен и
    /// предъявил его внутри окна, тоже пройдёт. Десять секунд — столько, сколько нужно
    /// вкладке или повтору после сетевого таймаута, и мало для того, кто пришёл с украденным
    /// значением позже.
    ///
    /// ⚠ Ненулевое значение меньше десяти действует как десять: короче не покрыть повтор обмена
    /// клиентом (<c>TokenService.Окно</c>, сведение ревью 2026-09-15). Ноль — строгий режим.
    /// </remarks>
    public static readonly SettingKey<int> RefreshGraceSeconds = new("auth.refresh_grace_seconds",
        10,
        "Refresh grace window, seconds",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 0, Maximum: 300);

    /// <summary>Сколько держать закрытые строки, дней.</summary>
    /// <remarks>
    /// Не про безопасность, а про размер таблицы: доступ мёртвая строка не даёт ни секунды,
    /// а место занимает. История «кто и откуда входил» живёт в логе (события <c>auth.login</c>).
    /// </remarks>
    public static readonly SettingKey<int> RetentionDays = new("auth.session_retention_days",
        30,
        "Keep closed sessions, days",
        "",
        "auth",
        Minimum: 1, Maximum: 3650);

}

/// <summary>Объявление настроек модуля для общего экрана настроек.</summary>
internal sealed class TokenSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        TokenSettings.AccessTtlMinutes,
        TokenSettings.SessionIdleMinutes,
        TokenSettings.SessionAbsoluteHours,
        TokenSettings.RefreshGraceSeconds,
        TokenSettings.RetentionDays,
    ];
}
