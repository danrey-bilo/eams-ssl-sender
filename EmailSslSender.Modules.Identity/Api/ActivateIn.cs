using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Что присылают, активируя учётную запись.</summary>
internal sealed record ActivateIn
{
    [Required]
    [StringLength(200, MinimumLength = 10)]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [Required]
    [StringLength(1024, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Ссылка приглашения в теле запроса: секрет из письма не попадает в адрес API.</summary>
internal sealed record ActivationLinkIn
{
    [Required]
    [StringLength(200, MinimumLength = 10)]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}
