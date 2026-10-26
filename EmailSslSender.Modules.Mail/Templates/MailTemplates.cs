using System.Text;
using EmailSslSender.Shared.Contracts.Mail;

namespace EmailSslSender.Modules.Mail.Templates;

/// <summary>
/// Всё, что объявили модули: пара «вид + язык» → текст.
/// </summary>
/// <remarks>
/// ⚠ <b>Спор двух модулей за одну пару роняет СТАРТ.</b> Тот же приём и тот же довод, что у
/// справочника событий журнала: отказ при старте останавливает выкатку и виден одной строкой;
/// отказ при первой отправке оставляет систему поднятой, а письмо — неушедшим.
///
/// ⚠ <b>Неполнота языков — тоже отказ при старте, и сообщение называет ЧЕЙ вид неполон.</b>
/// На дюжине писем четырёх модулей красный тест без имени вида не отвечает на вопрос «куда
/// идти», а идти надо в чужой модуль.
/// </remarks>
internal sealed class MailTemplates
{
    private readonly Dictionary<(string Kind, string Locale), MailTemplate> _byKey;

    /// <summary>Виды, с которых владелец снял предел частоты.</summary>
    private readonly HashSet<string> _безПредела;

    public MailTemplates(IEnumerable<IMailTemplateProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var поставщики = providers.ToList();

        _byKey = [];
        var споры = new List<string>();

        foreach (var текст in поставщики.SelectMany(п => п.Templates))
        {
            if (!MailLocales.All.Contains(текст.Locale, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Письмо «{текст.Kind}» объявлено на языке «{текст.Locale}», которого нет в "
                    + $"списке: {string.Join(", ", MailLocales.All)}. Язык, которого нет в "
                    + "настройке `ui.locale`, выбрать нельзя, и письмо на нём не придёт никому.");
            }

            if (!_byKey.TryAdd((текст.Kind, текст.Locale), текст))
            {
                споры.Add($"{текст.Kind}/{текст.Locale}");
            }
        }

        if (споры.Count > 0)
        {
            throw new InvalidOperationException(
                "Два модуля объявили одно и то же письмо: "
                + string.Join(", ", споры.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                + ". Какой из текстов уйдёт человеку, решал бы порядок регистрации модулей.");
        }

        // ⚠ Полнота проверяется ПОСЛЕ сбора всех: вид может объявляться по частям — скажем,
        // общий текст модулем, а перевод дополнением. Проверка внутри цикла запретила бы это
        // без всякой нужды.
        var неполные = _byKey.Keys
            .Select(ключ => ключ.Kind)
            .Distinct(StringComparer.Ordinal)
            .Where(вид => MailLocales.All.Any(язык => !_byKey.ContainsKey((вид, язык))))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (неполные.Count > 0)
        {
            var подробности = неполные.Select(вид =>
                $"{вид} (нет: {string.Join(", ", MailLocales.All.Where(я => !_byKey.ContainsKey((вид, я))))})");

            throw new InvalidOperationException(
                "Письмо объявлено не на всех языках: " + string.Join("; ", подробности)
                + ". Неполный вид означает письмо, которое одному человеку придёт, а другому "
                + "нет — и обнаружится это у второго.");
        }

        Kinds = [.. _byKey.Keys.Select(к => к.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // ⚠ Вид без предела обязан быть ОБЪЯВЛЕН шаблонами. Опечатка здесь иначе ничего бы не
        // сломала и ничего бы не сделала — а снятие защиты, которое молча не состоялось, так же
        // плохо, как снятое молча.
        _безПредела = new HashSet<string>(
            поставщики.SelectMany(п => п.UnthrottledKinds), StringComparer.Ordinal);

        var незнакомые = _безПредела.Where(вид => !Knows(вид)).Order(StringComparer.Ordinal).ToList();

        if (незнакомые.Count > 0)
        {
            throw new InvalidOperationException(
                "Предел частоты снят с видов писем, которых не объявил ни один модуль: "
                + string.Join(", ", незнакомые) + ".");
        }
    }

    /// <summary>Все объявленные виды писем.</summary>
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>Действует ли на вид предел <c>mail.rate_per_hour</c>.</summary>
    public bool IsThrottled(string kind) => !_безПредела.Contains(kind);

    /// <summary>Есть ли такой вид.</summary>
    public bool Knows(string kind) => Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>
    /// Отрисовать письмо: тема и тело с подставленными значениями.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Незнакомый язык откатывается на английский, а незнакомый ВИД — отказ.</b> Разница
    /// не в строгости: язык мог появиться в настройке раньше перевода, и молча написать
    /// по-английски лучше, чем не написать вовсе. Вид, которого нет, — это опечатка
    /// вызывающего, и письмо по ней не уйдёт никогда.
    /// </remarks>
    public (string Subject, string Body) Render(
        string kind, string locale, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var текст = _byKey.GetValueOrDefault((kind, locale))
            ?? _byKey.GetValueOrDefault((kind, MailLocales.En))
            ?? throw new InvalidOperationException(
                $"Письма «{kind}» не объявлял ни один модуль. Вид письма — не строка в вызове, "
                + "а объявление в `IMailTemplateProvider`.");

        return (Подставить(текст.Subject, values), MailFrame.Wrap(Подставить(текст.Body, values), текст.Locale));
    }

    /// <summary>
    /// Подставить значения в токены вида <c>{link}</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Свой проход, а не <c>string.Format</c> и не интерполяция.</b> Причина в том, что
    /// подставляемое приходит снаружи: ссылка, код, число минут. <c>string.Format</c> разбирает
    /// фигурные скобки <b>в значении</b> — и значение со скобкой либо роняет отрисовку, либо
    /// съедается. Здесь скобки в значении обычные символы: проход идёт по шаблону, а не по
    /// результату.
    ///
    /// ⚠ <b>Неизвестный токен остаётся как есть, а не превращается в пустоту.</b> Пустота — это
    /// письмо «перейдите по ссылке:» без ссылки, и человек не поймёт, что оно сломано. Токен,
    /// оставшийся видимым, читается как ошибка сразу.
    /// </remarks>
    private static string Подставить(string шаблон, IReadOnlyDictionary<string, string> values)
    {
        if (!шаблон.Contains('{', StringComparison.Ordinal))
        {
            return шаблон;
        }

        var итог = new StringBuilder(шаблон.Length);
        var i = 0;

        while (i < шаблон.Length)
        {
            var начало = шаблон.IndexOf('{', i);

            if (начало < 0)
            {
                итог.Append(шаблон, i, шаблон.Length - i);
                break;
            }

            var конец = шаблон.IndexOf('}', начало + 1);

            if (конец < 0)
            {
                итог.Append(шаблон, i, шаблон.Length - i);
                break;
            }

            итог.Append(шаблон, i, начало - i);

            var имя = шаблон[(начало + 1)..конец];

            итог.Append(values.TryGetValue(имя, out var значение)
                ? значение
                : шаблон[начало..(конец + 1)]);

            i = конец + 1;
        }

        return итог.ToString();
    }
}
