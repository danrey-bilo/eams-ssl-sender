using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Mail.Tests;

// ⚠ Оба класса — КОПИИ из `EmailSslSender.Modules.Notifications.Tests`, и это осознанно. Общий
// тестовый проект на все модули означал бы одну сборку с доступом к нутру всех сразу
// (правило 1); а тесты — как раз то место, где срезают углы. Копия в тридцать строк
// дешевле дыры в границах модулей.
//
// ⚠ Синхронные пути в счётчике обязательны: без них `SaveChanges()` проходит мимо, и
// проверка «одна область — один SELECT» зеленеет на той редакции, которую должна ловить.
// Найдено мутацией у соседа 2026-08-28.

/// <summary>
/// Считает команды, ушедшие в базу.
/// </summary>
/// <remarks>
/// ⚠ Им проверяется <b>цена</b> вызова, а не намерение: «пачка уехала одной вставкой» нельзя
/// доказать чтением кода — EF сам решает, как её резать.
/// </remarks>
public sealed class СчётчикКоманд : DbCommandInterceptor
{
    private int _счёт;

    /// <summary>Сколько команд ушло с последнего сброса.</summary>
    public int Счёт => Volatile.Read(ref _счёт);

    /// <summary>Обнулить перед проверкой.</summary>
    public void Сбросить() => Interlocked.Exchange(ref _счёт, 0);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _счёт);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _счёт);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    // ⚠ Синхронные пути считаются ТОЖЕ. Без них `SaveChanges()` в цикле проходит мимо
    // счётчика, и проверка «пачка не развернулась» зеленеет на той самой редакции, которую
    // должна ловить. Найдено мутацией 2026-08-28.
    public override InterceptionResult<int> NonQueryExecuting(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Interlocked.Increment(ref _счёт);

        return base.NonQueryExecuting(command, eventData, result);
    }

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Interlocked.Increment(ref _счёт);

        return base.ReaderExecuting(command, eventData, result);
    }
}

/// <summary>Журнал, запоминающий строки.</summary>
public sealed class ЗаписнойЖурнал : ILoggerProvider
{
    /// <summary>Всё, что уехало в лог.</summary>
    public List<string> Строки { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Перо(Строки);

    public void Dispose()
    {
    }

    private sealed class Перо(List<string> строки) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (строки)
            {
                строки.Add(formatter(state, exception));
            }
        }
    }
}
