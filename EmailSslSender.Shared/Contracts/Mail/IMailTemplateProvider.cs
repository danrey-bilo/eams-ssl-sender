namespace EmailSslSender.Shared.Contracts.Mail;

/// <summary>Языки, на которых установка умеет писать.</summary>
/// <remarks>
/// ⚠ <b>Список закрыт и совпадает с вариантами <c>ui.locale</c>.</b> Расхождение означало бы
/// настройку, которую человек может выбрать, а письмо на этом языке не придёт: он получил бы
/// молчаливый откат к чужому языку и решил, что настройка не работает.
/// </remarks>
public static class MailLocales
{
    /// <summary>Украинский.</summary>
    public const string Uk = "uk";

    /// <summary>Русский.</summary>
    public const string Ru = "ru";

    /// <summary>Английский. Он же запасной, если запрошенного нет.</summary>
    public const string En = "en";

    /// <summary>Все три, в порядке справочника.</summary>
    public static readonly IReadOnlyList<string> All = [Uk, Ru, En];
}

/// <summary>
/// Один текст письма: вид, язык, тема, тело.
/// </summary>
/// <param name="Kind">Вид письма. Он же <c>mail_outbox.kind</c> и ключ предела частоты.</param>
/// <param name="Locale">Язык из <see cref="MailLocales"/>.</param>
/// <param name="Subject">Тема. Хранится открытой — секрета в ней нет.</param>
/// <param name="Body">
/// Тело с токенами вида <c>{link}</c>, <c>{code}</c>, <c>{minutes}</c>.
/// </param>
public sealed record MailTemplate(string Kind, string Locale, string Subject, string Body);

/// <summary>
/// Модуль объявляет свои письма.
/// </summary>
/// <remarks>
/// Точка расширения того же рода, что таблицы и настройки модулей: почта собирает
/// объявленное со всех модулей и не знает заранее, какие письма будут.
///
/// ⚠ <b>Тексты объявляет тот, кто шлёт, а не почта.</b> Иначе добавление письма о блокировке
/// учётной записи требовало бы правки модуля почты — то есть ровно того, что запрещает
/// правило 1.
///
/// ⚠ <b>Все три языка обязательны у каждого вида.</b> Неполный вид означает письмо, которое
/// одному человеку придёт, а другому нет, — и обнаружится это у второго.
/// </remarks>
public interface IMailTemplateProvider
{
    /// <summary>Письма этого модуля: по одному на пару «вид + язык».</summary>
    IReadOnlyList<MailTemplate> Templates { get; }

    /// <summary>
    /// Виды этого модуля, на которые не действует предел <c>mail.rate_per_hour</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Только для писем, которые не может вызвать посторонний.</b> Предел защищает чужой
    /// ящик от публичных форм: сброс пароля, коды входа. Рассылку, которую настроил администратор
    /// со свежим вторым фактором (сертификаты по подпискам), он резал бы без всякой пользы —
    /// сотрудник на шести подписках не получил бы шестую. Вид, который запускается снаружи без
    /// входа, сюда не кладут никогда.
    /// </remarks>
    IReadOnlyCollection<string> UnthrottledKinds => [];
}
