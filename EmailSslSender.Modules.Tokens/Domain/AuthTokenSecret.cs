namespace EmailSslSender.Modules.Tokens.Domain;

/// <summary>
/// Сам предъявляемый секрет — то, что уезжает клиенту.
/// </summary>
/// <remarks>
/// <b>Приехало из <c>Identity/Domain/Session.cs</c> (<c>SessionToken</c>, этап 10.5.4).</b>
///
/// <b>Одна таблица на все виды секретов.</b> У них общее всё, кроме срока: выдача, хранение
/// хешем, поиск, признак использования. Разводить их по таблицам значило бы писать один и
/// тот же код трижды, и вторая копия однажды забыла бы проверить срок.
///
/// ⚠ <b>Токенов доступа у сессии бывает несколько живых сразу</b>, и это намеренно.
/// Обновление выдаёт новый, но прежний доживает свой короткий век: иначе вкладка, не
/// успевшая узнать о новом токене, получала бы отказ на ровном месте.
/// </remarks>
internal sealed class AuthTokenSecret
{
    public Guid Id { get; set; }

    /// <summary>Чьей строке принадлежит.</summary>
    public Guid TokenId { get; set; }

    /// <summary>Вид: <see cref="SecretKinds"/>.</summary>
    public string Kind { get; set; } = SecretKinds.Access;


    /// <summary>
    /// SHA-256 от самого секрета.
    /// </summary>
    /// <remarks>
    /// В базе живёт только он. Дамп базы не должен давать возможности войти чужой сессией.
    /// Argon2 здесь не нужен: секрет — это 32 байта случайности, перебирать нечего, а
    /// платить тридцатью миллисекундами за каждый запрос было бы разорительно. Граница NIST
    /// в 112 бит энтропии пройдена с запасом.
    /// </remarks>
    public byte[] Hash { get; set; } = [];

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Когда секретом воспользовались.
    /// </summary>
    /// <remarks>
    /// Заполняется только у токенов обновления и <b>только один раз</b>. Предъявленный
    /// повторно потраченный токен — это обнаружение кражи, а не ошибка.
    /// </remarks>
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Виды секретов.</summary>
internal static class SecretKinds
{
    /// <summary>Короткоживущий, предъявляется на каждом запросе.</summary>
    public const string Access = "access";

    /// <summary>Долгоживущий, обменивается на новую пару и гасится при этом.</summary>
    public const string Refresh = "refresh";


    /// <summary>Все допустимые значения — для ограничения в базе.</summary>
    public static readonly string[] All = [Access, Refresh];
}
