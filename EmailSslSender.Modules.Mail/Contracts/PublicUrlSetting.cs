using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Mail.Contracts;

/// <summary>
/// Ключ внешнего адреса установки — наружу, для хоста.
/// </summary>
/// <remarks>
/// ⚠ <b>Единственное объявление настройки, торчащее из модуля наружу.</b> Хост реализует
/// <c>IPublicOrigin</c> и обязан прочитать именно этот ключ; передать ему строку с именем
/// значило бы завести второе место, где имя написано, — и разойдутся они молча.
///
/// ⚠ <b>Сама настройка объявлена в <c>MailSettings</c> и остаётся внутренней.</b> Наружу торчит
/// только ключ, и только ради чтения.
/// </remarks>
public static class PublicUrlSetting
{
    /// <summary>Ключ настройки внешнего адреса.</summary>
    public static SettingKey<string> Key => MailSettings.PublicUrl;
}
