using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Settings;
using EmailSslSender.Modules.KeyManager.Storage;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.KeyManager;

/// <summary>Подключение модуля Key Manager Plus.</summary>
public sealed class KeyManagerModuleOptions
{
    /// <summary>
    /// Поднимать ли фоновый опрос в этом процессе.
    /// </summary>
    /// <remarks>
    /// Решает хост по роли процесса, как <c>RunMaintenance</c> у почты. Два процесса с опросом
    /// друг другу не мешают — подписка отправляется под блокировкой строки, — но и пользы от
    /// второго нет.
    /// </remarks>
    public bool RunPoller { get; set; }
}

/// <summary>
/// Интеграция с ManageEngine Key Manager Plus: опрос SSL-сертификатов и рассылка их файлов по подпискам.
/// </summary>
/// <remarks>
/// ⚠ Модуль не трогает соседей: письма — через <see cref="IMailQueue"/>, настройки — через
/// <see cref="ISettingsReader"/>, таблицы и шаблоны — объявлениями, которые собирают хост и почта.
/// </remarks>
public static class KeyManagerModule
{
    /// <summary>Подключить модуль.</summary>
    public static IMvcBuilder AddKeyManagerModule(
        this IMvcBuilder mvc, Action<KeyManagerModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new KeyManagerModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;
        services.AddSingleton(options);

        services.AddSingleton<IEntityConfigurationProvider, KeyManagerTables>();
        services.AddSingleton<ISettingDeclarationProvider, KeyManagerSettingDeclarations>();
        services.AddSingleton<IMailTemplateProvider, CertificateMail>();

        // ⚠ Цель ротации регистрируется В ТОМ ЖЕ изменении, что и запечатывание ключа API.
        services.AddScoped<ApiKeyStore>();
        services.AddScoped<IRewrapTarget, ApiKeyRewrap>();

        services.AddScoped<IKeyManagerConnector, KeyManagerConnector>();
        services.AddScoped<CertificateSnapshots>();
        services.AddScoped<CertificateDelivery>();
        services.AddScoped<CertificatePolling>();

        // ⚠ Сигнал — синглтон и регистрируется всегда: контроллер будит опрос и там, где опрос
        // не поднят, — тогда сигнал просто никто не ждёт.
        services.AddSingleton<PollWakeup>();

        if (options.RunPoller)
        {
            services.AddHostedService<CertificatePoller>();
        }

        mvc.ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(
                new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(typeof(KeyManagerModule).Assembly));
            parts.FeatureProviders.Add(new Api.InternalControllerFeatureProvider());
        });

        return mvc;
    }
}
