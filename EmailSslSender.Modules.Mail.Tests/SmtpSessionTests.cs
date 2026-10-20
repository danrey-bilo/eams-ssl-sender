using System.Net;
using System.Net.Sockets;
using System.Text;
using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Сессия SMTP: отказ во входе не оставляет соединения без входа.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Ревью 2026-09-15 (mail-3). После отказа <c>AuthenticateAsync</c> MailKit соединения не
/// рвёт, а сессия переподключалась только при <c>!IsConnected</c>: второе и дальнейшие письма
/// порции шли по живому соединению БЕЗ входа и получали «530 Authentication required» — и этот
/// вторичный текст администратор видел в статусе почты вместо настоящего отказа.
///
/// ⚠ <b>Сервер здесь — заглушка на локальном сокете</b>, а не контейнер почтового сервера:
/// проверяется порядок команд одной сессии, и знать его заглушка обязана точно, а настоящий
/// сервер ведёт себя по-разному по настройке.
/// </remarks>
[Collection(MailDatabaseCollection.Name)]
public sealed class SmtpSessionTests(MailDatabase база)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Отказ_входа_не_оставляет_соединение_без_входа()
    {
        await using var сервер = ЗаглушкаSmtp.Запустить();
        await using var scope = база.Scope();

        var отправитель = new SmtpSender(scope.ServiceProvider.GetRequiredService<SystemSecrets>());
        var политика = new MailPolicy(
            Enabled: true,
            Host: "127.0.0.1",
            Port: сервер.Порт,
            Security: "none",
            Username: "robot",
            FromName: "EAMS",
            FromAddress: "vault@example.com",
            RatePerHour: 100,
            MaxAttempts: 5);

        var письмо = new OutgoingMail("кому@example.com", "тема", "тело");

        await using (var сессия = отправитель.Open(политика))
        {
            await Should.ThrowAsync<AuthenticationException>(() => сессия.SendAsync(письмо, Ct));

            // Второе письмо порции обязано подключиться и войти заново — и получить ту же
            // настоящую причину, а не «530» от команды, отправленной без входа.
            await Should.ThrowAsync<AuthenticationException>(() => сессия.SendAsync(письмо, Ct));
        }

        сервер.Подключений.ShouldBe(2, "второе письмо пошло по соединению, где вход не состоялся");
        сервер.КомандБезВхода.ShouldBe(0, "письмо отправлялось без входа");
    }

    /// <summary>
    /// Зеркало: реле без входа (пустое имя) — законная установка, и соединение одно на порцию.
    /// </summary>
    [Fact]
    public async Task Реле_без_входа_шлёт_порцию_одним_соединением()
    {
        await using var сервер = ЗаглушкаSmtp.Запустить(требоватьВход: false);
        await using var scope = база.Scope();

        var отправитель = new SmtpSender(scope.ServiceProvider.GetRequiredService<SystemSecrets>());
        var политика = new MailPolicy(
            true, "127.0.0.1", сервер.Порт, "none", null, "EAMS", "vault@example.com", 100, 5);

        await using (var сессия = отправитель.Open(политика))
        {
            await сессия.SendAsync(new OutgoingMail("a@example.com", "тема", "тело"), Ct);
            await сессия.SendAsync(new OutgoingMail("b@example.com", "тема", "тело"), Ct);
        }

        сервер.Подключений.ShouldBe(1);
        сервер.Принято.ShouldBe(2);
    }

    [Theory]
    [InlineData("https://mail.example.com")]
    [InlineData("mail.example.com:465")]
    [InlineData("mail.example.com/")]
    public async Task Адрес_вместо_имени_сервера_отказ_с_понятным_текстом(string хост)
    {
        await using var scope = база.Scope();

        var отправитель = new SmtpSender(scope.ServiceProvider.GetRequiredService<SystemSecrets>());
        var политика = new MailPolicy(true, хост, 465, "tls", null, "EAMS", "vault@example.com", 100, 5);

        await using var сессия = отправитель.Open(политика);

        var ошибка = await Should.ThrowAsync<InvalidOperationException>(
            () => сессия.SendAsync(new OutgoingMail("a@example.com", "тема", "тело"), Ct));

        ошибка.Message.ShouldContain("только имя");
    }
}

/// <summary>
/// Крошечный SMTP-сервер: вход всегда отказывает, письмо без входа — «530».
/// </summary>
internal sealed class ЗаглушкаSmtp : IAsyncDisposable
{
    private readonly TcpListener _слушатель;
    private readonly CancellationTokenSource _жизнь = new();
    private readonly Task _приём;
    private readonly bool _требоватьВход;
    private int _подключений;
    private int _безВхода;
    private int _принято;

    private ЗаглушкаSmtp(bool требоватьВход)
    {
        _требоватьВход = требоватьВход;
        _слушатель = new TcpListener(IPAddress.Loopback, 0);
        _слушатель.Start();
        _приём = ПриниматьAsync();
    }

    public static ЗаглушкаSmtp Запустить(bool требоватьВход = true) => new(требоватьВход);

    public int Порт => ((IPEndPoint)_слушатель.LocalEndpoint).Port;

    public int Подключений => Volatile.Read(ref _подключений);

    public int КомандБезВхода => Volatile.Read(ref _безВхода);

    public int Принято => Volatile.Read(ref _принято);

    private async Task ПриниматьAsync()
    {
        var соединения = new List<Task>();

        try
        {
            while (!_жизнь.IsCancellationRequested)
            {
                var клиент = await _слушатель.AcceptTcpClientAsync(_жизнь.Token);

                Interlocked.Increment(ref _подключений);
                соединения.Add(ОбслужитьAsync(клиент));
            }
        }
        catch (Exception ошибка) when (ошибка is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Заглушку остановили.
        }

        await Task.WhenAll(соединения);
    }

    private async Task ОбслужитьAsync(TcpClient клиент)
    {
        using var _ = клиент;

        try
        {
            var поток = клиент.GetStream();
            using var чтение = new StreamReader(поток, Encoding.ASCII);
            await using var запись = new StreamWriter(поток, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            await запись.WriteLineAsync("220 stub ESMTP");

            var данные = false;

            while (await чтение.ReadLineAsync(_жизнь.Token) is { } строка)
            {
                if (данные)
                {
                    if (строка == ".")
                    {
                        данные = false;
                        Interlocked.Increment(ref _принято);
                        await запись.WriteLineAsync("250 2.0.0 принято");
                    }

                    continue;
                }

                var команда = строка.ToUpperInvariant();

                if (команда.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    await запись.WriteLineAsync("250-stub");
                    await запись.WriteLineAsync(_требоватьВход ? "250 AUTH PLAIN" : "250 8BITMIME");
                }
                else if (команда.StartsWith("HELO", StringComparison.Ordinal)
                    || команда.StartsWith("RSET", StringComparison.Ordinal)
                    || команда.StartsWith("NOOP", StringComparison.Ordinal))
                {
                    await запись.WriteLineAsync("250 2.0.0 ok");
                }
                else if (команда.StartsWith("AUTH", StringComparison.Ordinal))
                {
                    // Начальный ответ может прийти в той же строке или отдельной; отказ — один.
                    if (команда.Trim() == "AUTH PLAIN")
                    {
                        await запись.WriteLineAsync("334 ");
                        await чтение.ReadLineAsync(_жизнь.Token);
                    }

                    await запись.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
                }
                else if (команда.StartsWith("MAIL FROM", StringComparison.Ordinal))
                {
                    if (_требоватьВход)
                    {
                        Interlocked.Increment(ref _безВхода);
                        await запись.WriteLineAsync("530 5.7.0 Authentication required");
                    }
                    else
                    {
                        await запись.WriteLineAsync("250 2.1.0 ok");
                    }
                }
                else if (команда.StartsWith("RCPT TO", StringComparison.Ordinal))
                {
                    await запись.WriteLineAsync(_требоватьВход ? "503 5.5.1 no sender" : "250 2.1.5 ok");
                }
                else if (команда.StartsWith("DATA", StringComparison.Ordinal))
                {
                    данные = true;
                    await запись.WriteLineAsync("354 go ahead");
                }
                else if (команда.StartsWith("QUIT", StringComparison.Ordinal))
                {
                    await запись.WriteLineAsync("221 2.0.0 bye");

                    return;
                }
                else
                {
                    await запись.WriteLineAsync("502 5.5.2 unknown");
                }
            }
        }
        catch (Exception ошибка) when (ошибка is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Клиент разорвал соединение — ровно то, что проверяется.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _жизнь.CancelAsync();
        _слушатель.Stop();
        await _приём;
        _жизнь.Dispose();
    }
}
