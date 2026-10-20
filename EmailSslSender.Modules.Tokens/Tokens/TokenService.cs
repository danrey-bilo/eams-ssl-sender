using System.Security.Cryptography;
using System.Text;
using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Modules.Tokens.Settings;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Tokens.Tokens;

/// <summary>
/// Выдача, обмен и проверка предъявляемых секретов.
/// </summary>
/// <remarks>
/// Проверка пароля, блокировка после неудач и второй фактор — <b>у соседа</b> (модуль учётных
/// записей): это учётные записи, и знать про них выдаче незачем.
///
/// ⚠ <b>Токены непрозрачные, а не JWT.</b> Подпись избавила бы от обращения к базе при
/// проверке — но обращаться всё равно приходится: человека могли отключить, строку
/// отозвать, секрет украсть. Раз обращение всё равно есть, подпись не покупает ничего, а
/// стоит управления ключом и его ротацией.
///
/// ⚠ <b>Про человека эта служба не знает НИЧЕГО, и это граница модуля.</b> Активен ли он,
/// сменил ли пароль, включён ли фактор — вопросы соседа; мы отвечаем только «секрет жив, и
/// вот чей он». Разбор — у <see cref="ITokenIntrospector"/>.
/// </remarks>
internal sealed class TokenService(
    SenderDbContext db,
    ITokenRevoker revoker,
    ISettingsReader settings,
    TimeProvider clock,
    ILogger<TokenService> log) : ITokenIssuer, ITokenIntrospector
{
    public bool Recognises(string presented) => Ours(presented);

    public async Task<TokenIdentity?> IntrospectAsync(
        string presented, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presented);

        if (!Ours(presented))
        {
            return null;
        }


        var now = clock.GetUtcNow();
        var hash = Hash(presented);

        var found = await (
            from secret in db.Set<AuthTokenSecret>()
            join owner in db.Set<AuthToken>() on secret.TokenId equals owner.Id
            where secret.Hash == hash && secret.Kind == SecretKinds.Access
            select new { secret.ExpiresAt, Token = owner })

            // ⚠ Без отслеживания: этот путь проходит КАЖДЫЙ запрос с токеном, а менять здесь
            // нечего — только читать. Отслеживание держало бы обе сущности в памяти контекста
            // до конца запроса и заставляло EF сверять их на каждом сохранении.
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        // ⚠ Отзыв проверяется РАНЬШЕ срока, и это не косметика. Отзыв гасит секреты, выставляя
        // им срок «сейчас», — то есть проверь мы срок первой, отозванная строка отвечала бы
        // «истекла», и человек так и не узнал бы, что обнаружена кража токена. Ровно та
        // причина, ради сохранения которой всё это и написано. Поймано тестом.
        if (found.Token.RevokedAt is not null)
        {
            throw Expired(CodeFor(found.Token.RevokedReason), Explain(found.Token.RevokedReason));
        }

        // ⚠ ⚠ ⚠ СНАЧАЛА СЕССИЯ, ПОТОМ ДОСТУП, и порядок несущий (ревью 2026-09-15, решение 0080).
        // Истёкший доступ при живой сессии лечится обменом пары, и клиенты обменивают её ТОЛЬКО на
        // `TOKEN_EXPIRED` (`eams.web/src/shared/api/client.ts`, `Eams.Desktop/Api/ApiClient.cs`).
        // Прежде обе ветки сливались в `SESSION_EXPIRED`, и оба клиента уводили человека на вход по
        // истечении каждого токена доступа, хотя половина обновления была жива. Кончившаяся сессия
        // обменом не лечится — ей `SESSION_EXPIRED`, иначе клиент сначала сходит на обмен и лишь
        // после отказа уйдёт на вход.
        // мутация 2026-09-15: проверки переставлены местами → покраснел
        // TokenLifecycleTests.Кончившаяся_сессия_не_просит_обмена.
        if (found.Token.ExpiresAt <= now)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        if (found.ExpiresAt <= now)
        {
            throw Expired(ErrorCodes.TokenExpired, "Токен доступа истёк: обменяйте пару.");
        }

        // ⚠ Отметка второго фактора едет С ТОКЕНОМ: она свойство этой сессии, а не человека.
        return new TokenIdentity(
            found.Token.UserId,
            found.Token.Id,
            AuditActorType.User,
            found.Token.StepUpAt);
    }

    /// <summary>
    /// Выдать пару человеку, про которого вызывающий уже решил, что пускает.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Ничего не сохраняет сам и требует открытой транзакции.</b> Иначе вход состоялся
    /// бы отдельно от обнуления счётчика неудач и от строки журнала — то есть при сбое между
    /// ними человек получил бы токены, а система осталась бы при мнении, что он не входил.
    /// </remarks>
    public Task<TokenPair> IssueAsync(
        TokenIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTransaction();

        if (!TokenKinds.All.Contains(request.Kind))
        {
            // ⚠ Отказ, а не подстановка сессии. Опечатка в виде означала бы токен со сроком
            // и правами не того рода, а обнаружилось бы это на живом ключе.
            throw new ArgumentException(
                $"Неизвестный вид токена «{request.Kind}». Известны: {string.Join(", ", TokenKinds.All)}.",
                nameof(request));
        }

        return IssueAsync(request, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<TokenPair> RotateAsync(
        string refresh, RequestOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(origin);

        var now = clock.GetUtcNow();
        var hash = Hash(refresh);

        // ⚠ Одной выборкой, а не двумя. Секрет и его строка нужны оба и всегда.
        var found = await (
            from candidate in db.Set<AuthTokenSecret>()
            join owner in db.Set<AuthToken>() on candidate.TokenId equals owner.Id
            where candidate.Hash == hash && candidate.Kind == SecretKinds.Refresh
            select new { Secret = candidate, Token = owner })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");

        var secret = found.Secret;
        var token = found.Token;

        // Тот же порядок, что и в проверке, и по той же причине: отзыв гасит секреты, и
        // проверка срока первой съела бы настоящую причину.
        if (token.RevokedAt is not null)
        {
            throw Expired(CodeFor(token.RevokedReason), Explain(token.RevokedReason));
        }

        if (token.ExpiresAt <= now || secret.ExpiresAt <= now)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        // ⚠ Окно читается один раз и ДО обеих проверок повтора: им решается и повтор, замеченный
        // чтением, и повтор, замеченный гашением (ниже).
        var grace = Окно(await settings.GetAsync(TokenSettings.RefreshGraceSeconds, cancellationToken));

        if (secret.UsedAt is { } usedAt)
        {
            if (Повтор(now, usedAt, grace))
            {
                throw await КражаAsync(token, now - usedAt, cancellationToken);
            }

            // Внутри окна — гонка вкладок, а не кража. Выдаём новую пару от текущего конца
            // цепочки и ничего не отзываем. ⚠ Ветка эта не независима: первый обычный обмен
            // любой из веток гасит остальные (ниже, в транзакции).
            log.LogDebug("refresh_token_race {TokenId} {Age}", token.Id, now - usedAt);
        }

        var выдано = await db.InTransactionAsync(
            async (inner) =>
            {
                // ⚠ Гашение условным обновлением, а не присваиванием: одновременные запросы
                // иначе оба сочли бы себя первыми.
                var погашено = await db.Set<AuthTokenSecret>()
                    .Where(row => row.Id == secret.Id && row.UsedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UsedAt, now), inner);

                // ⚠ ⚠ Ноль строк — половину погасил ОДНОВРЕМЕННЫЙ запрос, прочитавший её вместе с
                // нами (чтение выше идёт без блокировки). Прежняя редакция число строк не
                // смотрела и выдавала пару всегда — то есть две одновременные предъявы одной
                // половины давали две живые цепочки, и обнаружение повтора между ними не
                // срабатывало никогда, даже при окне 0 (ревью 2026-09-15, S2, refresh-1).
                // Решает то же окно, что и выше: внутри — гонка вкладок, снаружи — кража.
                // мутация 2026-09-15: убрал эту проверку (выдача при нуле строк) → покраснел
                // TokenLifecycleTests.Одновременные_обновления_при_нулевом_окне_не_расщепляют_цепочку.
                if (погашено == 0)
                {
                    var когда = await db.Set<AuthTokenSecret>()
                        .AsNoTracking()
                        .Where(row => row.Id == secret.Id)
                        .Select(row => row.UsedAt)
                        .SingleAsync(inner);

                    if (когда is { } использован && Повтор(now, использован, grace))
                    {
                        return (TokenPair?)null;
                    }
                }
                else
                {
                    // ⚠ ⚠ ⚠ Обычный обмен гасит ВСЕ прочие живые половины этой строки (ревью
                    // безопасности 2026-10-20). Живая половина рядом с той, что предъявлена, бывает
                    // ровно одна: ветка, выданная гонкой внутри окна снисхождения. Прежде такая
                    // ветка жила сама по себе — у вора, предъявившего украденную половину в те же
                    // десять секунд, что и владелец, оставалась НЕЗАВИСИМАЯ цепочка: каждая
                    // сторона меняла свои половины, ни одна не предъявляла чужую, и повтор не
                    // замечался до абсолютного срока сессии. Теперь первый же обмен любой ветки
                    // гасит другую, а погашенная, предъявленная позже окна, — кража по общему
                    // правилу (`Повтор`), и закрывается сессия целиком.
                    //
                    // ⚠ Гасится отметкой «использована», а не сроком: срок дал бы молчаливое «сессия
                    // кончилась», и кражу не заметил бы никто.
                    //
                    // ⚠ Только на ОБЫЧНОМ обмене, не на гонке (ветка выше): гонка вкладок
                    // заканчивается тем, что кука у браузера одна — та, чей ответ пришёл последним, и
                    // это может оказаться любая из веток. Погаси гонка первую ветку — следующий
                    // обмен той же вкладкой оказался бы «кражей» на ровном месте. Повтор клиента
                    // после зависшего ответа (настольный клиент) первую ветку не получал вовсе.
                    //
                    // ⚠ Токены доступа погашенной ветки доживают свой короткий срок — как и
                    // прежний токен доступа при обычном обмене.
                    await db.Set<AuthTokenSecret>()
                        .Where(row => row.TokenId == token.Id
                            && row.Kind == SecretKinds.Refresh
                            && row.UsedAt == null
                            && row.Id != secret.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UsedAt, now), inner);
                }

                // Оператором, а не присваиванием: строка прочитана без отслеживания, и
                // отслеживать её здесь незачем — меняется ровно одно поле.
                await db.Set<AuthToken>()
                    .Where(row => row.Id == token.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSeenAt, now), inner);

                var pair = await IssueAsync(
                    new TokenIssueRequest(token.UserId, token.Kind, origin.UserAgent, origin.Address),
                    now,
                    inner,
                    token.Id,
                    token.ExpiresAt);

                await db.SaveChangesAsync(inner);

                return (TokenPair?)pair;
            },
            cancellationToken);

        // Отзыв — своей транзакцией, ПОСЛЕ той, что ничего не выдала: брошенный внутри неё отказ
        // откатил бы и отзыв.
        return выдано ?? throw await КражаAsync(token, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// Предъявленная половина — уже потраченная и не гонка вкладок?
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Окно 0 означает «повтор всегда кража»</b>, в том числе у одновременных запросов, чьи
    /// отметки времени разошлись в обратную сторону на миллисекунды. Это цена, которую принимает
    /// администратор, выключивший окно: две законные вкладки, обновляющиеся одновременно, закроют
    /// сессию как украденную.
    /// </remarks>
    private static bool Повтор(DateTimeOffset now, DateTimeOffset usedAt, TimeSpan grace) =>
        grace <= TimeSpan.Zero || now - usedAt > grace;

    /// <summary>Наименьшее ненулевое окно снисхождения: повтор обмена клиентом со своим путём по сети.</summary>
    internal static readonly TimeSpan НаименьшееОкно = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Действующее окно снисхождения: ноль — строгий режим, иначе не короче <see cref="НаименьшееОкно"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>Замечание ревью к PR #31, сведение ревью 2026-09-15.</b> Настольный клиент повторяет
    /// потерянный обмен (<c>Eams.Desktop/Api/Session.cs</c>): первая попытка ждёт своим сроком 4 с, пауза
    /// полсекунды, затем повтор со своим путём по сети. Если первый обмен дошёл и погасил половину, а
    /// ответ завис, повтор приходит через пять с лишним секунд. Окно короче делало его «кражей»: сессия
    /// закрывалась, поднималась тревога, клиент стирал зеркало. Настройка разрешает от 1 до 9 секунд, и
    /// ни одно из этих значений повтор не покрывает.
    ///
    /// ⚠ Ненулевое окно ПОДНИМАЕТСЯ до десяти секунд, а не отклоняется: сохранённое значение у установки
    /// уже может быть, и отказ из-за него хуже. Десять — значение по умолчанию, риск которого уже принят.
    /// Ноль — строгий режим с названной ценой (<see cref="Повтор"/>) — остаётся как есть.
    ///
    /// ⚠ Удлинишь в клиенте срок первой попытки или паузу — сверь это число.
    ///
    /// мутация 2026-09-15: окно без нижней границы → покраснел
    /// TokenLifecycleTests.Короткое_окно_не_делает_повтор_клиента_кражей.
    /// мутация 2026-09-15: окно в сто раз длиннее нижней границы → покраснел
    /// TokenLifecycleTests.Повтор_позже_наименьшего_окна_остаётся_кражей.
    /// </remarks>
    internal static TimeSpan Окно(int секунд) =>
        секунд <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(секунд, НаименьшееОкно.TotalSeconds));

    /// <summary>Закрыть сессию как украденную и вернуть отказ, который бросит вызывающий.</summary>
    private async Task<AppException> КражаAsync(AuthToken token, TimeSpan age, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(
            async (inner) =>
            {
                await revoker.RevokeAsync(token.Id, RevokeReasons.Stolen, inner);

                return true;
            },
            cancellationToken);

        log.LogWarning("refresh_token_reuse {TokenId} {UserId} {Age}", token.Id, token.UserId, age);

        return Expired(ErrorCodes.SessionRevoked, Explain(RevokeReasons.Stolen));
    }

    /// <summary>Живые строки человека — «где я вошёл».</summary>
    public Task<List<AuthToken>> ListAsync(
        Guid userId, string kind, CancellationToken cancellationToken = default) =>
        db.Set<AuthToken>()
            .AsNoTracking()
            .Where(row => row.UserId == userId
                && row.Kind == kind
                && row.RevokedAt == null
                && row.ExpiresAt > clock.GetUtcNow())
            .OrderByDescending(row => row.LastSeenAt)
            .ToListAsync(cancellationToken);

    // --- внутреннее -----------------------------------------------------------------------

    /// <summary>Выдать пару, заведя строку или продлив существующую.</summary>
    private async Task<TokenPair> IssueAsync(
        TokenIssueRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? existing = null,
        DateTimeOffset? existingExpires = null)
    {
        var accessMinutes = Math.Max(1, await settings.GetAsync(TokenSettings.AccessTtlMinutes, cancellationToken));
        var idleMinutes = Math.Max(1, await settings.GetAsync(TokenSettings.SessionIdleMinutes, cancellationToken));
        var absoluteHours = Math.Max(1, await settings.GetAsync(TokenSettings.SessionAbsoluteHours, cancellationToken));

        var tokenId = existing ?? Guid.CreateVersion7();
        var expires = existingExpires ?? now.AddHours(absoluteHours);

        if (existing is null)
        {
            db.Add(new AuthToken
            {
                Id = tokenId,
                UserId = request.UserId,
                Kind = request.Kind,
                Name = request.Name,
                Address = request.Address,
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = expires,
                StepUpAt = request.StepUpAt,
            });
        }

        var access = NewSecret();
        var refresh = NewSecret();

        var accessExpires = now.AddMinutes(accessMinutes);

        // ⚠ Срок обновления упирается в абсолютный срок строки. Без этого простой продлевался
        // бы бесконечно, и украденный токен вместе с ним: строка, которой пользуются активно,
        // простоя не накопит никогда.
        var refreshExpires = Min(now.AddMinutes(idleMinutes), expires);

        db.Add(new AuthTokenSecret
        {
            Id = Guid.CreateVersion7(),
            TokenId = tokenId,
            Kind = SecretKinds.Access,
            Hash = Hash(access),
            IssuedAt = now,
            ExpiresAt = accessExpires,
        });

        db.Add(new AuthTokenSecret
        {
            Id = Guid.CreateVersion7(),
            TokenId = tokenId,
            Kind = SecretKinds.Refresh,
            Hash = Hash(refresh),
            IssuedAt = now,
            ExpiresAt = refreshExpires,
        });

        return new TokenPair(access, accessExpires, refresh, refreshExpires, tokenId);
    }

    private static AppException Expired(string code, string message) =>
        new(code, message, System.Net.HttpStatusCode.Unauthorized);

    /// <summary>
    /// Машинный код отказа по причине закрытия.
    /// </summary>
    /// <remarks>
    /// ⚠ Не один код на все отзывы. Отключённая учётная запись — это не «сессия закрыта,
    /// войдите заново»: входить заново бесполезно, и клиент обязан сказать об этом прямо.
    /// </remarks>
    private static string CodeFor(string? reason) => reason switch
    {
        RevokeReasons.UserInactive => ErrorCodes.UserInactive,


        // ⚠ Клиент различает «сессия закрыта» (SESSION_REVOKED) и «сессия истекла»
        // (SESSION_EXPIRED) именно по этой строке: первое — повод сказать человеку причину.
        _ => ErrorCodes.SessionRevoked,
    };

    /// <summary>Человеческое объяснение, почему доступ кончился.</summary>
    /// <remarks>
    /// Единственное, что экран входа может сказать вместо голого «войдите заново». Разница
    /// между «вышли сами» и «обнаружена кража токена» — это разница между «ладно» и
    /// «звоните в безопасность».
    /// </remarks>
    private static string Explain(string? reason) => reason switch
    {
        RevokeReasons.SignedOut => "Вы вышли из системы.",
        RevokeReasons.Revoked => "Сессия закрыта. Войдите заново.",
        RevokeReasons.Stolen => "Обнаружено повторное использование токена — сессия закрыта. "
            + "Если это были не вы, сообщите администратору.",
        RevokeReasons.UserInactive => "Учётная запись отключена.",
        RevokeReasons.PasswordChanged => "Пароль изменён — войдите заново.",

        RevokeReasons.TwoFactorReset => "Второй фактор снят — прежние сессии закрыты. Войдите заново.",

        _ => "Сессия кончилась. Войдите заново.",
    };

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first < second ? first : second;

    /// <summary>32 байта случайности в URL-безопасном виде.</summary>
    private static string NewSecret() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Длина секрета в символах: 32 байта в base64url без выравнивания.</summary>
    private const int SecretLength = 43;

    /// <summary>
    /// Похоже ли предъявленное на <b>наш</b> секрет.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Заголовок <c>Authorization: Bearer</c> — не собственность одного модуля.</b>
    /// Распознаватели личности выстроены цепочкой. Прежде модуль личностей забирал
    /// <b>любое</b> непустое значение после <c>Bearer</c> и отвечал на негодное исключением
    /// «сессия кончилась» — то есть обрывал опрос цепочки. Найдено разбором 2026-08-22.
    ///
    /// <b>Проверяется форма, а не значение.</b> Наш секрет — ровно 32 байта случайности в
    /// base64url: сорок три символа. Всё, что не такой формы, — «не ко мне», и цепочка идёт
    /// дальше. Всё, что такой формы, но неизвестно, — по-прежнему исключение.
    ///
    /// ⚠ Это <b>не</b> проверка подлинности и не должна ею стать: угадать форму тривиально,
    /// а решает по-прежнему хеш в базе. Сужение здесь нужно ради соседей, не ради защиты.
    /// </remarks>
    internal static bool Ours(string token) => Сессия(token);

    /// <summary>Секрет сессии: ровно сорок три символа base64url.</summary>
    private static bool Сессия(string token) =>
        token.Length == SecretLength && Base64Url(token);

    /// <summary>Все символы — из алфавита base64url без выравнивания.</summary>
    private static bool Base64Url(ReadOnlySpan<char> value)
    {
        foreach (var symbol in value)
        {
            var allowed = symbol is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '_';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// SHA-256 от секрета.
    /// </summary>
    /// <remarks>
    /// В базе живёт только он: дамп базы не должен давать возможности войти чужой сессией.
    /// Argon2 здесь не нужен и вреден — секрет это 256 бит случайности, перебирать нечего, а
    /// платить тридцатью миллисекундами на каждом запросе было бы разорительно. NIST
    /// SP 800-63B-4 §3.1.2.2 проводит границу ровно там: секреты длиннее 112 бит хранятся
    /// «approved hashing function», короче — обязаны идти через схему хеширования паролей.
    /// </remarks>
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "Выдача токенов обязана идти внутри транзакции вызывающего: иначе вход состоится "
            + "отдельно от обнуления счётчика неудач и от строки журнала (правило 15).");
    }
}
