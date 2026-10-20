using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Учётная запись, как её видит клиент.
/// </summary>
/// <remarks>
/// ⚠ <c>password_hash</c> здесь нет и быть не может ни в каком виде. Схема выхода — то
/// место, где секрет утекает тише всего: поле добавляют «на время отладки», а замечают
/// через год в чужом отчёте об ошибке.
/// </remarks>
internal sealed record UserOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("activated_at")] DateTimeOffset? ActivatedAt,
    [property: JsonPropertyName("locked_until")] DateTimeOffset? LockedUntil,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("authenticator_enabled")] bool AuthenticatorEnabled = false)
{
    /// <summary>Из читающей проекции (вскрытие 27.7): хеш пароля из базы не выходит.</summary>
    public static UserOut From(Storage.UserRow user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.IsActive,
        user.MustChangePassword,
        user.ActivatedAt,
        user.LockedUntil,
        user.CreatedAt,
        user.UpdatedAt);

    /// <summary>
    /// Из сущности — там, где она уже на руках после записи.
    /// </summary>
    /// <remarks>
    /// ⚠ Перегрузка законна и нужна: заведение, правка и активация только что сохранили
    /// сущность и знают её целиком — читать её заново проекцией значило бы лишний запрос ради
    /// формы. Чего делать нельзя — звать её из **чтения**: тогда `password_hash` снова поедет
    /// из базы (вскрытие 27.7).
    /// </remarks>
    public static UserOut From(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.IsActive,
        user.MustChangePassword,
        user.ActivatedAt,
        user.LockedUntil,
        user.CreatedAt,
        user.UpdatedAt);
}

/// <summary>Страница списка пользователей.</summary>
internal sealed record UsersPageOut(
    [property: JsonPropertyName("items")] IReadOnlyList<UserOut> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
