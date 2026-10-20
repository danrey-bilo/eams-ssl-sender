using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Mail.Secrets;

/// <summary>
/// Имена секретов установки. Набор ЗАКРЫТ.
/// </summary>
/// <remarks>
/// ⚠ <b>Закрыт в коде, а не в схеме, и это осознанно.</b> В базе колонка — обычный
/// <c>text</c>; ограничение живёт там, где его читают. Таблица без закрытого набора
/// превращается в свалку, куда следующий модуль положит что попало, — и разбираться, что там
/// лежит и чьё оно, будет некому.
/// </remarks>
internal static class SystemSecretNames
{
    /// <summary>Пароль к SMTP-серверу.</summary>
    public const string SmtpPassword = "smtp_password";

    /// <summary>Все законные имена.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { SmtpPassword };
}

/// <summary>
/// Секреты установки: запечатать, открыть, убрать.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Испорченный блоб даёт ОТКАЗ, а не «секрета нет».</b> Соблазн обернуть открытие в
/// <c>catch</c> и вернуть <c>null</c> велик — «ну не открылось, значит не настроено». Цена
/// названа образцом дословно: тогда «шлём без авторизации» выглядит как неверный пароль у
/// провайдера, и искать причину будут не там. Испорченный секрет — это авария хранилища или
/// подмена; и то и другое обязано быть громким.
///
/// ⚠ <b>Контекст AEAD включает имя секрета.</b> Без него годная запечатка переставляется с
/// одного секрета на другой: пароль SMTP, положенный на место будущего ключа подписи, открылся
/// бы как он.
/// </remarks>
internal sealed class SystemSecrets(
    SenderDbContext db, ISecretEnvelope envelope, ICurrentUser current, TimeProvider clock)
{
    /// <summary>Приставка контекста AEAD. Полный контекст — приставка плюс имя секрета.</summary>
    /// <remarks>
    /// мутация 2026-08-29: убрал имя секрета из контекста → покраснел
    /// SecretsTests.Контекст_AEAD_включает_имя_секрета (правило 9).
    /// </remarks>
    private const string Контекст = "sender:system-secret:";

    /// <summary>Открыть секрет или <c>null</c>, если он не задан.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Секрет есть, но не открывается: ключ не тот, контекст не тот или данные подменены.
    /// </exception>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Проверить(key);

        var строка = await db.Set<SystemSecret>()
            .AsNoTracking()
            .FirstOrDefaultAsync(с => с.Key == key, cancellationToken);

        // Строки нет — секрет не задан. Это законное состояние, а не ошибка.
        //
        // мутация 2026-08-29: обернул открытие в `catch` с `return null` → покраснел
        // SecretsTests.Испорченный_блоб_даёт_ОТКАЗ_а_не_молчание (правило 9).
        return строка is null
            ? null
            : envelope.OpenText(new WrappedDataKey(строка.DataKey), new SealedSecret(строка.Sealed), Контекст + key);
    }

    /// <summary>Задать секрет. Повторная запись перекладывает значение, а не плодит строки.</summary>
    /// <remarks>
    /// ⚠ Не зовёт <c>SaveChangesAsync</c>: фиксацией распоряжается вызывающий — вместе со
    /// строкой журнала и в одной транзакции с ней (правило 15).
    /// </remarks>
    public async Task PutAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Проверить(key);

        var ключ = envelope.CreateDataKey();
        var запечатано = envelope.SealText(ключ, value, Контекст + key);

        var строка = await db.Set<SystemSecret>()
            .FirstOrDefaultAsync(с => с.Key == key, cancellationToken);

        if (строка is null)
        {
            db.Set<SystemSecret>().Add(new SystemSecret
            {
                Key = key,
                Sealed = запечатано.Bytes,
                DataKey = ключ.Bytes,
                UpdatedAt = clock.GetUtcNow(),
                UpdatedBy = current.Id,
            });

            return;
        }

        строка.Sealed = запечатано.Bytes;
        строка.DataKey = ключ.Bytes;

        // ⚠ Время проставляется ЗДЕСЬ, а не серверным умолчанием: `onupdate` у EF нет вовсе, и
        // колонка показывала бы дату первой записи, молча про все последующие.
        строка.UpdatedAt = clock.GetUtcNow();
        строка.UpdatedBy = current.Id;
    }

    /// <summary>Убрать секрет.</summary>
    /// <returns><c>true</c>, если строка была.</returns>
    public async Task<bool> DropAsync(string key, CancellationToken cancellationToken = default)
    {
        Проверить(key);

        var строка = await db.Set<SystemSecret>()
            .FirstOrDefaultAsync(с => с.Key == key, cancellationToken);

        if (строка is null)
        {
            return false;
        }

        db.Set<SystemSecret>().Remove(строка);
        return true;
    }

    /// <summary>Задан ли секрет — без его открытия.</summary>
    /// <remarks>
    /// ⚠ Нужен экрану настроек: «пароль задан» показывается, сам пароль — никогда. Открывать
    /// секрет ради ответа «да/нет» значило бы держать его в памяти без надобности.
    /// </remarks>
    public async Task<bool> HasAsync(string key, CancellationToken cancellationToken = default)
    {
        Проверить(key);

        return await db.Set<SystemSecret>().AnyAsync(с => с.Key == key, cancellationToken);
    }

    private static void Проверить(string key)
    {
        if (!SystemSecretNames.All.Contains(key))
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "Имя не из закрытого набора секретов установки. Новое имя заводится константой "
                + "рядом с набором, а не строкой в вызове: иначе таблица становится свалкой.");
        }
    }
}
