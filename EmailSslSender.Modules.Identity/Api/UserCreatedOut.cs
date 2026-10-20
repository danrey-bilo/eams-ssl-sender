using System.Text.Json.Serialization;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Ответ на заведение: сам человек и ссылка приглашения.
/// </summary>
/// <remarks>
/// ⚠ <b>Ссылка в теле ответа — только когда почта не настроена.</b> Без неё приглашённый
/// не войдёт никогда, а отдавать её администратору, который сам и завёл человека, —
/// наименьшее из зол: он и так знает, кого пригласил. Почта настроена — ссылка уходит
/// письмом, и поле пусто. В базе лежит только хеш ссылки.
/// </remarks>
internal sealed record UserCreatedOut(
    [property: JsonPropertyName("user")] UserOut User,
    [property: JsonPropertyName("invitation_token")] string? InvitationToken,
    [property: JsonPropertyName("invitation_expires_at")] DateTimeOffset? InvitationExpiresAt);
