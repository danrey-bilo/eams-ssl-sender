namespace EmailSslSender.Shared;

/// <summary>Имя сервиса для людей.</summary>
/// <remarks>
/// Одно место на всё, что видит человек: подпись писем, отправитель по умолчанию, подпись записи
/// в приложении-аутентификаторе. Решение владельца 2026-10-26.
/// </remarks>
public static class Product
{
    /// <summary>Как сервис называется в письмах и интерфейсе.</summary>
    public const string Name = "EAMS ssl sender";
}
