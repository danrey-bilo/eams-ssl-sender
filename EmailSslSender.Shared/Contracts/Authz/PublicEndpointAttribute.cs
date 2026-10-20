namespace EmailSslSender.Shared.Contracts.Authz;

/// <summary>
/// Эндпоинт открыт без входа.
/// </summary>
/// <remarks>
/// ⚠ <b>По умолчанию закрыто всё</b>: фильтр хоста (<c>AuthenticationFilter</c>) отказывает
/// неаутентифицированному на любом действии без этой пометки. Забытая пометка даёт
/// 401 — громко и сразу; забытая проверка при обратном умолчании отдавала бы данные молча.
///
/// Причина обязательна и пишется словами: «вход: у пришедшего ещё нет сессии». Пометка без
/// причины — это открытая дверь, про которую через полгода никто не скажет, зачем она.
/// </remarks>
/// <param name="reason">Почему сюда пускают без входа.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PublicEndpointAttribute(string reason) : Attribute
{
    /// <summary>Почему сюда пускают без входа.</summary>
    public string Reason { get; } = reason;
}
