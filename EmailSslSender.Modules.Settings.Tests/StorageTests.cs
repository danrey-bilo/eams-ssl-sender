using EmailSslSender.Modules.Settings.Domain;
using EmailSslSender.Modules.Settings.Storage;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Settings.Tests;

/// <summary>
/// Два слоя хранения на настоящей базе.
/// </summary>
/// <remarks>
/// ⚠ <b>Главный факт файла — <c>'null'::jsonb</c> отличается от отсутствия строки.</b> На нём
/// стоит вся модель: «журнал хранить бессрочно» это заданное значение <c>null</c>, а «настройку
/// не трогали» — отсутствие строки. В Python оба лежат в одной колонке и различаются часовым
/// <c>_MISSING</c> — значением, которое не является значением и о котором обязан знать каждый
/// читатель.
///
/// ⚠ Тесты фильтруют по СВОИМ ключам. База живёт весь прогон коллекции, и утверждение «в
/// таблице ровно одна строка» краснело бы от соседнего теста, а не от дефекта.
/// </remarks>
[Collection(SettingsDatabaseCollection.Name)]
public sealed class StorageTests(SettingsDatabase база)
{
    [Fact]
    public async Task Системное_значение_пишется_и_читается()
    {
        var ключ = Ключ("write");
        var кто = Guid.NewGuid();

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ, "42", кто, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var (repo, _) = Слои(scope);
            var всё = await repo.SystemAsync(TestContext.Current.CancellationToken);

            всё[ключ].ShouldBe("42");
        }
    }

    [Fact]
    public async Task Заданный_null_и_отсутствие_строки_различаются()
    {
        var заданный = Ключ("null-set");
        var нетстроки = Ключ("null-absent");

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(заданный, "null", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var (repo, _) = Слои(scope);
            var всё = await repo.SystemAsync(TestContext.Current.CancellationToken);

            // Заданный `null` — это СТРОКА со значением `null`.
            всё.ContainsKey(заданный).ShouldBeTrue();
            всё[заданный].ShouldBe("null");

            // «Не задано» — это отсутствие строки, и никакого часового для этого не нужно.
            всё.ContainsKey(нетстроки).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Повторная_запись_того_же_ключа_строк_не_плодит()
    {
        var ключ = Ключ("twice");

        for (var раз = 0; раз < 3; раз++)
        {
            await using var scope = база.Scope();
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ, $"{раз}", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            база.Часы.Вперёд(TimeSpan.FromMinutes(1));
        }

        await using (var scope = база.Scope())
        {
            var (_, db) = Слои(scope);
            var строки = await db.Set<SystemSetting>()
                .AsNoTracking()
                .Where(строка => строка.Key == ключ)
                .ToListAsync(TestContext.Current.CancellationToken);

            строки.Count.ShouldBe(1);
            строки[0].ValueJson.ShouldBe("2");
        }
    }

    [Fact]
    public async Task Время_правки_двигается_вместе_с_часами()
    {
        var ключ = Ключ("stamp");
        DateTimeOffset первое;

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ, "1", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            первое = await Время(scope, ключ);
        }

        база.Часы.Вперёд(TimeSpan.FromHours(3));

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ, "2", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            (await Время(scope, ключ)).ShouldBe(первое.AddHours(3));
        }
    }

    [Fact]
    public async Task Сброс_убирает_строку_и_сообщает_была_ли_она()
    {
        var ключ = Ключ("reset");

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ, "7", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);

            (await repo.ResetSystemAsync(ключ, TestContext.Current.CancellationToken)).ShouldBeTrue();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            (await repo.SystemAsync(TestContext.Current.CancellationToken))
                .ContainsKey(ключ).ShouldBeFalse();
        }

        await using (var scope = база.Scope())
        {
            var (repo, _) = Слои(scope);

            // ⚠ Второй сброс отвечает `false`, а не бросает: «сбросить то, чего нет» — не
            // ошибка вызывающего, а нормальный исход. Эндпоинту (16.7) это нужно, чтобы не
            // писать в журнал правку, которой не было.
            (await repo.ResetSystemAsync(ключ, TestContext.Current.CancellationToken)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Строка_с_неизвестным_ключом_законна_в_базе()
    {
        // ⚠ Внешнего ключа на реестр нет и быть не может: реестр живёт в коде. Отсюда
        // требование к хранилищу — не фильтровать по справочнику: увидеть, что в базе лежит на
        // самом деле, иначе будет нечем.
        var ключ = Ключ("nobody-declared-this");

        await using (var scope = база.Scope())
        {
            var (repo, db) = Слои(scope);
            await repo.SetSystemAsync(ключ, "1", null, TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = база.Scope())
        {
            var (repo, _) = Слои(scope);
            (await repo.SystemAsync(TestContext.Current.CancellationToken))
                .ContainsKey(ключ).ShouldBeTrue();
        }
    }

    /// <summary>Свой ключ на тест: база живёт весь прогон коллекции.</summary>
    private static string Ключ(string имя) => $"test.{имя}.{Guid.NewGuid():N}";

    private static (SettingsRepository, SenderDbContext) Слои(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<SettingsRepository>(),
         scope.ServiceProvider.GetRequiredService<SenderDbContext>());

    private static async Task<DateTimeOffset> Время(AsyncServiceScope scope, string ключ)
    {
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var строка = await db.Set<SystemSetting>()
            .AsNoTracking()
            .FirstAsync(запись => запись.Key == ключ, TestContext.Current.CancellationToken);

        return строка.UpdatedAt;
    }
}
