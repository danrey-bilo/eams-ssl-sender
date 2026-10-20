using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Modules.Mail.Storage;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail;

/// <summary>
/// Подключение модуля почты.
/// </summary>
/// <remarks>
/// Почта установки: очередь писем в базе, отправка по SMTP (MailKit, режим защиты задаётся
/// явно: <c>tls</c> — SSL с первого байта, порт 465; <c>starttls</c> — 587; <c>none</c> — реле в
/// той же сети), шаблоны писем и запечатанный пароль SMTP.
///
/// ⚠ <b>Настройки SMTP живут в модуле настроек</b> (<c>mail.*</c>, все помечены настройками
/// защиты), а пароль — отдельно, запечатанным в <c>system_secrets</c>.
/// </remarks>
public sealed class MailModuleOptions
{
    /// <summary>
    /// Обслуживать ли уборку отправленных в этом процессе.
    /// </summary>
    /// <remarks>
    /// Решает хост по роли процесса. Модуль не должен знать, как эти роли называются, — иначе
    /// он начнёт зависеть от того, как устроено развёртывание.
    /// </remarks>
    public bool RunMaintenance { get; set; }
}

public static class MailModule
{
    /// <summary>Подключить почту.</summary>
    public static IMvcBuilder AddMailModule(
        this IMvcBuilder mvc, Action<MailModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new MailModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;
        services.AddSingleton(options);

        // Свои таблицы в общей модели. Хост о их составе не знает.
        services.AddSingleton<IEntityConfigurationProvider, MailEntities>();

        // Секреты установки и их ротация. ⚠ Цель ротации регистрируется В ТОМ ЖЕ подэтапе, что
        // и запечатывание: добавленная через месяц, она означает месяц, в течение которого
        // мастер-ключ нельзя вывести из обращения, и никто об этом не знает.
        services.AddScoped<SystemSecrets>();
        services.AddScoped<IRewrapTarget, SystemSecretsRewrap>();

        // Справочник писем — синглтон: тексты неизменны и собираются один раз на процесс.
        // ⚠ И трогается ПРИ СТАРТЕ: иначе спор за пару «вид + язык» или неполнота языков
        // уронили бы не выкатку, а первую отправку — то есть человека, нажавшего «сбросить
        // пароль».
        services.AddSingleton<MailTemplates>();
        services.AddHostedService<MailTemplatesService>();
        services.AddSingleton<IMailTemplateProvider, MailOwnTemplates>();

        // Постановка в очередь — область запроса: пишет в чужую единицу работы и не фиксирует
        // её (правило 15 у вызывающего).
        services.AddScoped<IMailQueue, MailQueue>();

        // Отправка и её насос. ⚠ Насос поднимается в ОБЕИХ ролях: захват порции атомарен
        // (`FOR UPDATE SKIP LOCKED`), два процесса делят очередь, а не повторяют друг друга —
        // зато письмо уходит и тогда, когда воркер не поднят.
        // ⚠ Сигнал — синглтон: насос один на процесс, и будят его отовсюду (этап 60.2).
        services.AddSingleton<MailWakeup>();
        services.AddScoped<IMailSender, SmtpSender>();
        services.AddScoped<MailDelivery>();
        services.AddHostedService<MailDeliveryService>();

        // ⚠ Уборка — единственное необратимое действие модуля, и поднимается она только в
        // обслуживающем процессе: роль решает хост, модуль не знает, как эти роли зовут.
        if (options.RunMaintenance)
        {
            services.AddHostedService<SentMailCleanup>();
        }

        // Свои настройки.
        services.AddSingleton<ISettingDeclarationProvider, MailSettingDeclarations>();

        // ⚠ Без этого `internal`-контроллеры не находятся, и получается 404 без единой
        // ошибки при старте. Провайдер смотрит только на свою сборку — разбор у него самого.
        mvc.ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(
                new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(typeof(MailModule).Assembly));
            parts.FeatureProviders.Add(new Api.InternalControllerFeatureProvider());
        });

        return mvc;
    }
}
