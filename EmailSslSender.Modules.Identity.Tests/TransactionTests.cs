using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Общая обвязка транзакций: чужая транзакция уважается.
/// </summary>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class TransactionTests(IdentityDatabase database)
{
    /// <summary>
    /// Чужая транзакция уважается: второй не открывается, трекер не чистится.
    /// </summary>
    /// <remarks>
    /// ⚠ Службы модулей зовут друг друга из СВОИХ транзакций, вокруг своих записей. Открой
    /// обвязка вторую, EF отказал бы; а очистка трекера выбросила бы всё, что вызывающий успел
    /// набрать до вызова, — молча и без ошибки, то есть запись сохранилась бы без половины
    /// полей. Найдено разбором готовности 2026-08-24.
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
}
