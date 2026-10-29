using Modules.Core.Contracts.Settings;
using Modules.Mail.Contracts;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Внешний адрес установки — заданный настройкой.</summary>
internal sealed class PublicOrigin(ISettingsReader settings) : IPublicOrigin
{
    public async Task<string?> ConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var адрес = await settings.GetAsync(PublicUrlSetting.Key, cancellationToken);

        return string.IsNullOrWhiteSpace(адрес) ? null : СоСхемой(адрес.Trim().TrimEnd('/'));
    }

    // ⚠ Только настройка: имя хоста из запроса подделывается, и ссылка с токеном ушла бы на чужой сервер.
    public Task<string?> ResolveAsync(CancellationToken cancellationToken = default) =>
        ConfiguredAsync(cancellationToken);

    // ⚠ Адрес без схемы браузер считает относительным; https — наружу смотрит туннель.
    private static string СоСхемой(string адрес) =>
        адрес.Contains("://", StringComparison.Ordinal) ? адрес : $"https://{адрес}";
}
