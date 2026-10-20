using EmailSslSender.Modules.KeyManager.Settings;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;

namespace EmailSslSender.Modules.KeyManager.Secrets;

/// <summary>Привязка ключа API, заданного до привязки, к текущему адресу и отпечатку — для <c>migrate</c>.</summary>
internal sealed class ApiKeyLegacyBinding(ApiKeyStore store, ISettingsReader settings) : ILegacySecretBinder
{
    public string Name => "ключ API Key Manager Plus";

    public async Task<bool> BindAsync(CancellationToken cancellationToken = default)
    {
        var адрес = await settings.GetAsync(KeyManagerSettings.BaseUrl, cancellationToken);

        // Адреса нет — привязывать не к чему: ключ откроется после ввода заново.
        if (string.IsNullOrWhiteSpace(адрес))
        {
            return false;
        }

        var отпечаток = await settings.GetAsync(KeyManagerSettings.ServerSha256, cancellationToken);

        return await store.BindLegacyAsync(ApiKeyStore.Target(адрес, отпечаток), cancellationToken);
    }
}
