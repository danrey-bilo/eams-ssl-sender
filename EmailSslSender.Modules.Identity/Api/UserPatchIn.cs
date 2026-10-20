using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.Identity.Domain;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>Что можно изменить. Не присланное поле не меняется.</summary>
internal sealed record UserPatchIn
{
    [StringLength(200)]
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    // ⚠ Поля is_active здесь нет: включение и отключение — отдельные действия со своими строками
    // журнала и со свежим вторым фактором, а правка профиля его не требует и обошла бы его.

    [JsonPropertyName("must_change_password")]
    public bool? MustChangePassword { get; init; }
}
