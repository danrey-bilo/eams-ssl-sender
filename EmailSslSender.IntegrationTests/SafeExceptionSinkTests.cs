using EmailSslSender.Api.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Исключение доезжает до лога цепочкой типов и стеком, но не текстом (правило 14, решение 0095).
/// </summary>
/// <remarks>
/// ⚠ ⚠ Найдено аудитом надёжности 2026-10-19: EF Core, Npgsql и ASP.NET пишут свои ошибки сами и
/// с объектом исключения, а форматтер кладёт его текст в <c>@x</c>. Арх-тест
/// <c>LogExceptions</c> видит только свой код. Проверка — тем же форматтером, что в бою.
/// </remarks>
public sealed class SafeExceptionSinkTests
{
    // мутация 2026-10-19: приёмник передал событие как есть → покраснел этот факт.
    [Fact]
    public void Текст_исключения_не_доезжает_до_записи()
    {
        var записано = new Записи();

        using (var log = new LoggerConfiguration()
                   .MinimumLevel.Verbose()
                   .WriteTo.Sink(new SafeExceptionSink(записано))
                   .CreateLogger())
        {
            try
            {
                throw new InvalidOperationException(
                    "внешнее", new TimeoutException("Host=postgres;Password=совершенно-секретно"));
            }
            catch (InvalidOperationException error)
            {
                log.Error(error, "сбой {Где}", "чужой логгер");
            }
        }

        var строка = записано.Отрисовать();

        строка.ShouldNotContain("совершенно-секретно");
        строка.ShouldNotContain("внешнее");
        строка.ShouldContain(nameof(InvalidOperationException));
        строка.ShouldContain(nameof(TimeoutException));
        строка.ShouldContain(SafeExceptionSink.StackProperty);
        строка.ShouldContain("чужой логгер", Case.Sensitive, "свои поля события потеряны");
    }

    [Fact]
    public void Событие_без_исключения_проходит_как_есть()
    {
        var записано = new Записи();

        using (var log = new LoggerConfiguration().WriteTo.Sink(new SafeExceptionSink(записано)).CreateLogger())
        {
            log.Information("обычное {Что}", "сообщение");
        }

        записано.События.ShouldHaveSingleItem().Properties.ShouldNotContainKey(SafeExceptionSink.TypesProperty);
    }

    /// <summary>Приёмник, запоминающий события и рисующий их боевым форматтером.</summary>
    private sealed class Записи : ILogEventSink
    {
        public List<LogEvent> События { get; } = [];

        public void Emit(LogEvent logEvent) => События.Add(logEvent);

        public string Отрисовать()
        {
            using var текст = new StringWriter();
            var форматтер = new RenderedCompactJsonFormatter();

            foreach (var событие in События)
            {
                форматтер.Format(событие, текст);
            }

            return текст.ToString();
        }
    }
}
