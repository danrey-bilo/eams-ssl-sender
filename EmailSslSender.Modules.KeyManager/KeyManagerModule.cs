using System.Security.Cryptography.X509Certificates;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Export;
using EmailSslSender.Modules.KeyManager.Schema;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Settings;
using EmailSslSender.Modules.KeyManager.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Core.Composition;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Features;
using Modules.Crypto;

namespace EmailSslSender.Modules.KeyManager;

/// <summary>Подключение модуля Key Manager Plus.</summary>
public sealed class KeyManagerModuleOptions
{
    /// <summary>Поднимать ли фоновый опрос в этом процессе; решает хост по роли процесса.</summary>
    public bool RunPoller { get; set; }

    /// <summary>Переключатели модуля (своих подвозможностей нет).</summary>
    public KeyManagerModuleFeatures Features { get; } = new();

    /// <summary>Корни доверия вместо системных для соединения с KMP.</summary>
    /// <remarks>⚠ Только для тестов хоста; рабочий хост их не задаёт, свой УЦ — через <c>SSL_CERT_FILE</c>.</remarks>
    public X509Certificate2Collection TrustedRoots { get; } = [];
}

/// <summary>Переключатели модуля Key Manager Plus.</summary>
public sealed class KeyManagerModuleFeatures() : ModuleFeatures("keymanager");

/// <summary>Key Manager Plus: опрос SSL-сертификатов и рассылка их файлов по подпискам.</summary>
public static class KeyManagerModule
{
    public static IMvcBuilder AddKeyManagerModule(
        this IMvcBuilder mvc, Action<KeyManagerModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new KeyManagerModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IEntityConfigurationProvider, KeyManagerTables>();
        services.AddModuleSchema(KeyManagerSchema.Declaration);
        services.AddSingleton<ISettingDeclarationProvider, KeyManagerSettingDeclarations>();
        services.AddSingleton<IMailTemplateProvider, CertificateMail>();
        services.AddSingleton<IAuditActionProvider, KeyManagerAuditActions>();

        // ⚠ Запечатанный секрет — вместе со своей целью ротации.
        services.AddScoped<ApiKeyStore>();
        services.AddScoped<IRewrapTarget, ApiKeyRewrap>();

        services.AddScoped<IKeyManagerConnector, KeyManagerConnector>();
        services.AddScoped<CertificateSnapshots>();
        services.AddScoped<CertificateDelivery>();
        services.AddScoped<CertificatePolling>();

        services.AddScoped<CertificateExport>();

        // Всегда: контроллер будит опрос и там, где он не поднят.
        services.AddSingleton<PollWakeup>();

        if (options.RunPoller)
        {
            services.AddHostedService<CertificatePoller>();
        }

        // Недостающее роняет старт, а не первое действие.
        const string модуль = "keymanager";
        services.Require<IMailQueue>(модуль, "файлы сертификатов некому отправить письмом");
        services.Require<ISettingsReader>(модуль, "адрес Key Manager Plus и срок опроса некому прочитать");
        services.Require<ISecretEnvelope>(модуль, "ключ API Key Manager Plus нечем запечатать");

        // Контроллеры модуля internal — их находит провайдер ядра.
        return mvc.AddModule(typeof(KeyManagerModule).Assembly, options.Features);
    }

    /// <summary>Привязать ключ API 1.x к текущему адресу KMP.</summary>
    /// <remarks>⚠ Только из <c>migrate</c>: доверие — адресу на момент обновления; идемпотентно.</remarks>
    /// <returns><c>true</c> — ключ привязан сейчас.</returns>
    public static async Task<bool> BindLegacyApiKeyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsReader>();
        var адрес = await settings.GetAsync(KeyManagerSettings.BaseUrl, cancellationToken);

        if (string.IsNullOrWhiteSpace(адрес))
        {
            return false;
        }

        return await scope.ServiceProvider.GetRequiredService<ApiKeyStore>()
            .BindLegacyAsync(ApiKeyStore.Target(адрес), cancellationToken);
    }
}
