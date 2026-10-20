using EmailSslSender.Modules.Identity.Settings;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Crypto;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>Выданная ссылка: что показать человеку и до какого момента она живёт.</summary>
/// <param name="Token">Сама ссылка. Видна ровно один раз — в базе только её хеш.</param>
/// <param name="ExpiresAt">Когда перестанет работать.</param>
internal sealed record Invitation(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Одноразовые ссылки: выдача, чтение, гашение.
/// </summary>
/// <remarks>
/// ⚠ <b>Три свойства, и каждое держится на условии в SQL, а не на проверке в коде.</b>
/// Проверка «а не использована ли уже» с последующим обновлением — это две операции, между
/// которыми помещается второй запрос: две одновременные активации задали бы два разных
/// пароля, и второй затёр бы первый молча.
/// </remarks>
internal sealed class Invitations(SenderDbContext db, ISettingsReader settings, TimeProvider clock, IIntegrityProtector integrity)
{
    /// <summary>
    /// Гашение ссылки — условным обновлением с возвратом владельца.
    /// </summary>
    /// <remarks>
    /// Условия в <c>WHERE</c>, а не в <c>if</c>: выигрывает ровно одна из одновременных
    /// попыток, остальные не находят строки и получают обычный отказ.
    /// </remarks>
    private const string ConsumeSql =
        """
        UPDATE identity_tokens SET used_at = now()
        WHERE token_hash = {0} AND purpose = {1} AND used_at IS NULL AND expires_at > now()
        RETURNING user_id AS "Value"
        """;

    /// <summary>
    /// Выдать новую ссылку.
    /// </summary>
    /// <remarks>
    /// Прежние непогашенные ссылки того же назначения гасятся: «выслать новое приглашение»
    /// означает «прежнее перестало работать». Иначе у человека на руках оказывается
    /// несколько действующих ссылок, и отозвать их разом нечем.
    ///
    /// Не сохраняет: строка уезжает вместе с транзакцией вызывающего, в которой лежит и
    /// запись в журнал.
    /// </remarks>
    /// <param name="userId">Кому.</param>
    /// <param name="purpose">Зачем.</param>
    /// <param name="ttl">
    /// Свой срок. Пусто — срок приглашения из настроек. Задаётся распиской второго фактора:
    /// она живёт минуты, а приглашение — сутки, и один срок на двоих означал бы либо
    /// расписку длиной в сутки, либо приглашение длиной в пять минут.
    /// </param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <param name="binding">Привязка вызова к проверенным учётным данным; не открытый пароль.</param>
    /// <remarks>
    /// ⚠ Готового значения здесь больше нет: код из шести цифр выдаётся своим путём
    /// (<see cref="IssueCodeAsync"/>), с хешем, привязанным к человеку.
    /// </remarks>
    public async Task<Invitation> IssueAsync(
        Guid userId,
        string purpose,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default,
        string? binding = null)
    {
        var minutes = Math.Max(5, await settings.GetAsync(IdentitySettings.InvitationTtlMinutes, cancellationToken));
        var now = clock.GetUtcNow();
        var expires = now + (ttl ?? TimeSpan.FromMinutes(minutes));

        // ⚠ ⚠ Прежние гасятся ВСЕГДА. Для ссылки и расписки это несущее: выданная заново обязана
        // убить прежнюю, иначе две живые расписки удваивают перебор кода.
        await QuenchAsync(userId, purpose, now, cancellationToken);

        // 32 байта случайности. Перебирать там нечего — это и есть причина, по которой
        // ссылку можно хранить обычным SHA-256, а не argon2.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        db.Add(new IdentityToken
        {
            UserId = userId,
            TokenHash = Hash(token),
            Purpose = purpose,
            ExpiresAt = expires,
            CreatedAt = now,
            Binding = binding,
        });

        return new Invitation(token, expires);
    }

    /// <summary>
    /// Посмотреть, кому выдана ссылка, <b>не гася её</b>.
    /// </summary>
    /// <remarks>
    /// Нужно экрану активации: он показывает адрес и требования к паролю ещё до того, как
    /// человек что-то ввёл. Гасить ссылку на открытии страницы значило бы, что случайно
    /// закрытая вкладка требует нового приглашения.
    /// </remarks>
    /// <summary>
    /// Погасить все непогашенные ссылки этого назначения.
    /// </summary>
    /// <remarks>
    /// ⚠ Отметка — <b>время гашения</b>, а не срок той ссылки, что пришла ей на смену. Прежде
    /// сюда ставилось будущее время, и вопрос «когда эту ссылку погасили» ответа не имел:
    /// в поле стояло то, когда истечёт <i>другая</i> ссылка. Уборка при этом держала строку
    /// дольше положенного. Найдено разбором 2026-08-22.
    /// </remarks>
    public Task<int> QuenchAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        db.Set<IdentityToken>()
            .Where(row => row.UserId == userId && row.Purpose == purpose && row.UsedAt == null)
            .ExecuteUpdateAsync(row => row.SetProperty(x => x.UsedAt, now), cancellationToken);

    public async Task<Guid?> PeekAsync(
        string token, string purpose, CancellationToken cancellationToken = default)
    {
        var hash = Hash(token);
        var now = clock.GetUtcNow();

        var row = await db.Set<IdentityToken>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TokenHash == hash
                    && candidate.Purpose == purpose
                    && candidate.UsedAt == null
                    && candidate.ExpiresAt > now,
                cancellationToken);

        return row?.UserId;
    }

    /// <summary>Погасить ссылку и узнать владельца. <c>null</c> — не подошла.</summary>
    public async Task<Guid?> ConsumeAsync(
        string token, string purpose, CancellationToken cancellationToken = default)
    {
        var owners = await db.Database
            .SqlQueryRaw<Guid>(ConsumeSql, Hash(token), purpose)
            .ToListAsync(cancellationToken);

        return owners.Count == 1 ? owners[0] : null;
    }

    /// <summary>
    /// Выдать человеку КОД — шесть цифр, которые он введёт с экрана.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Хеш привязан к человеку, а прежние строки назначения УДАЛЯЮТСЯ</b> (ревью
    /// 2026-09-15, S2, logincode-1). Кодов всего миллион, а уникальный индекс
    /// <c>ix_identity_tokens_hash</c> стоит на всех строках, включая погашенные, которые живут до
    /// уборки ещё <c>identity.link_retention_days</c>. Голый хеш кода сталкивался с чужой мёртвой
    /// строкой с вероятностью «строк в таблице / 10⁶» на каждую выдачу — и вход отвечал
    /// пятисоткой. Привязка к человеку уводит столкновение с чужими строками; удаление своих
    /// прежних — со своими: у человека остаётся ровно одна строка этого назначения. История
    /// входа живёт в журнале, строка кода после конца ничего не рассказывает.
    ///
    /// ⚠ Ссылка с таким «токеном» была бы подбираемой: миллион вариантов вместо 32 байт
    /// случайности. Отсюда обязанность вызывающего — <b>считать попытки</b> тем же счётчиком
    /// неудачных входов, что и у пароля.
    ///
    /// Не сохраняет: строка уезжает вместе с транзакцией вызывающего.
    /// </remarks>
    // мутация 2026-09-15: вернул голый хеш кода и гашение вместо удаления → покраснели
    // TwoFactorTests.Совпавший_код_у_двоих_не_роняет_выдачу и .Повтор_своего_погашенного_кода_не_роняет_выдачу.
    public async Task<Invitation> IssueCodeAsync(
        Guid userId, string purpose, TimeSpan ttl, string code, CancellationToken cancellationToken = default,
        string? binding = null)
    {
        var now = clock.GetUtcNow();
        var expires = now + ttl;

        await db.Set<IdentityToken>()
            .Where(row => row.UserId == userId && row.Purpose == purpose && row.Binding == binding)
            .ExecuteDeleteAsync(cancellationToken);

        db.Add(new IdentityToken
        {
            UserId = userId,
            TokenHash = CodeHash(userId, code, purpose, binding),
            Purpose = purpose,
            ExpiresAt = expires,
            CreatedAt = now,
            Binding = binding,
        });

        return new Invitation(code, expires);
    }

    /// <summary>Погасить КОД этого человека. <c>null</c> — не подошёл.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Ищется по паре «человек, хеш», а не по хешу глобально.</b> Глобальный поиск
    /// означал, что чужой неверный ввод, совпавший с живым кодом другого человека, гасил этот
    /// код — и тот не мог войти.
    ///
    /// ⚠ Условие по человеку в запросе — страховка, а не несущая стена: несёт привязка хеша к
    /// человеку (<see cref="CodeHash"/>), и чужой ввод даёт другой хеш ещё до запроса.
    /// мутация 2026-09-15: убрал условие по человеку → НЕ покраснело ничего (мутация
    /// равносильная, хеш уже разводит людей); убрал человека из хеша → покраснел
    /// TwoFactorTests.Совпавший_код_у_двоих_не_роняет_выдачу.
    /// </remarks>
    public async Task<Guid?> ConsumeCodeAsync(
        Guid userId, string code, string purpose, CancellationToken cancellationToken = default,
        string? binding = null)
    {
        var owners = await db.Database
            .SqlQueryRaw<Guid>(ConsumeCodeSql, CodeHash(userId, code, purpose, binding), userId, purpose)
            .ToListAsync(cancellationToken);

        return owners.Count == 1 ? owners[0] : null;
    }

    private const string ConsumeCodeSql =
        """
        UPDATE identity_tokens SET used_at = now()
        WHERE token_hash = {0} AND user_id = {1} AND purpose = {2} AND used_at IS NULL AND expires_at > now()
        RETURNING user_id AS "Value"
        """;

    /// <summary>SHA-256 от кода, привязанного к человеку.</summary>
    /// <remarks>
    /// ⚠ Не пересекается с хешами ссылок: те — от 43 символов base64url, этот — от строки с
    /// двоеточием. Совпасть они могут только столкновением SHA-256.
    /// </remarks>
    // Подписанная оболочка перед хешированием скрывает шестизначный код от офлайн-перебора дампа БД.
    private byte[] CodeHash(Guid userId, string code, string purpose, string? binding) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(integrity.Protect(
            "identity.code.verifier", $"{userId:N}:{purpose}:{binding}", code)));


    public static string CodeBinding(string challenge) => Convert.ToHexString(Hash(challenge));

    public Task<string?> PeekBindingAsync(string token, string purpose, CancellationToken cancellationToken) =>
        db.Set<IdentityToken>().AsNoTracking().Where(row => row.TokenHash == Hash(token) && row.Purpose == purpose)
            .Select(row => row.Binding).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// SHA-256 от ссылки.
    /// </summary>
    /// <remarks>
    /// В базе живёт только он. Дамп базы не должен давать возможности войти чужой учётной
    /// записью — а незахешированная ссылка ровно это и даёт, причём бесследно.
    /// </remarks>
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
