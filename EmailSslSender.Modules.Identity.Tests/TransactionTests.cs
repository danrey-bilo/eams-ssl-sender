using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Общая обвязка транзакций: очередь «после фиксации».
/// </summary>
/// <remarks>
/// ⚠ Проверяется <b>механизм</b>, а не его пользователи: в этой сборке в очередь «после
/// фиксации» пока не кладёт никто, она оставлена для собственных действий, которые нельзя
/// совершать до фиксации. Ошибка в механизме испортит любого будущего пользователя.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class TransactionTests(IdentityDatabase database)
{
    /// <summary>Отложенное выполняется — но только после того, как транзакция состоялась.</summary>
    [Fact]
    public async Task Отложенное_выполняется_после_фиксации()
    {
        var order = new List<string>();

        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        await db.InTransactionAsync(
            async (token, after) =>
            {
                after.Then(_ =>
                {
                    order.Add("после");

                    return Task.CompletedTask;
                });

                // Любое обращение к базе внутри транзакции: важен сам факт, что тело
                // отработало раньше отложенного.
                await db.Database.ExecuteSqlRawAsync("SELECT 1", token);
                order.Add("тело");

                return true;
            },
            TestContext.Current.CancellationToken);

        order.ShouldBe(["тело", "после"]);
    }

    /// <summary>
    /// Чужая транзакция уважается: второй не открывается, трекер не чистится.
    /// </summary>
    /// <remarks>
    /// ⚠ Ради этого случая проверка и написана. Контракт выдачи доступа зовёт сосед — из
    /// СВОЕЙ транзакции, вокруг своей записи. Открой обвязка вторую, EF отказал бы; а очистка
    /// трекера выбросила бы всё, что сосед успел набрать до вызова, — молча и без ошибки, то
    /// есть запись сохранилась бы без половины полей. Найдено разбором готовности 2026-08-24.
    /// </remarks>
    [Fact]
    public async Task Внутри_чужой_транзакции_вторая_не_открывается()
    {
        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        await db.InTransactionAsync(
            async (outer) =>
            {
                var снаружи = db.Database.CurrentTransaction;

                снаружи.ShouldNotBeNull();

                await db.InTransactionAsync(
                    async (inner) =>
                    {
                        // Та же самая транзакция, а не вторая.
                        db.Database.CurrentTransaction.ShouldBeSameAs(снаружи);

                        await db.Database.ExecuteSqlRawAsync("SELECT 1", inner);
                    },
                    outer);

                return true;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Внутри чужой транзакции откладывать работу нельзя — и об этом говорят вслух.
    /// </summary>
    /// <remarks>
    /// Фиксировать будет вызывающий, и когда — неизвестно. Выполнить отложенное сразу значило
    /// бы сделать его до фиксации, то есть ровно то, от чего очередь и защищает.
    /// </remarks>
    [Fact]
    public async Task Внутри_чужой_транзакции_очередь_запрещена()
    {
        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var сбой = await Should.ThrowAsync<InvalidOperationException>(() => db.InTransactionAsync(
            async (outer, _) =>
            {
                await db.InTransactionAsync(
                    (inner, after) =>
                    {
                        after.Then(_ => Task.CompletedTask);

                        return Task.FromResult(true);
                    },
                    outer);

                return true;
            },
            TestContext.Current.CancellationToken));

        сбой.Message.ShouldContain("фиксирует");
    }

    /// <summary>
    /// Транзакция не состоялась — отложенное не выполняется вовсе.
    /// </summary>
    /// <remarks>
    /// ⚠ Ради этого очередь и заведена. Сброс кэша прав внутри транзакции — это заявление о
    /// намерении: изменение ещё не видно никому, а метка уже сменилась. Откатись транзакция —
    /// и мы сбросили бы кэш по правам, которых никто не менял; хуже того, параллельный запрос
    /// успел бы перезаполнить его данными, которые вот-вот откатятся.
    /// </remarks>
    [Fact]
    public async Task Отложенное_не_выполняется_при_откате()
    {
        var done = false;

        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        // Тип делегата назван явно: тело только бросает, и вывести по нему `Task<T>`
        // компилятор не может — он выбрал бы перегрузку без очереди и молча проверил не то.
        Func<CancellationToken, AfterCommit, Task<bool>> body = async (token, after) =>
        {
            after.Then(_ =>
            {
                done = true;

                return Task.CompletedTask;
            });

            await db.Database.ExecuteSqlRawAsync("SELECT 1", token);

            throw new InvalidOperationException("действие не состоялось");
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => db.InTransactionAsync(body, TestContext.Current.CancellationToken));

        done.ShouldBeFalse("отложенное выполнилось, хотя транзакция откатилась");
    }
}
