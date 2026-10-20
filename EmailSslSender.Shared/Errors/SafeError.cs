namespace EmailSslSender.Shared.Errors;

/// <summary>
/// Описание исключения, которое можно писать в лог.
/// </summary>
/// <remarks>
/// ⚠ <b>Правило 14: текст исключения при сбое подключения не логируется.</b> Строка
/// подключения Npgsql содержит пароль целиком, а Serilog печатает переданное исключение
/// вместе с сообщением и всеми внутренними. Достаточно одного неудачного подключения,
/// чтобы пароль от базы менеджера паролей оказался в логе — в файле, который потом
/// пересылают в тикет.
///
/// Отсюда правило: в местах, где открывается подключение, исключение <b>не передаётся</b>
/// логгеру объектом. Передаётся вот эта строка — цепочка <b>типов</b>:
/// <c>NpgsqlException → SocketException</c>. Типы не содержат ни строк подключения, ни
/// пользовательских данных, а различать «база не отвечает» и «отказано в правах на схему»
/// по ним можно.
///
/// Полный текст при этом не теряется: он остаётся в логе самой базы, где ему и место.
/// </remarks>
public static class SafeError
{
    /// <summary>Сколько звеньев цепочки показывать. Глубже — уже не диагностика, а шум.</summary>
    private const int MaxDepth = 4;

    /// <summary>Цепочка типов исключения: <c>Внешний → Внутренний → …</c>.</summary>
    public static string Describe(Exception? error)
    {
        if (error is null)
        {
            return "нет";
        }

        var chain = new List<string>(MaxDepth);

        for (var current = error; current is not null && chain.Count < MaxDepth; current = current.InnerException)
        {
            chain.Add(current.GetType().Name);
        }

        return string.Join(" → ", chain);
    }
}
