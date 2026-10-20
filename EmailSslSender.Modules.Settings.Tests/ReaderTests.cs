using EmailSslSender.Modules.Settings.Reading;
using EmailSslSender.Modules.Settings.Storage;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Settings.Tests;

/// <summary>
/// Три слоя разрешения значения и цена чтения.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Источников ТРИ, а не два, и третий завёл не педантизм.</b> Python сливает файл и
/// константу в одно слово <c>default</c>. Последствие видно на живом администраторе: строка
/// задана в базе, он правит конфигурацию, перезапускает — и ничего не меняется, <b>без
/// объяснения</b>, потому что экран говорит «умолчание» в обоих случаях.
///
/// ⚠ <b>Тесты фильтруют по своим ключам.</b> База живёт весь прогон коллекции, и раздел
/// конфигурации общий на обвязку: утверждение о содержимом таблицы целиком краснело бы от
/// соседнего теста, а не от дефекта.
/// </remarks>
[Collection(SettingsDatabaseCollection.Name)]
public sealed class ReaderTests(SettingsDatabase база)
{
    [Fact]
    public async Task Без_строки_и_без_файла_действует_умолчание()
    {
        var ключ = Ключ("plain", 10);

        await using var scope = база.Scope();
        var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

        найдено.Value.ShouldBe(10);
        найдено.Source.ShouldBe(SettingsSource.Default);
    }

    [Fact]
    public async Task Файл_перекрывает_умолчание()
    {
        var ключ = Ключ("from-file", 10);
        база.Файл[$"Settings:{ключ.Name}"] = "42";

        await using var scope = база.Scope();
        var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

        найдено.Value.ShouldBe(42);
        найдено.Source.ShouldBe(SettingsSource.Configuration);
    }

    [Fact]
    public async Task База_перекрывает_файл_а_её_снятие_возвращает_файл()
    {
        var ключ = Ключ("layers", 10);
        база.Файл[$"Settings:{ключ.Name}"] = "42";

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ.Name, "99", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

            найдено.Value.ShouldBe(99);
            найдено.Source.ShouldBe(SettingsSource.System);
        }

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.ResetSystemAsync(ключ.Name, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            // ⚠ Снятие строки возвращает СЛЕДУЮЩИЙ слой, а не умолчание. Иначе «сбросить» на
            // экране означало бы «выключить и файл тоже», и администратор, задавший значение
            // в файле, потерял бы его нажатием кнопки в вебе.
            var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

            найдено.Value.ShouldBe(42);
            найдено.Source.ShouldBe(SettingsSource.Configuration);
        }
    }

    [Fact]
    public async Task Одна_область_один_запрос_сколько_бы_ключей_ни_читали()
    {
        // ⚠ Настройки читаются на самом горячем пути: одна проверка пароля читает ПЯТЬ ключей
        // подряд, а проверка прав спрашивает свой на каждом решении. Запрос на ключ умножался
        // бы на это число, а не прибавлялся к нему. Проверяется счётчиком, а не глазами:
        // «читатель кэширует» нельзя доказать чтением кода.
        var ключи = Enumerable.Range(0, 5).Select(i => Ключ($"batch-{i}", i)).ToList();

        await using var scope = база.Scope();
        var читатель = Читатель(scope);

        // Первое чтение поднимает снимок; счётчик обнуляем после него, чтобы не считать
        // команды создания области.
        await читатель.ResolveAsync(ключи[0], TestContext.Current.CancellationToken);
        база.Команды.Сбросить();

        foreach (var ключ in ключи)
        {
            await читатель.ResolveAsync(ключ, TestContext.Current.CancellationToken);
        }

        база.Команды.Счёт.ShouldBe(0, "снимок таблицы обязан браться один раз на область");
    }

    [Fact]
    public async Task Новая_область_читает_заново()
    {
        // Зеркальный факт: без него предыдущий доказывал бы и «читатель кэширует навсегда» —
        // а тогда правка настройки не действовала бы до перезапуска процесса.
        var ключ = Ключ("fresh", 1);

        await using (var scope = база.Scope())
        {
            await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ.Name, "7", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            (await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken))
                .Value.ShouldBe(7);
        }
    }

    [Fact]
    public async Task Негодное_значение_в_базе_даёт_умолчание_и_предупреждение()
    {
        var ключ = Ключ("broken", 10, Minimum: 1, Maximum: 100);

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(
                ключ.Name, "\"много\"", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

            // ⚠ Умолчание, а не пятисотка: строка могла стать негодной без чьей-либо ошибки —
            // границу сузили выкаткой, значение положили руками через psql. Уронить на этом
            // запрос значит уронить КАЖДЫЙ запрос системы из-за одной строки в таблице.
            найдено.Value.ShouldBe(10);
            найдено.Source.ShouldBe(SettingsSource.Default);
        }

        // ⚠ И не молча: администратор уверен, что задал значение, а действует другое.
        база.Логи.Строки.ShouldContain(
            строка => строка.Contains("setting_value_invalid", StringComparison.Ordinal)
                && строка.Contains(ключ.Name, StringComparison.Ordinal));
    }

    /// <summary>Негодное значение обнуляемого ключа даёт умолчание и предупреждение, а не «пусто».</summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ Замечание ревью к PR #32, сведение ревью 2026-09-15. Разбор отдавал <c>null</c> и за
    /// заданный <c>null</c>, и за негодное значение; читатель помечал оба «задано системой», а с
    /// починкой settings-1 заданный <c>null</c> стал действовать. Итог — битый предел квоты по
    /// умолчанию читался «без ограничения», срок хранения — «бессрочно».
    /// </remarks>
    [Fact]
    public async Task Негодное_значение_обнуляемого_в_базе_даёт_умолчание_и_предупреждение()
    {
        var ключ = new SettingKey<int?>(
            $"test.nullable.broken.{Guid.NewGuid():N}", 12, "Months", "", "test", Minimum: 1, Maximum: 120);

        await ЗаписатьAsync(ключ.Name, "999");

        await using (var scope = база.Scope())
        {
            var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

            найдено.Value.ShouldBe(12);
            найдено.Source.ShouldBe(SettingsSource.Default);
        }

        await using (var scope = база.Scope())
        {
            (await Читатель(scope).GetAsync(ключ, TestContext.Current.CancellationToken))
                .ShouldBe(12, "битое значение обнуляемого ключа читалось пустым, то есть без ограничения");
        }

        база.Логи.Строки.ShouldContain(
            строка => строка.Contains("setting_value_invalid", StringComparison.Ordinal)
                && строка.Contains(ключ.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Заданный_null_у_обнуляемого_действует_а_не_считается_негодным()
    {
        // ⚠ Тот самый случай, ради которого «не задано» выражается отсутствием строки:
        // `null` — это ЗАДАННОЕ значение «хранить бессрочно», и умолчание его перекрывать не
        // должно, даже если умолчание непустое.
        var ключ = new SettingKey<int?>(
            $"test.nullable.{Guid.NewGuid():N}", 12, "Months", "", "test", Minimum: 1, Maximum: 120);

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ.Name, "null", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

            найдено.Value.ShouldBeNull();
            найдено.Source.ShouldBe(SettingsSource.System);
        }
    }

    /// <summary>
    /// Очищенная строка с непустым умолчанием читается кодом пустой, а не умолчанием.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (settings-1). Прежний тест выше проверял только <c>ResolveAsync</c> —
    /// экран, — а код читает через <c>GetAsync</c>, и там заданный <c>null</c> подменялся
    /// умолчанием. Живой случай — <c>server.public_url</c> с умолчанием
    /// <c>https://sender.example.com</c>: экран «пусто», письма со ссылками на заглушку.
    /// </remarks>
    [Fact]
    public async Task Очищенная_строка_с_непустым_умолчанием_читается_пустой()
    {
        var ключ = new SettingKey<string>(
            $"test.nullable.url.{Guid.NewGuid():N}", "https://sender.example.com", "Url", "", "test",
            Nullable: true);

        await ЗаписатьAsync(ключ.Name, "null");

        await using var scope = база.Scope();

        (await Читатель(scope).GetAsync(ключ, TestContext.Current.CancellationToken)).ShouldBeNull(
            "экран показывает «пусто», а код получил заглушку из умолчания");
    }

    /// <summary>Очищенный предел с непустым умолчанием — безлимит, а не умолчание.</summary>
    /// <remarks>
    /// ⚠ Случай <c>quotas.default.devices</c>: умолчание 5, пояснение «пусто — безлимит».
    /// Администратор снимал предел, а люди продолжали получать <c>QUOTA_EXCEEDED</c>.
    /// </remarks>
    [Fact]
    public async Task Очищенный_предел_с_непустым_умолчанием_читается_безлимитом()
    {
        var ключ = new SettingKey<long?>(
            $"test.nullable.limit.{Guid.NewGuid():N}", 5, "Limit", "", "test", Minimum: 0);

        await ЗаписатьAsync(ключ.Name, "null");

        await using var scope = база.Scope();

        (await Читатель(scope).GetAsync(ключ, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>Зеркало: без строки в базе действует умолчание, как и прежде.</summary>
    [Fact]
    public async Task Незаданный_обнуляемый_ключ_читается_умолчанием()
    {
        var ключ = new SettingKey<long?>(
            $"test.nullable.absent.{Guid.NewGuid():N}", 5, "Limit", "", "test", Minimum: 0);

        await using var scope = база.Scope();

        (await Читатель(scope).GetAsync(ключ, TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    /// <summary>
    /// Логическое из appsettings.json («True» с заглавной) действует.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (settings-2). Поставщик JSON-конфигурации хранит <c>true</c> строкой
    /// «True», и прежний разбор отдавал её в JSON как есть: предупреждение и умолчание. Тесты
    /// задавали файлом только числа, поэтому случай не был виден.
    /// </remarks>
    [Fact]
    public async Task Логическое_из_файла_с_заглавной_действует()
    {
        var ключ = new SettingKey<bool>(
            $"test.reader.flag.{Guid.NewGuid():N}", false, "Flag", "", "test");
        база.Файл[$"Settings:{ключ.Name}"] = "True";

        await using var scope = база.Scope();
        var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

        найдено.Value.ShouldBe(true);
        найдено.Source.ShouldBe(SettingsSource.Configuration);
    }

    [Fact]
    public async Task Негодное_значение_в_файле_даёт_умолчание_и_предупреждение()
    {
        var ключ = Ключ("file-broken", 10, Minimum: 1, Maximum: 100);
        база.Файл[$"Settings:{ключ.Name}"] = "1000";

        await using var scope = база.Scope();
        var найдено = await Читатель(scope).ResolveAsync(ключ, TestContext.Current.CancellationToken);

        найдено.Value.ShouldBe(10);
        найдено.Source.ShouldBe(SettingsSource.Default);
        база.Логи.Строки.ShouldContain(
            строка => строка.Contains("setting_value_invalid_configuration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Контракт_ISettingsReader_отдаёт_то_же_значение()
    {
        // Десять боевых потребителей зовут именно его, и ни один из них не правился.
        var ключ = Ключ("contract", 5);
        база.Файл[$"Settings:{ключ.Name}"] = "77";

        await using var scope = база.Scope();
        var читатель = scope.ServiceProvider.GetRequiredService<ISettingsReader>();

        (await читатель.GetAsync(ключ, TestContext.Current.CancellationToken)).ShouldBe(77);
    }

    [Fact]
    public async Task Все_объявления_отдаются_с_источниками()
    {
        // Экрану настроек (16.7) нужен именно этот вид: объявление плюс действующее значение.
        await using var scope = база.Scope();
        var всё = await Читатель(scope).AllAsync(TestContext.Current.CancellationToken);

        всё.ShouldNotBeEmpty();
        всё.ShouldContain(пара => пара.Declaration.Name == "ui.locale");
        всё.ShouldAllBe(пара => пара.Declaration.Name.Length > 0);
    }

    private static SettingKey<int> Ключ(
        string имя, int умолчание, double? Minimum = null, double? Maximum = null) =>
        new($"test.reader.{имя}.{Guid.NewGuid():N}", умолчание, имя, "", "test",
            Minimum: Minimum, Maximum: Maximum);

    private async Task ЗаписатьAsync(string имя, string json)
    {
        await using var scope = база.Scope();
        var (repo, db) = Слои(scope);

        await repo.SetSystemAsync(имя, json, null, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static SettingsReader Читатель(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<SettingsReader>();

    private static (SettingsRepository, SenderDbContext) Слои(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<SettingsRepository>(),
         scope.ServiceProvider.GetRequiredService<SenderDbContext>());
}
