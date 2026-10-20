using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Что присылают, заводя учётную запись.</summary>
internal sealed record UserIn
{
    [Required]
    [StringLength(320, MinimumLength = 3)]
    // ⚠ Форма, а не только длина (вскрытие 43). Прежде здесь стояла одна длина, и адресом
    // учётной записи могла стать строка без `@` — в вебе это не воспроизводилось, потому что
    // проверку держал `type="email"` у поля формы. Служба проверяет тем же средством: разойдись
    // они, API и команда `create-superadmin` считали бы адресом разное.
    [EmailAddress]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}
