using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Строка, которую можно писать в лог: цепочка типов, а не текстов.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>До этапа 23.5 у SafeError было 49 вызовов в одиннадцати проектах — и ноль
/// тестов.</b> Класс, стоящий на пути «строка подключения с паролем не попадает в лог»
/// (правило 14), держался чтением кода. Главная проверка здесь — четвёртая: ни одного
/// слова из сообщения исключения в выводе нет.
/// </remarks>
public sealed class SafeErrorTests
{
    [Fact]
    public void Пустое_описывается_словом_а_не_падает()
    {
        SafeError.Describe(null).ShouldBe("нет");
    }

    [Fact]
    public void Цепочка_идёт_от_внешнего_к_внутреннему_типами()
    {
        var error = new InvalidOperationException(
            "внешнее", new TimeoutException("среднее", new ArgumentException("глубинное")));

        SafeError.Describe(error).ShouldBe(
            "InvalidOperationException → TimeoutException → ArgumentException");
    }

    [Fact]
    public void Глубина_ограничена_четырьмя_звеньями()
    {
        // Пять уровней вложенности — в выводе четыре: глубже уже не диагностика, а шум.
        var error = new Exception(
            "1", new Exception("2", new Exception("3", new Exception("4", new Exception("5")))));

        SafeError.Describe(error).Split(" → ").Length.ShouldBe(4);
    }

    [Fact]
    public void Ни_одно_слово_сообщения_не_попадает_в_вывод()
    {
        // ⚠ ГЛАВНЫЙ ТЕСТ ФАЙЛА. Ровно тот сценарий, ради которого класс существует: текст
        // исключения подключения несёт строку подключения целиком, вместе с паролем.
        const string Секрет = "Host=db;Username=eams;Password=recognisable-QQQ-42";

        var error = new InvalidOperationException(
            $"Не удалось подключиться: {Секрет}",
            new ArgumentException(Секрет));

        var описание = SafeError.Describe(error);

        описание.ShouldNotContain("recognisable-QQQ-42");
        описание.ShouldNotContain("Password");
        описание.ShouldNotContain("Host=");
        описание.ShouldBe("InvalidOperationException → ArgumentException");
    }
}
