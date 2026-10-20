using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Identity.Settings;

/// <summary>Объявление настроек модуля для общего экрана настроек.</summary>
internal sealed class IdentitySettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        IdentitySettings.PasswordMinLength,
        IdentitySettings.RequireUppercase,
        IdentitySettings.RequireLowercase,
        IdentitySettings.RequireDigit,
        IdentitySettings.RequireSymbol,
        IdentitySettings.InvitationTtlMinutes,
        IdentitySettings.LockoutThreshold,
        IdentitySettings.LockoutMinutes,
        IdentitySettings.LinkRetentionDays,
        IdentitySettings.TwoFactorChallengeMinutes,
        IdentitySettings.StepUpMinutes,
    ];
}
