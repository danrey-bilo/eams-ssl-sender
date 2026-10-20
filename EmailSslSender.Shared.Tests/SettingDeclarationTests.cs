using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Объявление настройки читается без рефлексии, и то, что оно о себе сообщает, — правда.
/// </summary>
/// <remarks>
/// ⚠ <b>Смысл этого файла — в отсутствии <c>GetType()</c> и <c>typeof(SettingKey&lt;&gt;)</c>
/// во всей системе.</b> Пока объявления собирались в <c>IReadOnlyList&lt;object&gt;</c>, экран
/// настроек мог узнать их тип только рефлексией — а рефлексия по обобщённому типу отказывает
/// не при сборке, а в рантайме, то есть у пользователя и на самом видном экране.
///
/// <para>
/// Проверяется здесь ровно то, что нельзя проверить компилятором: что <c>ValueType</c> не
/// теряет обнуляемость, что <c>IsNullable</c> выводится по объявленному правилу и что список
/// допустимых значений нельзя испортить снаружи.
/// </para>
/// </remarks>
public sealed class SettingDeclarationTests
{
    [Fact]
    public void Обнуляемое_число_объявляет_себя_обнуляемым()
    {
        ISettingDeclaration ключ = new SettingKey<int?>(
            "x.months", Default: null, "Months", "", "x");

        ключ.IsNullable.ShouldBeTrue();
        ключ.ValueType.ShouldBe(typeof(int?));
        ключ.DefaultValue.ShouldBeNull();
    }

    [Fact]
    public void Необнуляемое_число_с_умолчанием_не_обнуляемо()
    {
        ISettingDeclaration ключ = new SettingKey<int>("x.count", Default: 10, "Count", "", "x");

        ключ.IsNullable.ShouldBeFalse();
        ключ.ValueType.ShouldBe(typeof(int));
        ключ.DefaultValue.ShouldBe(10);
    }

    [Fact]
    public void Логическое_остаётся_логическим_а_не_становится_числом()
    {
        // Мелочь на вид, и ровно она ломает экран: `bool`, приехавший как `int`, рисуется
        // полем ввода вместо переключателя, и настройка защиты правится набором «1».
        ISettingDeclaration ключ = new SettingKey<bool>("x.flag", Default: true, "Flag", "", "x");

        ключ.ValueType.ShouldBe(typeof(bool));
        ключ.DefaultValue.ShouldBe(true);
        ключ.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void Строка_с_умолчанием_null_считается_обнуляемой()
    {
        // Ссылочная обнуляемость в рантайме не видна: `SettingKey<string>` и
        // `SettingKey<string?>` — один тип. Единственная улика — умолчание.
        ISettingDeclaration ключ = new SettingKey<string?>("x.url", Default: null, "Url", "", "x");

        ключ.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void Строка_с_непустым_умолчанием_объявляет_обнуляемость_явно()
    {
        // ⚠ Тот самый случай, где вывод по умолчанию НЕВЕРЕН: умолчание есть, а `null`
        // законен. Первым таким ключом будет `server.public_url` у `Ops` (этап 17); ошибка
        // здесь означает `nullable: false` у веба и пропавшую кнопку «очистить».
        ISettingDeclaration выведено = new SettingKey<string>("x.name", "eams", "Name", "", "x");
        ISettingDeclaration задано = new SettingKey<string>(
            "x.name", "eams", "Name", "", "x", Nullable: true);

        выведено.IsNullable.ShouldBeFalse();
        задано.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void Границы_и_варианты_доступны_без_рефлексии()
    {
        ISettingDeclaration ключ = new SettingKey<int>(
            "x.length", Default: 12, "Length", "", "x", Minimum: 8, Maximum: 256);

        ключ.Minimum.ShouldBe(8);
        ключ.Maximum.ShouldBe(256);
        ключ.Choices.ShouldBeEmpty();
    }

    [Fact]
    public void Список_вариантов_нельзя_испортить_снаружи()
    {
        // ⚠ Объявление живёт синглтоном на процесс. Переданный массив копируется, иначе
        // потребитель, отсортировавший его «для показа», поменял бы порядок всем и навсегда.
        var исходный = new[] { "none", "starttls", "ssl" };
        ISettingDeclaration ключ = new SettingKey<string>(
            "x.security", "none", "Security", "", "x", Choices: исходный);

        исходный[0] = "испорчено";

        ключ.Choices.ShouldBe(["none", "starttls", "ssl"]);
    }

    [Fact]
    public void Область_по_умолчанию_системная()
    {
        // Умолчание выбрано так, что забытый параметр даёт настройку установки, а не личную:
        // личная, ставшая системной по недосмотру, была бы видна всем; наоборот — правилась
        // бы каждым по-своему, и «почему у нас разные пороги» выяснялось бы месяцами.
        ISettingDeclaration системная = new SettingKey<int>("x.a", 1, "A", "", "x");
        ISettingDeclaration личная = new SettingKey<int>(
            "x.b", 1, "B", "", "x", Scope: SettingScope.User);

        системная.Scope.ShouldBe(SettingScope.System);
        личная.Scope.ShouldBe(SettingScope.User);
    }

    [Fact]
    public void Признак_перезапуска_по_умолчанию_снят()
    {
        ISettingDeclaration ключ = new SettingKey<int>("x.c", 1, "C", "", "x");

        ключ.RequiresRestart.ShouldBeFalse();
    }
}
