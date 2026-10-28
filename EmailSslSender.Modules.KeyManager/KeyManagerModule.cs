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
    /// <summary>
    /// Поднимать ли фоновый опрос в этом процессе.
    /// </summary>
    /// <remarks>
    /// Решает хост по роли процесса, как <c>RunMaintenance</c> у почты. Два процесса с опросом
    /// друг другу не мешают — подписка отправляется под блокировкой строки, — но и пользы от
    /// второго нет.
    /// </remarks>
    public bool RunPoller { get; set; }

    /// <summary>Переключатели модуля. Своих подвозможностей у модуля нет — модуль целиком.</summary>
    public KeyManagerModuleFeatures Features { get; } = new();

    /// <summary>
    /// Корневые сертификаты, которым соединение с Key Manager Plus доверяет вместо системных.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Только для тестов хоста</b>: их заглушка Key Manager Plus с самоподписанным сертификатом
    /// проходит ту же проверку цепочки, имени и отзыва. Рабочий хост корни не задаёт — сертификат
    /// проверяется системным хранилищем (свой удостоверяющий центр — <c>SSL_CERT_FILE</c>). Ни
    /// настройкой, ни через API это не задаётся.
    /// </remarks>
    public X509Certificate2Collection TrustedRoots { get; } = [];
}

/// <summary>Переключатели модуля Key Manager Plus (своих подвозможностей нет).</summary>
public sealed class KeyManagerModuleFeatures() : ModuleFeatures("keymanager");

/// <summary>
/// Интеграция с ManageEngine Key Manager Plus: опрос SSL-сертификатов и рассылка их файлов по подпискам.
/// </summary>
/// <remarks>
/// ⚠ Модуль проекта на фундаменте модулей EAMS: соседей не трогает — письма через
/// <see cref="IMailQueue"/>, настройки через <see cref="ISettingsReader"/>, журнал через
/// <see cref="IAuditWriter"/>; таблицы, настройки, письма и события — объявлениями, которые собирают
/// хост и модули. Чего не хватает при старте, называет проверка ядра (решение EAMS 0111).
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
        services.TryAddSingleton(TimeProvider.System);

        // Свои объявления: таблицы, набор миграций, настройки, письмо, события журнала.
        services.AddSingleton<IEntityConfigurationProvider, KeyManagerTables>();
        services.AddModuleSchema(KeyManagerSchema.Declaration);
        services.AddSingleton<ISettingDeclarationProvider, KeyManagerSettingDeclarations>();
        services.AddSingleton<IMailTemplateProvider, CertificateMail>();
        services.AddSingleton<IAuditActionProvider, KeyManagerAuditActions>();

        // ⚠ Цель ротации регистрируется В ТОМ ЖЕ изменении, что и запечатывание ключа API.
        services.AddScoped<ApiKeyStore>();
        services.AddScoped<IRewrapTarget, ApiKeyRewrap>();

        services.AddScoped<IKeyManagerConnector, KeyManagerConnector>();
        services.AddScoped<CertificateSnapshots>();
        services.AddScoped<CertificateDelivery>();
        services.AddScoped<CertificatePolling>();

        // Выгрузка сертификата и показ ключа администратору — в ответ, а не письмом.
        services.AddScoped<CertificateExport>();

        // ⚠ Сигнал — синглтон и регистрируется всегда: контроллер будит опрос и там, где опрос
        // не поднят, — тогда сигнал просто никто не ждёт.
        services.AddSingleton<PollWakeup>();

        if (options.RunPoller)
        {
            services.AddHostedService<CertificatePoller>();
        }

        // ⚠ Без этого модуль поднимался бы и падал первым действием, а не при старте.
        const string модуль = "keymanager";
        services.Require<IMailQueue>(модуль, "файлы сертификатов некому отправить письмом");
        services.Require<ISettingsReader>(модуль, "адрес Key Manager Plus и срок опроса некому прочитать");
        services.Require<ISecretEnvelope>(модуль, "ключ API Key Manager Plus нечем запечатать");

        // ⚠ Контроллеры модуля internal: находит их провайдер ядра, подключаемый здесь же.
        return mvc.AddModule(typeof(KeyManagerModule).Assembly, options.Features);
    }

    /// <summary>
    /// Привязать ключ API, заданный до привязки к адресу (1.x), к текущему адресу Key Manager Plus.
    /// </summary>
    /// <remarks>
    /// ⚠ Зовёт только команда <c>migrate</c> хоста: «доверие с первого раза» — тому адресу, что задан при
    /// обновлении, а не тому, что окажется в настройках при первом опросе. Адреса нет — привязывать не к
    /// чему: ключ откроется после ввода заново. Повторный вызов ничего не меняет.
    /// </remarks>
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
