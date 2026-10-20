using System.Text.Json.Serialization;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Сколько сессий закрыто.</summary>
internal sealed record RevokedOut([property: JsonPropertyName("revoked")] int Revoked);
