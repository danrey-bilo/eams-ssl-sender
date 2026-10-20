using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Сколько сессий закрыто.</summary>
internal sealed record RevokedOut([property: JsonPropertyName("revoked")] int Revoked);
