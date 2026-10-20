namespace EmailSslSender.Shared.Contracts.Tokens;

/// <summary>
/// Виды предъявляемых токенов.
/// </summary>
/// <remarks>
/// ⚠ <b>Вид один — сессия человека</b>, и всё же он назван, а не подразумевается: значение
/// ложится в колонку <c>kind</c> и в её <c>CHECK</c>-ограничение. В EAMS здесь же жили
/// токены настольного клиента, ключи интеграции и билеты на скачивание; появится новый вид —
/// он встаёт строкой сюда и в ограничение базы, а не подменяет смысл существующего.
/// </remarks>
public static class TokenKinds
{
    /// <summary>Вход человека: браузер, скрипт с паролем, клиент.</summary>
    public const string Session = "session";

    /// <summary>Все допустимые значения — для ограничения в базе.</summary>
    public static readonly string[] All = [Session];
}
