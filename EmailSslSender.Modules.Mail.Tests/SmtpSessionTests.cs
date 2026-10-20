using System.Net;
using System.Net.Sockets;
using System.Text;
using EmailSslSender.Modules.Mail.Api;
using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Shared.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Сессия SMTP: отказ во входе не оставляет соединения без входа, пароль не уходит ни открытым
/// текстом, ни на сервер, для которого его не вводили.
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
public sealed class SmtpSessionTests(MailDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>()
            .Set<SystemSecret>().ExecuteDeleteAsync(CancellationToken.None);
    }

    /// <summary>
    /// Любой отказ на пути входа рвёт соединение, и следующее письмо порции входит заново.
    /// </summary>
    /// <remarks>
    /// ⚠ Отказ здесь — наш, а не сервера: вход по паролю без защиты соединения запрещён, а
    /// заглушка говорит без TLS. Свойство проверяется то же — «отказ на шаге входа не оставляет
    /// живого соединения без входа»: мутация «убрать разрыв» по-прежнему красит этот факт, потому
    /// что отказ стоит после соединения.
    /// </remarks>
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
            await Should.ThrowAsync<MailConfigurationException>(() => сессия.SendAsync(письмо, Ct));

            // Второе письмо порции обязано подключиться и войти заново — и получить ту же
            // настоящую причину, а не «530» от команды, отправленной без входа.
            await Should.ThrowAsync<MailConfigurationException>(() => сессия.SendAsync(письмо, Ct));
        }

        сервер.Подключений.ShouldBe(2, "второе письмо пошло по соединению, где вход не состоялся");
        сервер.КомандБезВхода.ShouldBe(0, "письмо отправлялось без входа");
    }

    [Fact]
    public async Task Вход_по_паролю_без_защиты_соединения_отвергается()
    {
        // ⚠ Пароль задан ровно для этого сервера и этой защиты — привязка пропускает, и отказывает
        // именно запрет входа открытым текстом.
        await using var сервер = ЗаглушкаSmtp.Запустить();
        var политика = new MailPolicy(
            true, "127.0.0.1", сервер.Порт, "none", "robot", "EAMS", "vault@example.com", 100, 5);

        await ЗадатьПароль(политика, "пароль-ящика");

        await using var scope = база.Scope();
        var отправитель = new SmtpSender(scope.ServiceProvider.GetRequiredService<SystemSecrets>());

        await using (var сессия = отправитель.Open(политика))
        {
            var ошибка = await Should.ThrowAsync<MailConfigurationException>(
                () => сессия.SendAsync(new OutgoingMail("a@example.com", "тема", "тело"), Ct));

            ошибка.Message.ShouldContain("открытым");
            ошибка.Message.ShouldNotContain("пароль-ящика");
        }

        сервер.Входов.ShouldBe(0, "AUTH без защиты соединения не отправляется вовсе");
        сервер.КомандБезВхода.ShouldBe(0);
    }

    [Fact]
    public async Task Пароль_для_другого_сервера_не_уходит_и_соединения_нет()
    {
        // ⚠ Сценарий перехваченной сессии: пароль вводили для настоящего сервера, а сервер в
        // настройках переставили. Отказ — до соединения: новый сервер не видит от нас ничего.
        await using var сервер = ЗаглушкаSmtp.Запустить();
        var настоящая = new MailPolicy(
            true, "smtp.example.com", 587, "starttls", "robot", "EAMS", "vault@example.com", 100, 5);

        await ЗадатьПароль(настоящая, "пароль-ящика");

        await using var scope = база.Scope();
        var отправитель = new SmtpSender(scope.ServiceProvider.GetRequiredService<SystemSecrets>());
        var подменённая = настоящая with { Host = "127.0.0.1", Port = сервер.Порт };

        await using (var сессия = отправитель.Open(подменённая))
        {
            var ошибка = await Should.ThrowAsync<MailConfigurationException>(
                () => сессия.SendAsync(new OutgoingMail("a@example.com", "тема", "тело"), Ct));

            ошибка.Message.ShouldContain("заново");
        }

        сервер.Подключений.ShouldBe(0, "до подменённого сервера дело не дошло");
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
    [InlineData("https://mail.example.com", "mail.example.com")]
    [InlineData(" smtp://mail.example.com/ ", "mail.example.com")]
    [InlineData("mail.example.com", "mail.example.com")]
    [InlineData("mail.example.com:465", "mail.example.com:465")]
    public void Схема_перед_именем_сервера_срезается(string хост, string имя)
    {
        SmtpSender.ИмяСервера(хост).ShouldBe(имя);
    }

    [Theory]
    [InlineData("mail.example.com:465")]
    [InlineData("https://mail.example.com:465")]
    [InlineData("mail.example.com/smtp")]
    public async Task Адрес_вместо_имени_сервера_отказ_с_понятным_текстом(string хост)
    {
        await using var scope = база.Scope();

        var отправитель = new SmtpSender(scope.ServiceProvider.GetRequiredService<SystemSecrets>());
        var политика = new MailPolicy(true, хост, 465, "tls", null, "EAMS", "vault@example.com", 100, 5);

        await using var сессия = отправитель.Open(политика);

        var ошибка = await Should.ThrowAsync<MailConfigurationException>(
            () => сессия.SendAsync(new OutgoingMail("a@example.com", "тема", "тело"), Ct));

        ошибка.Message.ShouldContain("только имя");
    }

    [Fact]
    public void Наружу_уходит_только_текст_SMTP_TLS_сети_и_своих_отказов()
    {
        MailController.ТекстНаружу(new SmtpCommandException(
                SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxUnavailable, "5.7.1 relay denied"))
            .ShouldBeTrue();
        MailController.ТекстНаружу(new SmtpProtocolException("протокол")).ShouldBeTrue();
        MailController.ТекстНаружу(new AuthenticationException("535")).ShouldBeTrue();
        MailController.ТекстНаружу(new SslHandshakeException("tls")).ShouldBeTrue();
        MailController.ТекстНаружу(new SocketException((int)SocketError.ConnectionRefused)).ShouldBeTrue();
        MailController.ТекстНаружу(new IOException("обрыв")).ShouldBeTrue();
        MailController.ТекстНаружу(new MailConfigurationException("только имя")).ShouldBeTrue();

        // ⚠ Отказ базы, криптографии или чужой общий тип — только цепочкой типов: в тексте бывают
        // строка подключения и устройство внутренней сети.
        MailController.ТекстНаружу(new System.Security.Cryptography.CryptographicException("тег")).ShouldBeFalse();
        MailController.ТекстНаружу(new InvalidOperationException("Host=db;Password=секрет")).ShouldBeFalse();
        MailController.ТекстНаружу(new DbUpdateException("Host=db;Password=секрет")).ShouldBeFalse();
        MailController.ТекстНаружу(new ArgumentException("x")).ShouldBeFalse();
    }

    private async Task ЗадатьПароль(MailPolicy политика, string пароль)
    {
        await using var scope = база.Scope();

        await scope.ServiceProvider.GetRequiredService<SystemSecrets>().PutAsync(
            SystemSecretNames.SmtpPassword, пароль, SmtpSender.Цель(политика), Ct);
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>().SaveChangesAsync(Ct);
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
    private int _входов;

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

    /// <summary>Сколько раз клиент присылал AUTH.</summary>
    public int Входов => Volatile.Read(ref _входов);

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
                    Interlocked.Increment(ref _входов);

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
