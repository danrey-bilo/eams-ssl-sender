using System.Net;
using System.Net.Sockets;
using EmailSslSender.Api.Infrastructure;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Проба здоровья контейнера (<c>dotnet EmailSslSender.Api.dll health</c>, этап 25.3).
/// </summary>
/// <remarks>
/// ⚠ <b>Зачем тест на три строки кода.</b> Проба — единственное, по чему снаружи судят, жива
/// служба или нет: <c>docker compose ps</c> показывает её вердикт, а прокси по нему решает,
/// пускать ли трафик (<c>depends_on: condition: service_healthy</c>). Проба, которая всегда
/// отвечает «здоров», хуже отсутствующей — она превращает поломку в «всё хорошо». Поэтому
/// проверяется именно способность сказать «НЕТ».
///
/// ⚠ <b>Порт берётся из <c>ASPNETCORE_HTTP_PORTS</c>, и это не украшение.</b> Базовый образ
/// <c>aspnet</c> ставит туда <b>8080</b>, а этот проект — 8001 (правило проекта). Проба,
/// зашившая 8001 в код, молчаливо стучалась бы не в тот порт на любом образе с другим
/// умолчанием — найдено живым прогоном 25.3, где первая попытка ушла на 8080.
/// </remarks>
public sealed class HealthProbeTests
{
    /// <summary>Никто не слушает — проба обязана вернуть отказ, а не ноль.</summary>
    [Fact]
    public async Task Проба_отвечает_отказом_когда_никто_не_слушает()
    {
        var прежний = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");

        try
        {
            Environment.SetEnvironmentVariable(
                "ASPNETCORE_HTTP_PORTS", СвободныйПорт().ToString(System.Globalization.CultureInfo.InvariantCulture));

            var код = await Bootstrap.ProbeAsync();

            код.ShouldBe(1, "процесс не слушает — проба обязана сказать «нездоров»");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", прежний);
        }
    }

    /// <summary>
    /// Проба читает порт из окружения, а не из зашитой константы.
    /// </summary>
    /// <remarks>
    /// Проверяется тем же способом, которым это ломается: слушаем на СВОЁМ порту и отвечаем
    /// <c>200</c>. Зашей проба 8001 — она ушла бы мимо и вернула отказ.
    /// </remarks>
    [Fact]
    public async Task Проба_идёт_в_порт_из_окружения()
    {
        var прежний = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var слушатель = new TcpListener(IPAddress.Loopback, 0);

        слушатель.Start();

        var порт = ((IPEndPoint)слушатель.LocalEndpoint).Port;

        // Простейший ответчик: принять одно соединение и ответить 200. Полноценный хост тут
        // не нужен — проверяется адресация пробы, а не готовность приложения.
        var токен = TestContext.Current.CancellationToken;
        var ответчик = Task.Run(
            async () =>
            {
                using var соединение = await слушатель.AcceptTcpClientAsync(токен);
                using var поток = соединение.GetStream();

                // ⚠ Запрос ВЫЧИТЫВАЕТСЯ, и это не формальность (поймано прогоном 25.3):
                // закрытие сокета с непрочитанными входящими данными Windows превращает в RST,
                // и клиент видит «An error occurred while sending the request» вместо ответа.
                // Значение чтения используется утверждением — иначе оно и правда неточное.
                var прочитано = await поток.ReadAsync(new byte[1024], токен);

                прочитано.ShouldBeGreaterThan(0, "проба обязана прислать запрос");

                await поток.WriteAsync(
                    System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"),
                    токен);
                await поток.FlushAsync(токен);
            },
            токен);

        try
        {
            Environment.SetEnvironmentVariable(
                "ASPNETCORE_HTTP_PORTS", порт.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var код = await Bootstrap.ProbeAsync();

            код.ShouldBe(0, "на этом порту ответили 200 — проба обязана согласиться");

            await ответчик;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", прежний);
            слушатель.Stop();
        }
    }

    /// <summary>Ответ не из семейства 2xx — отказ.</summary>
    /// <remarks>
    /// Отдельно от «никто не слушает»: это состояние «процесс жив, но НЕ готов» — недоступна
    /// база, потерян замок единственного экземпляра. Именно его проба обязана отличать от
    /// здоровья, иначе <c>/api/ready</c> можно было бы не звать вовсе.
    /// </remarks>
    [Fact]
    public async Task Проба_отвечает_отказом_на_неготовность()
    {
        var прежний = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var слушатель = new TcpListener(IPAddress.Loopback, 0);

        слушатель.Start();

        var порт = ((IPEndPoint)слушатель.LocalEndpoint).Port;

        var токен = TestContext.Current.CancellationToken;
        var ответчик = Task.Run(
            async () =>
            {
                using var соединение = await слушатель.AcceptTcpClientAsync(токен);
                using var поток = соединение.GetStream();

                // ⚠ Запрос ВЫЧИТЫВАЕТСЯ, и это не формальность (поймано прогоном 25.3):
                // закрытие сокета с непрочитанными входящими данными Windows превращает в RST,
                // и клиент видит «An error occurred while sending the request» вместо ответа.
                // Значение чтения используется утверждением — иначе оно и правда неточное.
                var прочитано = await поток.ReadAsync(new byte[1024], токен);

                прочитано.ShouldBeGreaterThan(0, "проба обязана прислать запрос");

                await поток.WriteAsync(
                    System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 11\r\n"
                        + "Connection: close\r\n\r\nnot ready!!"),
                    токен);
                await поток.FlushAsync(токен);
            },
            токен);

        try
        {
            Environment.SetEnvironmentVariable(
                "ASPNETCORE_HTTP_PORTS", порт.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var код = await Bootstrap.ProbeAsync();

            код.ShouldBe(1, "503 — это «не готов», и проба обязана его различать");

            await ответчик;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", прежний);
            слушатель.Stop();
        }
    }

    /// <summary>Свободный порт, на котором заведомо никто не слушает.</summary>
    private static int СвободныйПорт()
    {
        var слушатель = new TcpListener(IPAddress.Loopback, 0);

        слушатель.Start();

        var порт = ((IPEndPoint)слушатель.LocalEndpoint).Port;

        слушатель.Stop();

        return порт;
    }
}
