using System.Globalization;
using System.Net;
using System.Text.Json;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;

using EmailSslSender.Modules.Settings.Api;

namespace EmailSslSender.Modules.Settings.Values;

/// <summary>
/// Разбор, проверка и упаковка значения настройки по её объявлению.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Границы и варианты берутся ИЗ ОБЪЯВЛЕНИЯ, а не из копии внутри модуля.</b> Копия
/// расходится молча: экран настроек читает объявление и предлагает диапазон 8…256, а проверка
/// берёт свои числа — и человек получает отказ на значении, которое ему только что предложили.
/// Ровно это было у порога квот: <c>WarningPercentMinimum</c> и <c>WarningPercentMaximum</c>
/// лежали константами рядом с ключом и в объявление не попадали.
///
/// ⚠ <b>Текст отказа НЕ содержит присланного значения.</b> Настройка не секрет, и соблазн
/// написать «значение 1000 больше максимума 100» велик. Но привычка печатать вход в текст ошибки
/// однажды напечатает не то — правило 14; а всё нужное для починки уже есть в
/// <c>details</c>: имя, тип, границы.
///
/// ⚠ <b>Шесть отказов, и список не сокращается.</b> Он перенесён из Python дословно, потому что
/// каждый отвечает на свой вопрос человека: «такого ключа нет» — проверь имя; «нельзя пусто» —
/// значение обязательно; «не то дно» — тип; «мало»/«много» — границы; «не из списка» — варианты.
/// Слив их в один «значение не годится», мы заставим гадать.
/// </remarks>
internal static class SettingValues
{
    /// <summary>
    /// Проверить присланное значение по объявлению и упаковать в JSON для хранения.
    /// </summary>
    /// <exception cref="AppException">Значение не подходит; код и подробности внутри.</exception>
    public static string Pack(ISettingDeclaration declaration, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var разобрано = Parse(declaration, value, out var причина);
        if (причина is { } почему)
        {
            throw Отказ(declaration, почему);
        }

        return JsonSerializer.Serialize(разобрано);
    }

    /// <summary>
    /// Прочитать хранимое значение. Негодное — <c>null</c> и никаких исключений.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Отдельный путь от <see cref="Pack"/>, и разница принципиальная.</b> При записи
    /// негодное значение — ошибка человека, и он обязан её увидеть. При чтении негодное значение
    /// уже лежит в базе: границу могли сузить выкаткой, ключ могли вывести из обращения, строку
    /// могли положить руками через psql. Бросить здесь значит уронить <b>каждый</b> запрос
    /// системы из-за одной строки в таблице — а настройки читаются на самом горячем пути.
    /// Поэтому негодное отбрасывается, и действует умолчание из объявления.
    /// </remarks>
    public static object? Read(ISettingDeclaration declaration, string json) =>
        TryRead(declaration, json, out var значение) ? значение : null;

    /// <summary>
    /// Прочитать хранимое значение и сказать, годно ли оно. Заданный <c>null</c> у обнуляемого — годен.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>Замечание ревью к PR #32, сведение ревью 2026-09-15.</b> <see cref="Read"/> отдаёт
    /// <c>null</c> и за заданный <c>null</c>, и за негодное значение, — и читатель помечал негодное
    /// значение обнуляемого ключа как заданное системой. С починкой settings-1 заданный <c>null</c>
    /// стал действовать, и вместе это давало худшее: битый предел квоты по умолчанию читался «без
    /// ограничения». Тот, кто решает, что действует, спрашивает этот метод, а не <see cref="Read"/>.
    ///
    /// мутация 2026-09-15: негодное объявлено годным → покраснели
    /// ReaderTests.Негодное_значение_обнуляемого_в_базе_даёт_умолчание_и_предупреждение и
    /// ReaderTests.Негодное_значение_в_базе_даёт_умолчание_и_предупреждение.
    /// </remarks>
    public static bool TryRead(ISettingDeclaration declaration, string json, out object? value)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        value = null;

        try
        {
            using var документ = JsonDocument.Parse(json);
            var значение = Parse(declaration, документ.RootElement, out var причина);

            if (причина is not null)
            {
                return false;
            }

            value = значение;

            return true;
        }
        catch (JsonException)
        {
            // В колонке `jsonb`, но разобрать не вышло — считаем незаданным.
            return false;
        }
    }

    /// <summary>Разобрать значение по объявлению. Причина отказа или <c>null</c>.</summary>
    private static object? Parse(ISettingDeclaration declaration, JsonElement value, out Причина? причина)
    {
        причина = null;

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            // мутация 2026-08-29: принял `null` у необнуляемого (`&& Name.Length < 0`) →
            // покраснел ValueTests.Пусто_у_необнуляемого_отвергается (правило 9).
            if (!declaration.IsNullable)
            {
                причина = Причина.НельзяПусто;
            }

            return null;
        }

        var тип = Nullable.GetUnderlyingType(declaration.ValueType) ?? declaration.ValueType;

        // ⚠ Логическое проверяется ПЕРВЫМ и отдельно. В Python это отдельная проверка, потому
        // что там `bool` наследует `int` и «истина — это единица» истинно буквально; здесь так
        // не выйдет, но обратное — `1` вместо `true` — прошло бы через числовую ветку, если
        // ветки перепутать местами. Родов значения в JSON два, и они не смешиваются.
        if (тип == typeof(bool))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                причина = Причина.НеТотТип;
                return null;
            }

            return value.GetBoolean();
        }

        if (тип == typeof(int) || тип == typeof(long))
        {
            if (value.ValueKind != JsonValueKind.Number)
            {
                причина = Причина.НеТотТип;
                return null;
            }

            // ⚠ `10.0` и `1e1` отвергаются, и это правильно: настройка объявлена целой, а
            // дробное значение в целой колонке однажды округлится не в ту сторону. Человек,
            // приславший `10.0` из формы, увидит «ожидалось целое» — потому и проверено тестом,
            // а не выяснено у пользователя.
            if (тип == typeof(int))
            {
                if (!value.TryGetInt32(out var целое))
                {
                    причина = Причина.НеТотТип;
                    return null;
                }

                return ВГраницах(declaration, целое, out причина) ? целое : null;
            }

            if (!value.TryGetInt64(out var длинное))
            {
                причина = Причина.НеТотТип;
                return null;
            }

            return ВГраницах(declaration, длинное, out причина) ? длинное : null;
        }

        if (тип == typeof(string))
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                причина = Причина.НеТотТип;
                return null;
            }

            var строка = value.GetString()!;

            // мутация 2026-08-29: обезвредил условие (`Count > int.MaxValue - 1`) →
            // покраснел ValueTests.Вариант_вне_списка_отвергается (правило 9).
            if (declaration.Choices.Count > 0 && !declaration.Choices.Contains(строка, StringComparer.Ordinal))
            {
                причина = Причина.НеИзСписка;
                return null;
            }

            // Границы у строки — это длина: так же, как у Python, и так же, как их показывает
            // экран. Ключ без границ длину не ограничивает.
            return ВГраницах(declaration, строка.Length, out причина) ? строка : null;
        }

        причина = Причина.НеТотТип;
        return null;
    }

    /// <summary>Число внутри объявленных границ. Границы необязательны.</summary>
    /// <remarks>
    /// ⚠ Зовётся только для НЕПУСТОГО значения. У <c>audit.retention_months</c> <c>null</c>
    /// законен, а <c>0</c> — нет: применив границы к пустому, мы запретили бы «хранить
    /// бессрочно».
    /// </remarks>
    private static bool ВГраницах(ISettingDeclaration declaration, double число, out Причина? причина)
    {
        причина = null;

        // мутация 2026-08-29: снял сравнение с минимумом (`+ double.NegativeInfinity`) →
        // покраснели ValueTests.Меньше_минимума_отвергается и
        // .Границы_не_применяются_к_пустому_но_применяются_к_числу (правило 9).
        if (declaration.Minimum is { } низ && число < низ)
        {
            причина = Причина.Мало;
            return false;
        }

        if (declaration.Maximum is { } верх && число > верх)
        {
            причина = Причина.Много;
            return false;
        }

        return true;
    }

    private static AppException Отказ(ISettingDeclaration declaration, Причина причина)
    {
        var текст = причина switch
        {
            Причина.НельзяПусто => "Значение обязательно.",
            Причина.НеТотТип => $"Ожидалось значение типа {Имя(declaration)}.",
            Причина.Мало => $"Значение меньше допустимого ({Число(declaration.Minimum)}).",
            Причина.Много => $"Значение больше допустимого ({Число(declaration.Maximum)}).",
            Причина.НеИзСписка =>
                $"Допустимые значения: {string.Join(", ", declaration.Choices)}.",
            _ => "Значение не подходит.",
        };

        return new AppException(
            ErrorCodes.InvalidSettingValue,
            текст,
            HttpStatusCode.UnprocessableContent,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = declaration.Name,
                ["type"] = Имя(declaration).Wire(),
                ["nullable"] = declaration.IsNullable,
                ["minimum"] = declaration.Minimum,
                ["maximum"] = declaration.Maximum,
                ["choices"] = declaration.Choices.Count > 0 ? declaration.Choices : null,
            });
    }

    /// <summary>
    /// Имя типа для клиента.
    /// </summary>
    /// <remarks>
    /// ⚠ Строка со списком вариантов называется <c>enum</c>, а не <c>string</c>: экран рисует по
    /// этому имени, и выпадающий список от поля ввода отличается именно здесь.
    /// </remarks>
    public static SettingType Имя(ISettingDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var тип = Nullable.GetUnderlyingType(declaration.ValueType) ?? declaration.ValueType;

        if (тип == typeof(bool))
        {
            return SettingType.Boolean;
        }

        if (тип == typeof(int) || тип == typeof(long))
        {
            return SettingType.Integer;
        }

        return declaration.Choices.Count > 0 ? SettingType.Enum : SettingType.String;
    }

    private static string Число(double? значение) =>
        значение?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private enum Причина
    {
        НельзяПусто,
        НеТотТип,
        Мало,
        Много,
        НеИзСписка,
    }
}
