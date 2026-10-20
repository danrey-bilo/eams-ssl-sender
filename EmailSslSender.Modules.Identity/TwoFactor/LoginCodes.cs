using System.Globalization;
using System.Security.Cryptography;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Mail;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Одноразовый код входа, уходящий письмом.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Прежний код гасится при выдаче нового.</b> Иначе два письма подряд дают два живых
/// кода: перебор становится вдвое дешевле, а человек, нажавший «выслать ещё раз», введёт тот,
/// что пришёл позже, — и попадёт в отказ, потому что живым оказался бы и первый.
///
/// ⚠ <b>Код — шесть цифр, как у аутентификатора, и это не подражание.</b> Человек вводит его в
/// то же поле того же экрана; разная длина у двух способов означала бы разный экран и разную
/// проверку на клиенте.
///
/// ⚠ <b>Хранится хешем, как все одноразовые ссылки</b> (<c>identity_tokens</c>): дамп базы не
/// должен отдавать действующие коды входа.
/// </remarks>
internal sealed class LoginCodes(
    Invitations invitations,
    IUserLocale locale,
    IMailQueue queue,
    ISettingsReader settings)
{
    /// <summary>Сколько цифр в коде.</summary>
    private const int Цифр = 6;

    /// <summary>
    /// Завести код и послать его письмом.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Не бросает, если письмо не ушло.</b> Ответ человеку обязан быть тем же самым:
    /// сказать «слишком часто» значило бы подтвердить существование учётной записи тому, кто
    /// это и выясняет.
    /// </remarks>
    public async Task<MailQueueResult> IssueAsync(User user, CancellationToken cancellationToken = default,
        string? binding = null, string purpose = Purposes.LoginCode)
    {
        ArgumentNullException.ThrowIfNull(user);

        // ⚠ Прежние коды человека выдача УДАЛЯЕТ, а не гасит — разбор у Invitations.IssueCodeAsync.
        var код = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        var минут = Math.Max(
            1, await settings.GetAsync(IdentitySettings.TwoFactorChallengeMinutes, cancellationToken));

        var язык = await locale.ForAsync(user.Id, cancellationToken);

        var result = await queue.EnqueueAsync(
            user.Email,
            IdentityMailKinds.LoginCode,
            язык,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = код,
                ["minutes"] = минут.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken);

        // Отказ почты сохраняет прежний код: повторная отправка не должна уничтожать уже доставленное письмо.
        if (result == MailQueueResult.Queued)
        {
            await invitations.IssueCodeAsync(
                user.Id, purpose, TimeSpan.FromMinutes(минут), код, cancellationToken, binding);
        }

        return result;
    }

    /// <summary>Подошёл ли код. Гасит его в любом случае — как расписку.</summary>
    public async Task<bool> VerifyAsync(
        Guid userId, string code, CancellationToken cancellationToken = default,
        string? binding = null, string purpose = Purposes.LoginCode)
    {
        // ⚠ ⚠ Поиск по паре «человек, хеш кода» (ревью 2026-09-15, S2, logincode-1). Прежний
        // глобальный поиск держался на уникальном индексе — и именно этот индекс ронял выдачу:
        // он стоит и на погашенных строках, живущих до уборки месяц, и совпадение нового кода с
        // любой из них давало пятисотку вместо письма. Ревью 2026-09-06 видело половину беды
        // (совпадение живых кодов) и было отклонено верно для неё; мёртвые строки тогда не
        // посчитали. Разбор — у Invitations.IssueCodeAsync.
        var чей = await invitations.ConsumeCodeAsync(userId, code, purpose, cancellationToken, binding);

        return чей == userId;
    }

    /// <summary>Длина кода — им проверяется, что поле на экране одно на оба способа.</summary>
    public static int Длина => Цифр;
}
