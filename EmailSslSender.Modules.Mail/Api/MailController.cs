using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Mail.Contracts;
using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Mail.Api;

/// <summary>Состояние почты для экрана настроек.</summary>
/// <remarks>
/// ⚠ <b>Пароля здесь нет НИ В КАКОМ виде</b> — только признак «задан». Хост, порт и отправитель
/// приезжают обычным списком настроек: они и есть настройки, и дублировать их здесь значило бы
/// завести второе место, где они показываются, и второе, где они разойдутся.
/// </remarks>
internal sealed record MailStatusOut(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("password_set")] bool PasswordSet,
    [property: JsonPropertyName("pending")] int Pending,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("last_error")] string? LastError);

/// <summary>Новый пароль SMTP. Пустая строка снимает его.</summary>
internal sealed record MailPasswordIn(
    [property: JsonPropertyName("password")] string? Password);

/// <summary>Кому послать проверочное письмо.</summary>
internal sealed record MailTestIn(
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("locale")] string? Locale);

/// <summary>
/// Почта установки: состояние, пароль, проверка.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Пароль SMTP требует свежего второго фактора</b> (<c>[RequireRecentSecondFactor]</c>,
/// решение владельца 2026-10-20). Тот, кто меняет SMTP, направляет письма сброса и коды входа на
/// свой релей. Адрес сервера и прочие <c>mail.*</c> — настройки защиты, и их правку закрывает тем
/// же кодом служба настроек.
///
/// ⚠ Проверочное письмо устанавливает исходящее соединение на хост и порт из настроек и
/// показывает ответ — то есть является <b>зондом наружу из-под сервера</b>. Доступно оно, как и
/// всё остальное, только вошедшему; свежего кода не спрашивает: письмо уходит на тот сервер,
/// который уже настроен, и пароль из ответа не узнать.
///
/// Прав в установке нет: все учётные записи — администраторы.
/// </remarks>
[ApiController]
[Route("api/mail")]
internal sealed class MailController(
    SenderDbContext db,
    SystemSecrets secrets,
    IMailSender sender,
    MailTemplates templates,
    ISettingsReader settings,
    IAuditWriter audit) : ControllerBase
{
    /// <summary>Состояние почты: настроена ли, задан ли пароль, что в очереди.</summary>
    [HttpGet]
    public async Task<MailStatusOut> Status(CancellationToken cancellationToken)
    {
        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        // ⚠ Письмо со сроком «никогда» не ждёт, а умерло: отменённое, стёртое по сроку или
        // исчерпавшее попытки — его попытки при этом могут быть и не исчерпаны.
        var ждут = await db.Set<MailOutbox>()
            .CountAsync(
                п => п.SentAt == null && п.Attempts < политика.MaxAttempts && п.NextAttemptAt < MailAttachments.Никогда,
                cancellationToken);

        var сдались = await db.Set<MailOutbox>()
            .CountAsync(
                п => п.SentAt == null && (п.Attempts >= политика.MaxAttempts || п.NextAttemptAt >= MailAttachments.Никогда),
                cancellationToken);

        // ⚠ Последняя ошибка — самого свежего неотправленного. Именно она отвечает на вопрос
        // «почему не уходит», а не первая по времени: настройки могли починить, и старая
        // ошибка увела бы разбор в прошлое.
        var последняя = await db.Set<MailOutbox>()
            .Where(п => п.SentAt == null && п.LastError != null)
            .OrderByDescending(п => п.CreatedAt)
            .Select(п => п.LastError)
            .FirstOrDefaultAsync(cancellationToken);

        return new MailStatusOut(
            политика.Configured,
            await secrets.HasAsync(SystemSecretNames.SmtpPassword, cancellationToken),
            ждут,
            сдались,
            последняя);
    }

    /// <summary>
    /// Задать или снять пароль SMTP.
    /// </summary>
    /// <remarks>
    /// ⚠ <b><c>PUT</c>, а не <c>PATCH</c>:</b> частичного пароля не бывает.
    ///
    /// ⚠ <b>Пустая строка СНИМАЕТ пароль</b>, и это законное состояние: реле в той же сети
    /// принимает почту без авторизации. Отказ на пустой строке запретил бы такую установку.
    ///
    /// ⚠ <b>В журнал идёт факт, а не значение</b> — ни старое, ни новое. Журнал append-only:
    /// строку оттуда нельзя ни изменить, ни удалить, и ошибка здесь необратима.
    ///
    /// ⚠ ⚠ <b>Пароль привязывается к серверу, порту, защите и учётной записи, заданным на момент
    /// ввода</b>: сначала настройки, потом пароль. Сменили любую из них — пароль вводится заново,
    /// иначе отправка отказывает с понятным текстом.
    /// </remarks>
    [HttpPut("password")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> SetPassword(
        [FromBody] MailPasswordIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var цель = SmtpSender.Цель(await MailPolicy.ReadAsync(settings, cancellationToken));

        await db.InTransactionAsync(
            async token =>
            {
                var задан = !string.IsNullOrEmpty(body.Password);

                if (задан)
                {
                    await secrets.PutAsync(SystemSecretNames.SmtpPassword, body.Password!, цель, token);
                }
                else
                {
                    await secrets.DropAsync(SystemSecretNames.SmtpPassword, token);
                }

                audit.Write(new AuditEntry(
                    MailActions.PasswordSet,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["set"] = задан,
                    },
                    LogChanges: true));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Послать проверочное письмо — сразу, минуя очередь.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Минуя очередь намеренно.</b> Смысл проверки в том, чтобы администратор увидел ответ
    /// чужого сервера <b>сейчас</b>; письмо, положенное в очередь, ушло бы через пятнадцать
    /// секунд и молча, а ошибка досталась бы логу.
    ///
    /// ⚠ ⚠ <b>Отказ — <c>409</c>, а не <c>502</c>, и причина в жизни, а не в семантике.</b>
    /// Семантически верен <c>502</c>: отказал чужой сервер, наш работает. Но <b>Cloudflare
    /// подменяет тело любого <c>5xx</c> от origin своей страницей об ошибке</b>, и конверт с
    /// <c>details.reason</c> до браузера не доезжает — администратор видит общее «письмо не
    /// отправлено» вместо <c>5.7.1 Authentication Required</c>, то есть ровно то, чего эта
    /// ручка обещает не делать. Найдено живой проверкой на стенде 2026-08-18.
    /// </remarks>
    [HttpPost("test")]
    public async Task<IActionResult> SendTest(
        [FromBody] MailTestIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var кому = body.To;

        if (string.IsNullOrWhiteSpace(кому))
        {
            throw new AppException(
                ErrorCodes.Validation, "Не указано, на какой адрес слать проверочное письмо.");
        }

        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        if (!политика.Configured)
        {
            throw new AppException(
                ErrorCodes.MailNotConfigured,
                "Почта не настроена: включите отправку и задайте адрес сервера.",
                HttpStatusCode.Conflict);
        }

        var (тема, тело) = templates.Render(
            MailKinds.Test,
            body.Locale ?? MailLocales.En,
            new Dictionary<string, string>(StringComparer.Ordinal));

        try
        {
            // ⚠ Одно письмо — одна сессия: проверочное письмо шлётся руками из админки, и
            // держать соединение между нажатиями было бы соединением без хозяина.
            await using var сессия = sender.Open(политика);

            await сессия.SendAsync(new OutgoingMail(кому, тема, тело), cancellationToken);
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            // ⚠ Текст чужого сервера уезжает администратору целиком — ради этого ручка и
            // существует: «5.7.1 Authentication Required» говорит, что делать, а «не удалось
            // отправить» не говорит ничего.
            //
            // ⚠ ⚠ Но ТОЛЬКО текст SMTP, TLS и сети (`ТекстНаружу`). Внутри той же попытки
            // открывается пароль из базы: отказ Npgsql, криптографии или чего угодно ещё уехал
            // бы наружу своим сообщением — а там бывают строка подключения и устройство
            // внутренней сети. Всё прочее — цепочкой типов (`SafeError`). Пароль SMTP в текст
            // SMTP не попадает: MailKit передаёт его в `AuthenticateAsync` отдельно. Отдельной
            // строки в лог/журнал здесь нет — диагностика это сам ответ.
            throw new AppException(
                ErrorCodes.MailSendFailed,
                "Письмо не отправлено: сервер отказал.",
                HttpStatusCode.Conflict,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = ТекстНаружу(ошибка) ? ошибка.Message : SafeError.Describe(ошибка),
                });
        }

        await db.InTransactionAsync(
            async token =>
            {
                audit.Write(new AuditEntry(MailActions.TestSent));
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Можно ли отдать текст отказа администратору как есть.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Закрытый список, а не «всё, кроме опасного»</b>: ответ сервера SMTP, рукопожатие
    /// TLS, вход, сеть, разбор адреса получателя — и свои отказы модуля
    /// (<see cref="MailConfigurationException"/>: имя сервера, защита, привязка пароля),
    /// составленные без секретов. Новое исключение по умолчанию идёт цепочкой типов — молчать
    /// безопаснее, чем рассказывать.
    /// </remarks>
    internal static bool ТекстНаружу(Exception ошибка) => ошибка is MailConfigurationException
        or SmtpCommandException
        or SmtpProtocolException
        or AuthenticationException
        or SaslException
        or SslHandshakeException
        or SocketException
        or IOException
        or TimeoutException
        or MimeKit.ParseException;
}
