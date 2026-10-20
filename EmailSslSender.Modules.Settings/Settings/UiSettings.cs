using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Settings.Settings;

/// <summary>Настройки, общие для всей установки.</summary>
/// <remarks>
/// ⚠ <b>Подписи по-английски</b> — это язык API; переводит их клиент.
/// </remarks>
internal static class UiSettings
{
    /// <summary>Раздел на экране настроек.</summary>
    public const string Category = "ui";

    /// <summary>
    /// Язык писем и сообщений установки.
    /// </summary>
    /// <remarks>
    /// ⚠ Набор языков совпадает с <c>MailLocales.All</c>: письмо обязано быть объявлено на
    /// каждом из них, иначе справочник писем уронит старт.
    /// </remarks>
    public static readonly SettingKey<string> Locale = new(
        "ui.locale",
        Default: "ru",
        "Language",
        "Language of letters: invitations, password reset, sign-in codes.",
        Category,
        Choices: ["uk", "ru", "en"]);
}

/// <summary>Объявление настроек модуля для общего экрана настроек.</summary>
internal sealed class SettingsSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        UiSettings.Locale,
    ];
}
