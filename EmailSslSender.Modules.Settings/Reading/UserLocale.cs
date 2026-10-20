using EmailSslSender.Modules.Settings.Settings;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Settings.Reading;

/// <summary>
/// На каком языке писать человеку — для писем.
/// </summary>
/// <remarks>
/// ⚠ <b>Язык один на установку</b> (<c>ui.locale</c>): личных предпочтений здесь нет. Контракт
/// при этом спрашивает про конкретного человека — так он устроен в EAMS, и появление личного
/// языка не потребует правки тех, кто шлёт письма.
/// </remarks>
internal sealed class UserLocale(SettingsReader reader) : IUserLocale
{
    public async Task<string> ForAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await reader.GetAsync(UiSettings.Locale, cancellationToken);
}
