using EmailSslSender.Modules.Settings.Api;
using EmailSslSender.Modules.Settings.Catalog;
using EmailSslSender.Modules.Settings.Reading;
using EmailSslSender.Modules.Settings.Settings;
using EmailSslSender.Modules.Settings.Storage;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Contracts.Settings;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Settings;

/// <summary>
/// Подключение модуля настроек.
/// </summary>
/// <remarks>
/// Расширение <c>IMvcBuilder</c>, а не <c>IServiceCollection</c>: регистрация своей сборки как
/// источника контроллеров живёт только здесь, а звать <c>AddControllers()</c> внутри модуля
/// значило бы, что порядок настройки MVC зависит от порядка подключения модулей.
///
/// ⚠ <b>Модуль не объявляет прав на этом подэтапе</b> — они приходят с эндпоинтами (16.7).
/// Состояние законное ровно до появления первого эндпоинта: правило 12 требует проверки на
/// эндпоинте, а не наличия прав у модуля.
///
/// ⚠ <b>На своём первом подэтапе модуль не делает ничего наружу</b> — ни эндпоинта, ни записи.
/// Справочник заводится раньше хранения намеренно: слою базы нужно знать, что такое ключ,
/// чтобы отличить «строки нет» от «такого ключа никто не объявлял», а без этого различия
/// опечатка в имени тихо превращается в «настройка не задана».
/// </remarks>
public static class SettingsModule
{
    /// <summary>Подключить настройки.</summary>
    public static IMvcBuilder AddSettingsModule(this IMvcBuilder mvc)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var services = mvc.Services;

        // Свои таблицы в общей модели. Хост о их составе не знает.
        services.AddSingleton<IEntityConfigurationProvider, SettingsEntities>();

        // Хранилище — область запроса: оно пишет в единицу работы, и другой у него быть не
        // должно. Фиксацией распоряжается вызывающий, вместе со строкой журнала (правило 15).
        services.AddScoped<SettingsRepository>();

        // ⚠ Читатель — ОБЛАСТЬ ЗАПРОСА, и это не кэш: снимок таблицы живёт ровно один запрос,
        // правка настройки действует со следующего. Настоящий кэш — 16.9, и он заводится
        // только по замеру (правило 18).
        services.AddScoped<SettingsReader>();
        services.AddScoped<ISettingsReader>(sp => sp.GetRequiredService<SettingsReader>());

        // Справочник — синглтон: объявления неизменны и собираются один раз на процесс.
        services.AddSingleton<SettingsCatalog>();

        // ⚠ И трогается ПРИ СТАРТЕ, а не лениво: иначе спор двух модулей за одно имя уронил бы
        // не выкатку, а первое открытие экрана настроек — у того, кто его открыл.
        services.AddHostedService<SettingsCatalogService>();

        // ⚠ «Какой язык у вот этого человека» — узкий контракт вместо параметра субъекта у
        // `ISettingsReader`: тот отвечает про ОБРАТИВШЕГОСЯ, а письмо уходит не ему. Разбор —
        // решение 0030.
        services.AddScoped<IUserLocale, UserLocale>();

        // Службы правки: область запроса, как и всё, что пишет в единицу работы.
        services.AddScoped<SettingsService>();

        // Свои три права и своё единственное событие журнала.
        services.AddSingleton<ISettingDeclarationProvider, SettingsSettingDeclarations>();

        // ⚠ Без этого `internal`-контроллеры не находятся, и получается 404 без единой
        // ошибки при старте. Провайдер смотрит только на свою сборку — разбор у него самого.
        mvc.ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(new AssemblyPart(typeof(SettingsModule).Assembly));
            parts.FeatureProviders.Add(new InternalControllerFeatureProvider());
        });

        return mvc;
    }
}
