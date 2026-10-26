namespace EmailSslSender.Api.Infrastructure;

/// <summary>Имя сервиса для людей.</summary>
/// <remarks>
/// Одно место на всё, что видит человек: подпись писем и отправитель по умолчанию (модуль почты),
/// подпись записи в приложении-аутентификаторе (модуль учётных записей), заголовок документа API.
/// Решение владельца 2026-10-26.
/// </remarks>
internal static class Product
{
    /// <summary>Как сервис называется в письмах и интерфейсе.</summary>
    public const string Name = "EAMS ssl sender";
}
