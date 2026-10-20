using EmailSslSender.Modules.Settings.Api;
using System.Text.Json;
using EmailSslSender.Modules.Settings.Values;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Settings.Tests;

/// <summary>
/// Проверка значения настройки: шесть отказов и то, что проходит.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Зеркальные факты обязательны, и это правило 9, а не педантизм.</b> Проверка,
/// испытанная только отказом, может отказывать <b>всегда</b>; испытанная только успехом —
/// принимать всегда, и это опаснее: настройка защиты примет любое значение, включая то, которое
/// её выключает. Поэтому у каждой границы здесь есть пара «за» и «против».
///
/// ⚠ <b>Границы берутся из ОБЪЯВЛЕНИЯ.</b> Ни одного числа, вписанного в проверку руками, здесь
/// нет — иначе тест доказывал бы, что копия согласна сама с собой.
/// </remarks>
public sealed class ValueTests
{
    private static readonly SettingKey<int> Длина = new(
        "test.length", 12, "Length", "", "test", Minimum: 8, Maximum: 256);

    private static readonly SettingKey<int?> Срок = new(
        "test.months", null, "Months", "", "test", Minimum: 1, Maximum: 120);

    private static readonly SettingKey<bool> Флаг = new("test.flag", true, "Flag", "", "test");

    private static readonly SettingKey<string> Тема = new(
        "test.theme", "system", "Theme", "", "test", Choices: ["light", "dark", "system"]);

    // --- то, что проходит -------------------------------------------------------------------

    [Fact]
    public void Годное_число_принимается_и_упаковывается()
    {
        Pack(Длина, "12").ShouldBe("12");
        Pack(Длина, "8").ShouldBe("8");
        Pack(Длина, "256").ShouldBe("256");
    }

    [Fact]
    public void Годный_вариант_принимается()
    {
        Pack(Тема, "\"dark\"").ShouldBe("\"dark\"");
    }

    [Fact]
    public void Логическое_принимается_обоими_значениями()
    {
        Pack(Флаг, "true").ShouldBe("true");
        Pack(Флаг, "false").ShouldBe("false");
    }

    [Fact]
    public void Пусто_у_обнуляемого_принимается()
    {
        // «Хранить журнал бессрочно» — это заданный `null`, а не отсутствие настройки.
        Pack(Срок, "null").ShouldBe("null");
    }

    // --- шесть отказов ----------------------------------------------------------------------

    [Fact]
    public void Пусто_у_необнуляемого_отвергается()
    {
        Отказ(Длина, "null").Code.ShouldBe(ErrorCodes.InvalidSettingValue);
    }

    [Fact]
    public void Строка_вместо_числа_отвергается()
    {
        var отказ = Отказ(Длина, "\"12\"");

        отказ.Code.ShouldBe(ErrorCodes.InvalidSettingValue);
        отказ.Details!["type"].ShouldBe("integer");
    }

    [Fact]
    public void Дробное_вместо_целого_отвергается()
    {
        // ⚠ Проверено, а не предположено: `System.Text.Json` разбирает `10.0` в `int` отказом,
        // и `1e1` тоже. Человек, приславший `10.0` из веб-формы, увидит «ожидалось целое» —
        // это правильное поведение, но выяснять его у пользователя было бы поздно.
        Отказ(Длина, "10.0").Code.ShouldBe(ErrorCodes.InvalidSettingValue);
        Отказ(Длина, "1e1").Code.ShouldBe(ErrorCodes.InvalidSettingValue);
    }

    [Fact]
    public void Меньше_минимума_отвергается()
    {
        var отказ = Отказ(Длина, "7");

        отказ.Code.ShouldBe(ErrorCodes.InvalidSettingValue);
        отказ.Details!["minimum"].ShouldBe(8d);
    }

    [Fact]
    public void Больше_максимума_отвергается()
    {
        Отказ(Длина, "257").Details!["maximum"].ShouldBe(256d);
    }

    [Fact]
    public void Вариант_вне_списка_отвергается()
    {
        var отказ = Отказ(Тема, "\"neon\"");

        отказ.Code.ShouldBe(ErrorCodes.InvalidSettingValue);
        отказ.Message.ShouldContain("light");
    }

    [Fact]
    public void Единица_не_проходит_за_истину()
    {
        // ⚠ В Python это отдельная проверка, потому что там `bool` наследует `int` и «истина —
        // это единица» истинно буквально. Здесь так не выйдет по устройству языка, но родов
        // значения в JSON два, и перепутанные ветки разбора пропустили бы `1` в логическое.
        Отказ(Флаг, "1").Code.ShouldBe(ErrorCodes.InvalidSettingValue);
        Отказ(Флаг, "\"true\"").Code.ShouldBe(ErrorCodes.InvalidSettingValue);
    }

    // --- границы у обнуляемого --------------------------------------------------------------

    [Fact]
    public void Границы_не_применяются_к_пустому_но_применяются_к_числу()
    {
        // ⚠ Пара, и без неё ни один из фактов ничего не значит: у `audit.retention_months`
        // `null` законен, а `0` — нет. Применив границы к пустому, мы запретили бы «хранить
        // бессрочно»; не применив к числу — разрешили бы ноль месяцев, то есть немедленную
        // чистку журнала.
        Pack(Срок, "null").ShouldBe("null");
        Отказ(Срок, "0").Code.ShouldBe(ErrorCodes.InvalidSettingValue);
        Pack(Срок, "1").ShouldBe("1");
    }

    // --- текст отказа -----------------------------------------------------------------------

    [Fact]
    public void Текст_отказа_не_содержит_присланного_значения()
    {
        // ⚠ Настройка не секрет, и соблазн написать «значение 999 больше 256» велик. Но
        // привычка печатать вход в текст ошибки однажды напечатает не то (правило 14), а всё
        // нужное для починки уже лежит в `details`.
        var отказ = Отказ(Длина, "999");

        отказ.Message.ShouldNotContain("999");
        отказ.Details!["key"].ShouldBe("test.length");
    }

    // --- чтение хранимого -------------------------------------------------------------------

    [Fact]
    public void Негодное_хранимое_значение_отбрасывается_а_не_роняет()
    {
        // ⚠ Отдельный путь от записи, и разница принципиальная. При записи негодное — ошибка
        // человека, он обязан её увидеть. При чтении негодное УЖЕ ЛЕЖИТ в базе: границу могли
        // сузить выкаткой, строку могли положить руками через psql. Бросить здесь значит
        // уронить каждый запрос системы из-за одной строки в таблице.
        SettingValues.Read(Длина, "999").ShouldBeNull();
        SettingValues.Read(Длина, "\"не число\"").ShouldBeNull();
        SettingValues.Read(Длина, "не json вовсе").ShouldBeNull();

        // А годное — читается.
        SettingValues.Read(Длина, "12").ShouldBe(12);
    }

    [Fact]
    public void Имя_типа_отличает_список_вариантов_от_строки()
    {
        // Экран рисует по этому имени: выпадающий список от поля ввода отличается здесь.
        SettingValues.Имя(Тема).ShouldBe(SettingType.Enum);
        SettingValues.Имя(new SettingKey<string>("t.s", "x", "S", "", "t")).ShouldBe(SettingType.String);
        SettingValues.Имя(Длина).ShouldBe(SettingType.Integer);
        SettingValues.Имя(Флаг).ShouldBe(SettingType.Boolean);
        SettingValues.Имя(Срок).ShouldBe(SettingType.Integer);
    }

    [Fact]
    public void Имя_типа_уезжает_наружу_теми_же_строками()
    {
        // ⚠ ⚠ Вскрытие 44: сервер отдавал `"boolean"`, экран сравнивал с `'bool'` — и не
        // совпадало НИКОГДА, отчего булева настройка рисовалась текстовым полем со словом
        // `false` внутри. Сверять было не с чем: в контракте стояла свободная строка.
        //
        // Теперь строки задаёт перечисление, и они уезжают в контракт списком. Факт держит
        // САМИ СТРОКИ: переименование значения — ломающее изменение (правило 30), клиент
        // рисует по ним.
        Строкой(SettingType.Boolean).ShouldBe("boolean");
        Строкой(SettingType.Integer).ShouldBe("integer");
        Строкой(SettingType.Enum).ShouldBe("enum");
        Строкой(SettingType.String).ShouldBe("string");
    }

    /// <summary>Как значение уходит в JSON — тем же сериализатором, что и в ответе.</summary>
    private static string Строкой(SettingType значение) =>
        System.Text.Json.JsonSerializer.Serialize(значение).Trim('"');

    private static string Pack(ISettingDeclaration ключ, string json)
    {
        using var документ = JsonDocument.Parse(json);
        return SettingValues.Pack(ключ, документ.RootElement);
    }

    private static AppException Отказ(ISettingDeclaration ключ, string json) =>
        Should.Throw<AppException>(() => Pack(ключ, json));
}
