using Modules.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace EmailSslSender.IntegrationTests;

/// <summary>Исключение доезжает до лога цепочкой типов и стеком, но не текстом.</summary>
/// <remarks>⚠ Чужие логгеры (EF Core, Npgsql, ASP.NET) пишут исключение сами — проверка боевым форматтером.</remarks>
public sealed class SafeExceptionSinkTests
{
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

    /// <summary>Приёмник с боевым форматтером.</summary>
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
