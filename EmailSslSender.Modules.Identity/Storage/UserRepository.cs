using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Api;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Storage;

/// <summary>
/// Единственная точка доступа к таблицам модуля.
/// </summary>
/// <remarks>
/// Условие «живой» (<c>deleted_at IS NULL</c>) пишется здесь и только здесь. Глобального
/// фильтра EF Core нарочно нет: он молчаливо влияет на все запросы, включая те, где
/// удалённых как раз надо увидеть, — и обнаруживается это не чтением кода, а удивлением.
/// </remarks>
internal sealed class UserRepository(SenderDbContext db)
{
    /// <summary>
    /// Активные учётные записи — одним запросом, который их же и блокирует.
    /// </summary>
    /// <remarks>
    /// ⚠ Блокировка и чтение обязаны быть <b>одним</b> оператором. Первая редакция этого
    /// метода читала идентификаторы обычным запросом, а <c>FOR UPDATE</c> выполняла
    /// следом — то есть между чтением и блокировкой оставалась ровно та щель, ради
    /// закрытия которой всё и написано.
    /// </remarks>
    private const string LockActiveUsers =
        "SELECT * FROM users WHERE deleted_at IS NULL AND is_active FOR UPDATE";

    /// <summary>Живые пользователи.</summary>
    public IQueryable<User> Live => db.Set<User>().Where(user => user.DeletedAt == null);

    /// <summary>Найти живого по идентификатору.</summary>
    /// <remarks>
    /// ⚠ Сущность целиком и с трекингом — это путь ЗАПИСИ: смена имени, пароля, флагов.
    /// Для чтения есть <see cref="ReadAsync"/>, и он не берёт `password_hash` (вскрытие 27.7).
    /// </remarks>
    public Task<User?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Live.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    /// <summary>Прочитать живого по идентификатору — без трекинга и без хеша пароля.</summary>
    public Task<UserRow?> ReadAsync(Guid id, CancellationToken cancellationToken = default) =>
        Live
            .Where(user => user.Id == id)
            .Select(user => new UserRow(
                user.Id,
                user.Email,
                user.DisplayName,
                user.IsActive,
                user.MustChangePassword,
                user.ActivatedAt,
                user.LockedUntil,
                user.CreatedAt,
                user.UpdatedAt))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Найти живого по адресу.
    /// </summary>
    /// <remarks>
    /// Регистр не приводится: колонка <c>citext</c>, и сравнение уже нечувствительно к нему.
    /// Приводить дополнительно значило бы завести второе правило рядом с первым — и однажды
    /// они разойдутся.
    /// </remarks>
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        Live.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    /// <summary>Есть ли живой с таким адресом.</summary>
    public Task<bool> EmailTakenAsync(string email, CancellationToken cancellationToken = default) =>
        Live.AnyAsync(user => user.Email == email, cancellationToken);

    /// <summary>
    /// Страница списка людей: поиск, потолок, курсор.
    /// </summary>
    /// <remarks>
    /// ⚠ Запрос живёт здесь, а не в контроллере. До 2026-08-22 контроллер собирал его сам —
    /// из <c>Live</c>, <c>Where</c> и <c>Take</c>, — вопреки собственному комментарию о том,
    /// что репозиторий это единственная точка доступа к таблицам. Пока запрос один, разницы
    /// не видно; когда их станет три, условие «живой» разъедется между ними.
    ///
    /// Берётся на одну строку больше, чем просят: лишняя отвечает на вопрос «есть ли
    /// следующая страница», и отвечает точно — в отличие от догадки «раз набрали ровно
    /// потолок, наверное есть ещё».
    /// </remarks>
    /// <summary>Сколько отдавать, если не просили.</summary>
    private const int DefaultLimit = 50;

    /// <summary>Больше этого не отдаём: страница — не выгрузка.</summary>
    private const int MaxLimit = 200;

    /// <summary>Читающая проекция: та же, что у страницы, но без выполнения.</summary>
    /// <remarks>
    /// ⚠ Выделена отдельно ради факта «запрос списка не читает `password_hash`»: сторож
    /// смотрит текст запроса, который построит EF, и без такой точки ему смотреть не на что.
    /// </remarks>
    internal IQueryable<UserRow> PageQuery(string? query, string? after)
    {
        var rows = Live;

        if (!string.IsNullOrWhiteSpace(query))
        {
            // ⚠ Не ILike: он есть только у Npgsql, а модуль провайдера не знает и знать не
            // должен — иначе смена базы становится правкой во всех модулях сразу.
            //
            // Адрес и без того сравнивается без учёта регистра: колонка citext. Имя —
            // обычный text, поэтому регистр снимается явно. Индекса под это нет намеренно:
            // пользователей в корпоративной установке сотни, и полный проход по ним дешевле
            // индекса, который надо поддерживать.
            var needle = query.Trim();
            var lowered = needle.ToLowerInvariant();

            rows = rows.Where(user =>
                user.Email.Contains(needle)
                || (user.DisplayName != null && user.DisplayName.ToLower().Contains(lowered)));
        }

        if (after is { } cursor)
        {
            rows = rows.Where(user => string.Compare(user.Email, cursor) > 0);
        }

        // ⚠ ⚠ Проекция, а не сущность: `password_hash` из базы не выходит (вскрытие 27.7).
        return rows
            .OrderBy(user => user.Email)
            .Select(user => new UserRow(
                user.Id,
                user.Email,
                user.DisplayName,
                user.IsActive,
                user.MustChangePassword,
                user.ActivatedAt,
                user.LockedUntil,
                user.CreatedAt,
                user.UpdatedAt))
            .AsNoTracking();
    }

    public async Task<(List<UserRow> Items, string? NextCursor)> PageAsync(
        string? query, string? after, int limit, CancellationToken cancellationToken = default)
    {
        // ⚠ Строгая проверка, а не подрезание: одно правило на всю систему, разбор — в
        // `PageLimit`. Прежде здесь молча отдавали двести из тысячи запрошенных.
        var page = PageLimit.Require(limit, DefaultLimit, MaxLimit);

        var items = await PageQuery(query, after)
            .Take(page + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= page)
        {
            return (items, null);
        }

        items.RemoveAt(page);

        return (items, UserCursor.Encode(items[^1].Email));
    }

    /// <summary>Добавить в текущую единицу работы. Не сохраняет.</summary>
    public void Add(User user) => db.Add(user);

    /// <summary>
    /// Идентификаторы активных учётных записей, <b>с блокировкой их строк</b>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Блокировка, а не подсчёт, и это не перестраховка.</b> Без неё две параллельные
    /// транзакции, каждая отключающая своего администратора, обе увидят «есть ещё один» и
    /// обе пройдут проверку — система останется без единого администратора, и вернуть
    /// его будет нечем, кроме прямого доступа к базе.
    ///
    /// Отдаются идентификаторы, а не количество: агрегат с <c>FOR UPDATE</c> несовместим,
    /// поэтому сравнение делается уже здесь, в коде.
    /// </remarks>
    /// <remarks>
    /// ⚠ ⚠ Возвращаются только те, кто <b>может войти</b> (ревью безопасности 2026-10-20):
    /// активированные и с настоящим паролем. Неактивированное приглашение тоже «активно», но
    /// войти по нему нельзя — посчитай его, и последний настоящий администратор удалял бы себя,
    /// оставляя систему без единого входа. Блокируются при этом все активные строки: порядок
    /// параллельных изменений тот же.
    /// </remarks>
    public async Task<HashSet<Guid>> LockActiveUsersAsync(CancellationToken cancellationToken = default)
    {
        // Запрос-константа, без подстановки: имя таблицы в нём литеральное, и анализатору
        // EF1002 придраться не к чему.
        var locked = await db.Set<User>()
            .FromSqlRaw(LockActiveUsers)
            .ToListAsync(cancellationToken);

        return [.. locked.Where(CanSignIn).Select(user => user.Id)];
    }

    /// <summary>Может ли учётная запись войти: активирована и пароль не заглушка.</summary>
    internal static bool CanSignIn(User user) => user.ActivatedAt is not null && user.PasswordHash != "!";
}
