using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Shared.Data;

/// <summary>
/// Одна транзакция на действие — общая для всех модулей.
/// </summary>
/// <remarks>
/// <b>Почему здесь, а не по копии на модуль.</b> Копии одного и того же десятка строк
/// разъезжаются: поправить одну и забыть остальные — значит получить модуль, где половина
/// действий защищена, а половина нет, причём разницы не видно ни по одному тесту.
///
/// <b>Стратегия выполнения, а не голая транзакция.</b> Соединение с базой может оборваться,
/// и настроенная стратегия повторит делегат. Без неё повтор был бы запрещён самой EF: она
/// отказывается повторять то, что уже начало транзакцию, и падает с указанием обернуть
/// вызов ровно так.
///
/// ⚠ <b>Очистка трекера перед каждой попыткой обязательна.</b> Повтор выполняет делегат
/// заново, а добавленные первой попыткой сущности остаются отслеженными — вторая вставила бы
/// их <b>ещё раз</b>. Отсюда требование к телу: всё, что оно правит, оно и загружает —
/// внутри себя. Сущность, прочитанная до вызова, после очистки перестаёт отслеживаться, и
/// правки над ней уходят в никуда молча. Это уже случалось (активация учётной записи,
/// 2026-08-22) и было поймано тестом, а не чтением.
/// </remarks>
public static class Transactions
{
    /// <summary>Выполнить действие в транзакции, повторив при обрыве соединения.</summary>
    public static async Task<T> InTransactionAsync<T>(
        this SenderDbContext db,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(action);

        // ⚠ Транзакция вызывающего уважается, а не подменяется. Службы модулей зовут друг друга
        // из своих транзакций, вокруг своих записей. Открой мы здесь вторую, EF отказал бы; а
        // очистка трекера выбросила бы всё, что вызывающий успел набрать до вызова, — молча и
        // без единой ошибки. Найдено разбором готовности 2026-08-24.
        if (db.Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            cancellationToken,
            async (token) =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(token);

                var attempt = await action(token);

                await transaction.CommitAsync(token);

                return attempt;
            });
    }

    /// <summary>То же для действия без результата.</summary>
    public static async Task InTransactionAsync(
        this SenderDbContext db,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await db.InTransactionAsync<bool>(
            async (token) =>
            {
                await action(token);

                return true;
            },
            cancellationToken);
    }
}
