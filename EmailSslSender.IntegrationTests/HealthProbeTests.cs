using System.Net;
using System.Net.Sockets;
using EmailSslSender.Api.Infrastructure;

namespace EmailSslSender.IntegrationTests;

/// <summary>Проба здоровья контейнера (<c>dotnet EmailSslSender.Api.dll health</c>): умеет сказать «нет».</summary>
/// <remarks>⚠ Порт — из <c>ASPNETCORE_HTTP_PORTS</c>: базовый образ ставит 8080, проект — 8001.</remarks>
public sealed class HealthProbeTests
{
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

    [Fact]
    public async Task Проба_идёт_в_порт_из_окружения()
    {
        var прежний = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var слушатель = new TcpListener(IPAddress.Loopback, 0);

        слушатель.Start();

        var порт = ((IPEndPoint)слушатель.LocalEndpoint).Port;

        // Одно соединение, ответ 200: проверяется адресация пробы, а не приложение.
        var токен = TestContext.Current.CancellationToken;
        var ответчик = Task.Run(
            async () =>
            {
                using var соединение = await слушатель.AcceptTcpClientAsync(токен);
                using var поток = соединение.GetStream();

                // ⚠ Запрос вычитывается: непрочитанные данные при закрытии Windows превращает в RST.
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

    /// <summary>Процесс жив, но не готов (не 2xx) — отказ.</summary>
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

                // ⚠ Запрос вычитывается — иначе RST вместо ответа.
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

    /// <summary>Порт, на котором никто не слушает.</summary>
    private static int СвободныйПорт()
    {
        var слушатель = new TcpListener(IPAddress.Loopback, 0);

        слушатель.Start();

        var порт = ((IPEndPoint)слушатель.LocalEndpoint).Port;

        слушатель.Stop();

        return порт;
    }
}
